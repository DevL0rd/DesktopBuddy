using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed unsafe class FfmpegAudioRtpPacketizer : IDisposable
{
    private const int PayloadType = 97;
    private const byte RtpChannel = 2;
    private const int RtpPacketSize = 1200;
    private const int AvioBufferSize = 4096;

    private readonly EncodedAudioStreamInfo _info;
    private readonly object _lock = new();

    private AVFormatContext* _fmtCtx;
    private AVIOContext* _ioCtx;
    private AVStream* _stream;
    private AVPacket* _packet;
    private avio_alloc_context_write_packet _writeCallback;
    private GCHandle _selfHandle;
    private bool _disposed;
    private bool _headerWritten;

    public event Action<RtpPacket> PacketReady;

    public FfmpegAudioRtpPacketizer(EncodedAudioStreamInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        Initialize();
    }

    public string BuildSdp(string controlUrl)
    {
        string ffmpegSdp = BuildFfmpegSdp();
        if (!string.IsNullOrWhiteSpace(ffmpegSdp))
            return EnsureControlLine(ffmpegSdp, controlUrl);

        return
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=DesktopBuddy\r\n" +
            "t=0 0\r\n" +
            "a=tool:DesktopBuddy\r\n" +
            "a=type:broadcast\r\n" +
            "a=control:*\r\n" +
            $"m=audio 0 RTP/AVP {PayloadType}\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            $"a=rtpmap:{PayloadType} MPEG4-GENERIC/{_info.SampleRate}/{_info.Channels}\r\n" +
            $"a=control:{controlUrl}\r\n";
    }

    public void Write(EncodedAudioPacket packet)
    {
        if (packet == null || packet.Data.Length == 0 || _disposed) return;

        lock (_lock)
        {
            if (_disposed || !_headerWritten) return;

            ffmpeg.av_packet_unref(_packet);
            int ret = ffmpeg.av_new_packet(_packet, packet.Data.Length);
            if (ret < 0)
            {
                Log.Msg($"[RTP:AAC] av_new_packet failed: {FfmpegError(ret)}");
                return;
            }

            Marshal.Copy(packet.Data, 0, (IntPtr)_packet->data, packet.Data.Length);
            _packet->pts = packet.Pts;
            _packet->dts = packet.Pts;
            _packet->duration = 0;
            _packet->stream_index = _stream->index;

            ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _packet);
            if (ret < 0)
                Log.Msg($"[RTP:AAC] av_interleaved_write_frame failed: {FfmpegError(ret)}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try { if (_fmtCtx != null) ffmpeg.av_write_trailer(_fmtCtx); } catch { }

            if (_packet != null)
            {
                var pkt = _packet;
                ffmpeg.av_packet_free(&pkt);
                _packet = null;
            }

            if (_fmtCtx != null)
            {
                if (_fmtCtx->pb != null)
                {
                    var pb = _fmtCtx->pb;
                    ffmpeg.avio_context_free(&pb);
                    _fmtCtx->pb = null;
                }

                ffmpeg.avformat_free_context(_fmtCtx);
                _fmtCtx = null;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }
    }

    private void Initialize()
    {
        FfmpegEncoder.SetFfmpegPath();

        _selfHandle = GCHandle.Alloc(this);
        _writeCallback = WritePacket;

        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "rtp", null);
        if (ret < 0 || fmtCtx == null)
            throw new InvalidOperationException($"avformat_alloc_output_context2(rtp audio) failed: {FfmpegError(ret)}");

        _fmtCtx = fmtCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        _fmtCtx->packet_size = RtpPacketSize;
        _fmtCtx->max_delay = 0;

        byte* ioBuffer = (byte*)ffmpeg.av_malloc(AvioBufferSize);
        _ioCtx = ffmpeg.avio_alloc_context(
            ioBuffer,
            AvioBufferSize,
            1,
            (void*)GCHandle.ToIntPtr(_selfHandle),
            null,
            _writeCallback,
            null);
        if (_ioCtx == null)
            throw new InvalidOperationException("avio_alloc_context failed for RTP audio packetizer");

        _fmtCtx->pb = _ioCtx;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null)
            throw new InvalidOperationException("avformat_new_stream failed for RTP audio packetizer");

        _stream->id = 1;
        _stream->time_base = new AVRational { num = 1, den = _info.SampleRate };
        _stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
        _stream->codecpar->codec_id = AVCodecID.AV_CODEC_ID_AAC;
        _stream->codecpar->sample_rate = _info.SampleRate;
        _stream->codecpar->ch_layout = new AVChannelLayout
        {
            order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE,
            nb_channels = _info.Channels,
            u = new AVChannelLayout_u { mask = _info.Channels == 1 ? ffmpeg.AV_CH_LAYOUT_MONO : ffmpeg.AV_CH_LAYOUT_STEREO }
        };

        if (_info.ExtraData.Length > 0)
        {
            _stream->codecpar->extradata = (byte*)ffmpeg.av_mallocz((ulong)_info.ExtraData.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            Marshal.Copy(_info.ExtraData, 0, (IntPtr)_stream->codecpar->extradata, _info.ExtraData.Length);
            _stream->codecpar->extradata_size = _info.ExtraData.Length;
        }

        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "payload_type", PayloadType.ToString(), 0);
        ffmpeg.av_dict_set(&opts, "rtpflags", "skip_rtcp", 0);
        ret = ffmpeg.avformat_write_header(_fmtCtx, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0)
            throw new InvalidOperationException($"avformat_write_header(rtp audio) failed: {FfmpegError(ret)}");

        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
            throw new InvalidOperationException("av_packet_alloc failed for RTP audio packetizer");

        _headerWritten = true;
        Log.Msg($"[RTP:AAC] Packetizer ready sampleRate={_info.SampleRate} channels={_info.Channels} extra={_info.ExtraData.Length}B");
    }

    private string BuildFfmpegSdp()
    {
        if (_fmtCtx == null) return null;

        const int SdpBufferSize = 4096;
        byte* buffer = stackalloc byte[SdpBufferSize];
        AVFormatContext** contexts = stackalloc AVFormatContext*[1];
        contexts[0] = _fmtCtx;

        int ret = ffmpeg.av_sdp_create(contexts, 1, buffer, SdpBufferSize);
        if (ret < 0)
        {
            Log.Msg($"[RTP:AAC] av_sdp_create failed: {FfmpegError(ret)}");
            return null;
        }

        return Marshal.PtrToStringAnsi((IntPtr)buffer);
    }

    private static string EnsureControlLine(string sdp, string controlUrl)
    {
        if (string.IsNullOrWhiteSpace(controlUrl))
            return sdp;

        string[] lines = sdp.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inMedia = false;
        bool replaced = false;
        var builder = new System.Text.StringBuilder();
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
                inMedia = true;

            if (inMedia && line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
            {
                if (replaced) continue;
                builder.Append("a=control:").Append(controlUrl).Append("\r\n");
                replaced = true;
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        if (!replaced)
            builder.Append("a=control:").Append(controlUrl).Append("\r\n");

        return builder.ToString();
    }

    private static int WritePacket(void* opaque, byte* buf, int bufSize)
    {
        if (bufSize <= 0) return 0;

        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        var packetizer = (FfmpegAudioRtpPacketizer)handle.Target;
        if (packetizer == null || packetizer._disposed) return bufSize;

        var data = new byte[bufSize];
        Marshal.Copy((IntPtr)buf, data, 0, bufSize);
        packetizer.PacketReady?.Invoke(new RtpPacket(data, 0, startsKeyframe: false, channel: RtpChannel));
        return bufSize;
    }

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }
}

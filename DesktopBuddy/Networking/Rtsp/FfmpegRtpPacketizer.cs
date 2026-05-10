using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed unsafe class FfmpegRtpPacketizer : IRtpPacketizer
{
    private const int PayloadType = 96;
    private const int RtpPacketSize = 1200;
    private const int AvioBufferSize = 4096;

    private readonly EncodedVideoStreamInfo _info;
    private readonly object _lock = new();

    private AVFormatContext* _fmtCtx;
    private AVIOContext* _ioCtx;
    private AVStream* _stream;
    private AVPacket* _packet;
    private avio_alloc_context_write_packet _writeCallback;
    private GCHandle _selfHandle;
    private bool _disposed;
    private bool _headerWritten;
    private bool _firstPacketAfterKeyframe;

    public event Action<RtpPacket> PacketReady;

    public FfmpegRtpPacketizer(EncodedVideoStreamInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        Initialize();
    }

    public string BuildSdp(string controlUrl)
    {
        string ffmpegSdp = BuildFfmpegSdp();
        if (!string.IsNullOrWhiteSpace(ffmpegSdp))
            return EnsureControlLine(ffmpegSdp, controlUrl);

        string codecName = _info.Codec switch
        {
            EncodedVideoCodec.Av1 => "AV1",
            EncodedVideoCodec.Hevc => "H265",
            EncodedVideoCodec.H264 => "H264",
            _ => "UNKNOWN"
        };

        string fmtp = _info.Codec == EncodedVideoCodec.H264
            ? $"a=fmtp:{PayloadType} packetization-mode=1\r\n"
            : "";

        return
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=DesktopBuddy\r\n" +
            "t=0 0\r\n" +
            "a=tool:DesktopBuddy\r\n" +
            "a=type:broadcast\r\n" +
            "a=control:*\r\n" +
            $"m=video 0 RTP/AVP {PayloadType}\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            $"a=rtpmap:{PayloadType} {codecName}/90000\r\n" +
            fmtp +
            $"a=framesize:{PayloadType} {_info.Width}-{_info.Height}\r\n" +
            $"a=control:{controlUrl}\r\n";
    }

    public void Write(EncodedVideoPacket packet)
    {
        if (packet == null || packet.Data.Length == 0 || _disposed) return;
        if (packet.Codec != _info.Codec) return;

        lock (_lock)
        {
            if (_disposed || !_headerWritten) return;

            ffmpeg.av_packet_unref(_packet);
            int ret = ffmpeg.av_new_packet(_packet, packet.Data.Length);
            if (ret < 0)
            {
                Log.Msg($"[RTP] av_new_packet failed: {FfmpegError(ret)}");
                return;
            }

            Marshal.Copy(packet.Data, 0, (IntPtr)_packet->data, packet.Data.Length);
            _packet->pts = packet.Pts90k;
            _packet->dts = packet.Pts90k;
            _packet->duration = 0;
            _packet->stream_index = _stream->index;
            if (packet.IsKeyframe)
            {
                _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
                _firstPacketAfterKeyframe = true;
            }

            ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _packet);
            if (ret < 0)
                Log.Msg($"[RTP] av_interleaved_write_frame failed for {_info.Codec}: {FfmpegError(ret)}");
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
            throw new InvalidOperationException($"avformat_alloc_output_context2(rtp) failed: {FfmpegError(ret)}");

        _fmtCtx = fmtCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        _fmtCtx->packet_size = RtpPacketSize;
        _fmtCtx->max_delay = 0;
        _fmtCtx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

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
            throw new InvalidOperationException("avio_alloc_context failed for RTP packetizer");

        _fmtCtx->pb = _ioCtx;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null)
            throw new InvalidOperationException("avformat_new_stream failed for RTP packetizer");

        _stream->id = 0;
        _stream->time_base = new AVRational { num = 1, den = 90000 };
        _stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        _stream->codecpar->codec_id = ToCodecId(_info.Codec);
        _stream->codecpar->width = _info.Width;
        _stream->codecpar->height = _info.Height;
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
            throw new InvalidOperationException($"avformat_write_header(rtp) failed for {_info.Codec}: {FfmpegError(ret)}");

        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
            throw new InvalidOperationException("av_packet_alloc failed for RTP packetizer");

        _headerWritten = true;
        Log.Msg($"[RTP] Packetizer ready codec={_info.Codec} size={_info.Width}x{_info.Height}");
    }

    private static AVCodecID ToCodecId(EncodedVideoCodec codec)
    {
        return codec switch
        {
            EncodedVideoCodec.Av1 => AVCodecID.AV_CODEC_ID_AV1,
            EncodedVideoCodec.Hevc => AVCodecID.AV_CODEC_ID_HEVC,
            EncodedVideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
            _ => AVCodecID.AV_CODEC_ID_NONE
        };
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
            Log.Msg($"[RTP] av_sdp_create failed for {_info.Codec}: {FfmpegError(ret)}");
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
        var packetizer = (FfmpegRtpPacketizer)handle.Target;
        if (packetizer == null || packetizer._disposed) return bufSize;

        var data = new byte[bufSize];
        Marshal.Copy((IntPtr)buf, data, 0, bufSize);
        bool startsKeyframe = packetizer._firstPacketAfterKeyframe;
        packetizer._firstPacketAfterKeyframe = false;
        packetizer.PacketReady?.Invoke(new RtpPacket(data, 0, startsKeyframe));
        return bufSize;
    }

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }
}

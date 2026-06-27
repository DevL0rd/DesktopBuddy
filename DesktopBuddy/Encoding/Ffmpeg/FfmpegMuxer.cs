using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private void SetupMuxer()
    {
        if (_rtspUrl != null)
        {
            SetupRtspMuxer();
            return;
        }

        _selfHandle = GCHandle.Alloc(this);

        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mpegts", null);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        byte* ioBuffer = (byte*)ffmpeg.av_malloc(AVIO_BUFFER_SIZE);
        _writeCallbackDelegate = WriteCallback;
        _ioCtx = ffmpeg.avio_alloc_context(
            ioBuffer, AVIO_BUFFER_SIZE,
            1,
            (void*)GCHandle.ToIntPtr(_selfHandle),
            null,
            _writeCallbackDelegate,
            null
        );
        if (_ioCtx == null) throw new Exception("avio_alloc_context failed");

        _fmtCtx->pb = _ioCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        _fmtCtx->max_delay = 0;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        if (_audioCapture != null && _audioCapture.IsCapturing)
        {
            SetupAudioStream();
        }

        AVDictionary* muxerOpts = null;
        ffmpeg.av_dict_set(&muxerOpts, "mpegts_flags", "pat_pmt_at_frames", 0);
        ffmpeg.av_dict_set(&muxerOpts, "flush_packets", "1", 0);
        ffmpeg.av_dict_set(&muxerOpts, "muxdelay", "0", 0);
        ffmpeg.av_dict_set(&muxerOpts, "muxpreload", "0", 0);
        ret = ffmpeg.avformat_write_header(_fmtCtx, &muxerOpts);
        ffmpeg.av_dict_free(&muxerOpts);
        if (ret < 0) throw new Exception($"avformat_write_header failed: {FfmpegError(ret)}");

        Log.Msg($"[FfmpegEnc:{_streamId}] MPEG-TS muxer ready (in-process, no external ffmpeg)");
    }

    private void SetupRtspMuxer()
    {
        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "rtsp", _rtspUrl);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 (rtsp) failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream (rtsp) failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        if (_audioCapture != null && _audioCapture.IsCapturing)
        {
            SetupAudioStream();
        }

        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
        ret = ffmpeg.avformat_write_header(_fmtCtx, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0) throw new Exception($"avformat_write_header (rtsp) failed: {FfmpegError(ret)}");

        Log.Msg($"[FfmpegEnc:{_streamId}] RTSP muxer ready: {_rtspUrl}");
    }

    private void RtspReconnect()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] RTSP broken, reconnecting in 3s...");
        Thread.Sleep(3000);
        if (_disposed) return;

        lock (_muxerLock)
        {
            try
            {
                if (_fmtCtx != null)
                {
                    try { ffmpeg.av_write_trailer(_fmtCtx); } catch { }
                    if (_fmtCtx->pb != null)
                    {
                        var pb = _fmtCtx->pb;
                        ffmpeg.avio_closep(&pb);
                        _fmtCtx->pb = null;
                    }
                    ffmpeg.avformat_free_context(_fmtCtx);
                    _fmtCtx = null;
                }
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP cleanup error: {ex.Message}");
                _fmtCtx = null;
            }

            _stream = null;
            _audioStream = null;

            try
            {
                SetupRtspMuxer();
                _rtspBroken = false;
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP reconnected");
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP reconnect failed: {ex.Message}");
            }
        }
    }

    private static int WriteCallback(void* opaque, byte* buf, int buf_size)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        var encoder = (FfmpegEncoder)handle.Target;
        return encoder.OnMpegTsData(buf, buf_size);
    }

    private int OnMpegTsData(byte* buf, int buf_size)
    {
        if (buf_size <= 0) return 0;

        lock (_ringLock)
        {
            int ringPos = (int)(_ringWritePos % RING_SIZE);
            int firstChunk = Math.Min(buf_size, RING_SIZE - ringPos);

            Marshal.Copy((IntPtr)buf, _ringBuffer, ringPos, firstChunk);
            if (firstChunk < buf_size)
                Marshal.Copy((IntPtr)(buf + firstChunk), _ringBuffer, 0, buf_size - firstChunk);

            _ringWritePos += buf_size;
        }

        try { _dataAvailable.Release(); }
        catch (SemaphoreFullException) { }

        return buf_size;
    }

}

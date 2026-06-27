using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    public void QueueFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_disposed || _initFailed) return;
        if (_initialized && ((width & ~1u) != _sourceWidth || (height & ~1u) != _sourceHeight))
        {
            if (_totalFrames == 0)
                Log.Msg($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch source={_sourceWidth}x{_sourceHeight} encode={_width}x{_height} frame={width}x{height}");
            return;
        }

        if (_initialized && _startTicks != 0)
        {
            double elapsedSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
            long videoPts = (long)(elapsedSec * 90000);
            if (videoPts <= Interlocked.Read(ref _lastVideoPts))
                return;
        }

        Marshal.AddRef(srcTexture);
        var prev = Interlocked.Exchange(ref _pendingTexture, srcTexture);
        if (prev != IntPtr.Zero) Marshal.Release(prev);
        _pendingWidth = width;
        _pendingHeight = height;
        _encodeEvent.Set();
    }

    private void EncodeLoop()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] Encode thread started");
        while (!_disposed)
        {
            if (_rtspBroken && _rtspUrl != null)
            {
                RtspReconnect();
                continue;
            }

            _encodeEvent.WaitOne(GetEncodeWaitMs());
            if (_disposed) break;

            var tex = Interlocked.Exchange(ref _pendingTexture, IntPtr.Zero);
            var w = _pendingWidth;
            var h = _pendingHeight;
            bool keepAliveFrame = false;

            if (tex == IntPtr.Zero)
            {
                if (_keepAliveTexture != IntPtr.Zero && IsKeepAliveDue(System.Diagnostics.Stopwatch.GetTimestamp()))
                {
                    Marshal.AddRef(_keepAliveTexture);
                    tex = _keepAliveTexture;
                    w = _keepAliveW;
                    h = _keepAliveH;
                    keepAliveFrame = true;
                }
                if (tex == IntPtr.Zero) continue;
            }

            try
            {
                EncodeFrameInternalLocked(tex, w, h, keepAliveFrame);
                long encodedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastEncodeTicks = encodedTicks;
                ScheduleNextKeepAlive(encodedTicks);

                var prev = _keepAliveTexture;
                Marshal.AddRef(tex);
                _keepAliveTexture = tex;
                _keepAliveW = w;
                _keepAliveH = h;
                if (prev != IntPtr.Zero)
                    WithD3dContextLock(() => ReleaseD3dTextureRef(prev, "previousKeepAliveTexture"));
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] Encode error (frame {_totalFrames}): {ex}");
            }
            finally
            {
                Marshal.Release(tex);
            }

        }
        var keepAliveTexture = _keepAliveTexture;
        _keepAliveTexture = IntPtr.Zero;
        if (keepAliveTexture != IntPtr.Zero)
            WithD3dContextLock(() => ReleaseD3dTextureRef(keepAliveTexture, "keepAliveTexture"));
        Log.Msg($"[FfmpegEnc:{_streamId}] Encode thread stopped");
    }

    private int GetEncodeWaitMs()
    {
        long nextTicks = Interlocked.Read(ref _nextKeepAliveTicks);
        int intervalMs = GetKeepAliveIntervalMs();
        if (nextTicks == 0 || _keepAliveTexture == IntPtr.Zero) return intervalMs;

        long remainingTicks = nextTicks - System.Diagnostics.Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) return 0;

        double remainingMs = (double)remainingTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return Math.Clamp((int)Math.Ceiling(remainingMs), 1, intervalMs);
    }

    private bool IsKeepAliveDue(long nowTicks)
    {
        long nextTicks = Interlocked.Read(ref _nextKeepAliveTicks);
        return nextTicks != 0 && nowTicks >= nextTicks;
    }

    private void ScheduleNextKeepAlive(long nowTicks)
    {
        long intervalTicks = System.Diagnostics.Stopwatch.Frequency / GetKeepAliveFps();
        Interlocked.Exchange(ref _nextKeepAliveTicks, nowTicks + intervalTicks);
    }

    private int GetKeepAliveFps()
    {
        return Math.Clamp(_keepAliveFps, 1, 240);
    }

    private int GetKeepAliveIntervalMs()
    {
        return Math.Max(1, (int)Math.Ceiling(1000.0 / GetKeepAliveFps()));
    }

    private void EncodeFrameInternalLocked(IntPtr srcTexture, uint width, uint height, bool keepAliveFrame)
    {
        int ret;
        try
        {
            if (_disposed) return;

            using (DesktopBuddyMod.Perf.Time("ffmpeg_get_buffer"))
            {
                ret = 0;
                WithD3dContextLock(() =>
                {
                    ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                });
                if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }
            }

            if (_needsVideoProcessor || _needsGpuScale)
            {
                bool converted = false;
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    WithD3dContextLock(() =>
                    {
                        converted = VideoProcessorConvert(srcTexture);
                        if (converted)
                        {
                            IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                            int dstIndex = (int)_hwFrame->data[1];
                            CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, _vpOutputTexture, (int)_width, (int)_height);
                        }
                    });
                }
                if (!converted)
                {
                    ffmpeg.av_frame_unref(_hwFrame);
                    return;
                }
            }
            else
            {
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    WithD3dContextLock(() =>
                    {
                        IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                        int dstIndex = (int)_hwFrame->data[1];
                        CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
                    });
                }
            }

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSec = (double)(nowTicks - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
            long videoPts = (long)(elapsedSec * 90000);
            if (videoPts <= Interlocked.Read(ref _lastVideoPts))
            {
                ffmpeg.av_frame_unref(_hwFrame);
                return;
            }
            Interlocked.Exchange(ref _lastVideoPts, videoPts);
            _hwFrame->pts = videoPts;
            _hwFrame->width = (int)_width;
            _hwFrame->height = (int)_height;
            _hwFrame->pict_type = _totalFrames == 0
                ? AVPictureType.AV_PICTURE_TYPE_I
                : AVPictureType.AV_PICTURE_TYPE_NONE;
            if (_totalFrames == 0)
                _hwFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;

            using (DesktopBuddyMod.Perf.Time("ffmpeg_encode"))
            {
                ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                ffmpeg.av_frame_unref(_hwFrame);
                if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_send_frame failed: {FfmpegError(ret)}"); return; }
            }

            using (DesktopBuddyMod.Perf.Time("ffmpeg_mux"))
            {
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_receive_packet failed: {FfmpegError(ret)}"); break; }

                    _pkt->stream_index = _stream->index;
                    ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _stream->time_base);

                    bool isKey = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    long httpKeyframeRingPos = -1;

                    lock (_muxerLock)
                    {
                        if (_rtspBroken) break;
                        if (isKey && _rtspUrl == null)
                        {
                            ffmpeg.avio_flush(_fmtCtx->pb);
                            httpKeyframeRingPos = _ringWritePos;
                        }
                        ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _pkt);
                        if (ret < 0)
                        {
                            Log.Msg($"[FfmpegEnc:{_streamId}] av_interleaved_write_frame (video) failed: {FfmpegError(ret)}");
                            if (_rtspUrl != null) _rtspBroken = true;
                        }
                        else if (httpKeyframeRingPos >= 0)
                        {
                            ffmpeg.avio_flush(_fmtCtx->pb);
                            Interlocked.Exchange(ref _lastKeyframeRingPos, httpKeyframeRingPos);
                        }
                    }

                    ffmpeg.av_packet_unref(_pkt);
                }
            }

            if (_rtspUrl == null)
            {
                lock (_muxerLock)
                {
                    ffmpeg.avio_flush(_fmtCtx->pb);
                }
            }
        }
        finally
        {
            if (_hwFrame != null)
                ffmpeg.av_frame_unref(_hwFrame);
        }

        _totalFrames++;
        if (keepAliveFrame) _keepAliveFramesEncoded++;
    }

    private static void CopyTextureToFrame(IntPtr deviceContext, IntPtr dstTexture, int dstArrayIndex, IntPtr srcTexture, int width, int height)
    {
        const int Ctx_CopySubresourceRegion = 46;

        var box = stackalloc uint[6];
        box[0] = 0; box[1] = 0; box[2] = 0;
        box[3] = (uint)width; box[4] = (uint)height; box[5] = 1;

        var vtable = *(IntPtr**)deviceContext;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)vtable[Ctx_CopySubresourceRegion];
        fn(deviceContext, dstTexture, (uint)dstArrayIndex, 0, 0, 0, srcTexture, 0, box);
    }

    private void WithD3dContextLock(Action action)
    {
        if (_d3dContextLock == null)
        {
            action();
            return;
        }

        lock (_d3dContextLock)
            action();
    }

}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder : IDisposable
{

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    private readonly string _rtspUrl;


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose === START ===");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose ENTER stream={_streamId} initialized={_initialized} disposed={_disposed}");
        _initialized = false;
        _disposed = true;
        try { _encodeEvent.Set(); } catch { }

        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose initThread.Join START stream={_streamId}");
        _initThread?.Join(5000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose initThread.Join DONE stream={_streamId}");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose audioThread.Join START stream={_streamId}");
        _audioEncodeThread?.Join(2000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose audioThread.Join DONE stream={_streamId}");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose encodeThread.Join START stream={_streamId}");
        _encodeThread?.Join(2000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose encodeThread.Join DONE stream={_streamId}");

        var ctxLock = _d3dContextLock;
        bool gotLock = false;
        if (ctxLock != null)
        {
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter START stream={_streamId}");
            gotLock = Monitor.TryEnter(ctxLock, 1000);
            if (!gotLock)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] WARNING: could not acquire D3D lock, skipping FFmpeg cleanup to avoid crash");
                Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter TIMEOUT stream={_streamId}");
                _fmtCtx = null; _codecCtx = null; _pkt = null; _hwFrame = null;
                return;
            }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter DONE stream={_streamId}");
        }
        try
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: writing trailer");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose trailer START stream={_streamId}");
            if (_fmtCtx != null)
            {
                try { ffmpeg.av_write_trailer(_fmtCtx); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: trailer error: {ex.Message}"); }
            }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose trailer DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing packets/frames");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose packets/frames START stream={_streamId}");
            try { if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: pkt free error: {ex.Message}"); _pkt = null; }
            try { if (_audioPkt != null) { var p = _audioPkt; ffmpeg.av_packet_free(&p); _audioPkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioPkt free error: {ex.Message}"); _audioPkt = null; }
            try { if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrame free error: {ex.Message}"); _hwFrame = null; }
            try { if (_audioFrame != null) { var f = _audioFrame; ffmpeg.av_frame_free(&f); _audioFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioFrame free error: {ex.Message}"); _audioFrame = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose packets/frames DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing VP resources");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose VP START stream={_streamId}");
            try
            {
                if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; }
                if (_vpOutputView != IntPtr.Zero) { Marshal.Release(_vpOutputView); _vpOutputView = IntPtr.Zero; }
                if (_vpInputTexture != IntPtr.Zero) { Marshal.Release(_vpInputTexture); _vpInputTexture = IntPtr.Zero; }
                if (_vpOutputTexture != IntPtr.Zero) { Marshal.Release(_vpOutputTexture); _vpOutputTexture = IntPtr.Zero; }
                if (_vpProcessor != IntPtr.Zero) { Marshal.Release(_vpProcessor); _vpProcessor = IntPtr.Zero; }
                if (_vpEnum != IntPtr.Zero) { Marshal.Release(_vpEnum); _vpEnum = IntPtr.Zero; }
                if (_vpContext != IntPtr.Zero) { Marshal.Release(_vpContext); _vpContext = IntPtr.Zero; }
                if (_vpDevice != IntPtr.Zero) { Marshal.Release(_vpDevice); _vpDevice = IntPtr.Zero; }
            }
            catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: VP cleanup error: {ex.Message}"); }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose VP DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing codec contexts");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose codec contexts START stream={_streamId}");
            try { if (_audioCodecCtx != null) { var c = _audioCodecCtx; ffmpeg.avcodec_free_context(&c); _audioCodecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioCodec free error: {ex.Message}"); _audioCodecCtx = null; }
            try { if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: codec free error: {ex.Message}"); _codecCtx = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose codec contexts DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing hw contexts");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose hw contexts START stream={_streamId}");
            try { if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrames free error: {ex.Message}"); _hwFramesCtx = null; }
            try { if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwDevice free error: {ex.Message}"); _hwDeviceCtx = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose hw contexts DONE stream={_streamId}");
        }
        finally
        {
            if (gotLock)
            {
                Monitor.Exit(ctxLock);
                Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D lock EXIT stream={_streamId}");
            }
        }
        _audioCapture = null;
        if (_keepAliveTexture != IntPtr.Zero) { try { Marshal.Release(_keepAliveTexture); } catch { } _keepAliveTexture = IntPtr.Zero; }

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing format context");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose format context START stream={_streamId}");
        try
        {
            if (_fmtCtx != null)
            {
                if (_rtspUrl != null)
                {
                    if (_fmtCtx->pb != null)
                    {
                        var pb = _fmtCtx->pb;
                        ffmpeg.avio_closep(&pb);
                        _fmtCtx->pb = null;
                    }
                }
                else if (_fmtCtx->pb != null)
                {
                    var pb = _fmtCtx->pb;
                    ffmpeg.avio_context_free(&pb);
                    _fmtCtx->pb = null;
                }
                ffmpeg.avformat_free_context(_fmtCtx);
                _fmtCtx = null;
            }
        }
        catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: fmtCtx free error: {ex.Message}"); _fmtCtx = null; }
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose format context DONE stream={_streamId}");

        if (_selfHandle.IsAllocated) _selfHandle.Free();

        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose events START stream={_streamId}");
        try { _dataAvailable.Dispose(); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: dataAvailable dispose error: {ex.Message}"); }
        try { _encodeEvent.Dispose(); } catch { }
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose events DONE stream={_streamId}");

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose === DONE === {_totalFrames} total frames");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose EXIT stream={_streamId} totalFrames={_totalFrames}");
    }
}

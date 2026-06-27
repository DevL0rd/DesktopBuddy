using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuddy;

internal sealed class LinuxNativeStreamSource : ILiveStreamSource
{
    private const int AudioSampleRate = 48000;
    private const int AudioChannels = 2;

    private readonly int _streamId;
    private readonly string _rtspUrl;
    private readonly LinuxNativeBridge _bridge = new();
    private ulong _nativeStreamId;
    private ulong _captureId;
    private ulong _audioCaptureId;
    private Thread _captureThread;
    private Thread _audioThread;
    private volatile bool _running;
    private int _disposed;
    private uint _nodeId;

    public bool IsRunning
    {
        get
        {
            var info = GetInfo();
            return _running && info.Running != 0 && info.Broken == 0;
        }
    }
    public long CurrentWritePosition => GetInfo().WritePos;

    public bool IsSourceAlive => _captureId == 0 || _bridge.IsCaptureAlive(_captureId);

    public string ReadableStreamState
    {
        get
        {
            var info = GetInfo();
            return $"native running={_running} native={_nativeStreamId} node={_nodeId} mode={(_rtspUrl == null ? "mpegts" : "rtsp")} size={info.Width}x{info.Height} broken={info.Broken} keyframe={info.KeyframePos} writePos={info.WritePos} frames={info.Frames}";
        }
    }

    internal LinuxNativeStreamSource(int streamId, string rtspUrl = null)
    {
        _streamId = streamId;
        _rtspUrl = rtspUrl;
    }

    internal bool Start(uint pipeWireNodeId)
    {
        if (_running) return true;
        if (pipeWireNodeId == 0)
        {
            Log.Msg($"[LinuxNativeStream:{_streamId}] Cannot start: missing PipeWire node id");
            return false;
        }

        _nodeId = pipeWireNodeId;
        int status = _bridge.StartStream(
            pipeWireNodeId,
            DesktopBuddyMod.RuntimeStreamFps,
            DesktopBuddyMod.RuntimeBitrateMbps,
            DesktopBuddyMod.RuntimeMaxStreamResolution,
            WgcCapture.RendererAdapterHintVendorId,
            DesktopBuddyMod.RuntimeEncoderPreference,
            _rtspUrl,
            audioEnabled: true,
            AudioSampleRate,
            AudioChannels,
            out _nativeStreamId);
        if (status != 0 || _nativeStreamId == 0)
        {
            string detail = _bridge.GetStreamLastError();
            Log.Msg($"[LinuxNativeStream:{_streamId}] Native stream start failed status={status} node={pipeWireNodeId}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" error={detail}")}");
            return false;
        }

        status = _bridge.StartCapture(pipeWireNodeId, out _captureId);
        if (status != 0 || _captureId == 0)
        {
            Log.Msg($"[LinuxNativeStream:{_streamId}] Native capture start failed status={status} node={pipeWireNodeId}");
            _bridge.StopStream(_nativeStreamId);
            _nativeStreamId = 0;
            return false;
        }

        _running = true;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = $"DesktopBuddy:LinuxStream:{_streamId}",
            IsBackground = true
        };
        _captureThread.Start();

        int audioStatus = _bridge.StartAudioCapture(out _audioCaptureId);
        if (audioStatus == 0 && _audioCaptureId != 0)
        {
            _audioThread = new Thread(AudioLoop)
            {
                Name = $"DesktopBuddy:LinuxAudio:{_streamId}",
                IsBackground = true
            };
            _audioThread.Start();
            Log.Msg($"[LinuxNativeStream:{_streamId}] Desktop audio capture started capture={_audioCaptureId}");
        }
        else
        {
            Log.Msg($"[LinuxNativeStream:{_streamId}] Desktop audio capture unavailable status={audioStatus}; continuing video-only");
            _audioCaptureId = 0;
        }

        Log.Msg($"[LinuxNativeStream:{_streamId}] Native stream started node={pipeWireNodeId} native={_nativeStreamId} mode={(_rtspUrl == null ? "mpegts" : "rtsp")}");
        return true;
    }

    private void AudioLoop()
    {
        var buffer = new float[8192];
        while (_running && _nativeStreamId != 0 && _audioCaptureId != 0)
        {
            int floats = _bridge.PollAudio(_audioCaptureId, buffer);
            if (floats <= 0)
            {
                Thread.Sleep(4);
                continue;
            }

            int frames = floats / AudioChannels;
            if (frames > 0)
                _bridge.PushStreamAudio(_nativeStreamId, buffer, frames);
        }
    }

    private void CaptureLoop()
    {
        int pushed = 0;
        int failures = 0;
        while (_running && _nativeStreamId != 0 && _captureId != 0)
        {
            int status = _bridge.PollFrame(_captureId, out var frame);
            if (status == 1)
            {
                Thread.Sleep(2);
                continue;
            }
            if (status != 0 || frame.Fd < 0)
            {
                Thread.Sleep(4);
                continue;
            }

            try
            {
                int pushStatus = _bridge.PushStreamFrame(_nativeStreamId, frame);
                if (pushStatus == 0)
                {
                    pushed++;
                    if (pushed == 1 || pushed % 120 == 0)
                        Log.Msg($"[LinuxNativeStream:{_streamId}] Encoded frames={pushed} {frame.Width}x{frame.Height}");
                }
                else if (++failures == 1 || failures % 60 == 0)
                {
                    Log.Msg($"[LinuxNativeStream:{_streamId}] Frame push failed status={pushStatus} failures={failures}");
                }
            }
            finally
            {
                _bridge.CloseFrame(frame);
            }
        }
    }

    public bool HasReadableVideoKeyframeAtOrAfter(long minimumKeyframePos)
    {
        var info = GetInfo();
        if (_rtspUrl != null)
            return _running && info.Running != 0 && info.Broken == 0;
        return _running &&
            info.Running != 0 &&
            info.Broken == 0 &&
            info.KeyframePos >= minimumKeyframePos &&
            info.KeyframePos >= 0 &&
            info.KeyframePos < info.WritePos;
    }

    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool keyframeAligned)
    {
        keyframeAligned = aligned;
        if (_rtspUrl != null || !_running)
            return 0;
        return _bridge.ReadStream(_nativeStreamId, buffer, ref readPos, ref aligned, minimumKeyframePos, out keyframeAligned);
    }

    public Task WaitForDataAsync(int milliseconds)
    {
        return Task.Delay(milliseconds);
    }

    public string GetReaderDiagnostics(long readPos, bool aligned)
    {
        var info = GetInfo();
        return $"readPos={readPos} writePos={info.WritePos} backlog={info.WritePos - readPos} aligned={aligned} latestKeyframe={info.KeyframePos} frames={info.Frames}";
    }

    public void Stop()
    {
        _running = false;
        try { _audioThread?.Join(1000); } catch { }
        _audioThread = null;
        try { _bridge.StopAudioCapture(_audioCaptureId); } catch { }
        _audioCaptureId = 0;
        try { _captureThread?.Join(1000); } catch { }
        _captureThread = null;
        try { _bridge.StopCapture(_captureId); } catch { }
        _captureId = 0;
        try { _bridge.StopStream(_nativeStreamId); } catch { }
        _nativeStreamId = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _bridge.Dispose();
    }

    private DbLinuxStreamInfo GetInfo()
    {
        try { return _bridge.GetStreamInfo(_nativeStreamId); }
        catch { return default; }
    }
}

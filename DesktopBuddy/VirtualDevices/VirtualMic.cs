using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBuddy;

/// <summary>
/// Renders audio to VB-Cable's "CABLE Input" device via WASAPI,
/// making it available as a virtual microphone on "CABLE Output".
/// Reads from an AudioListener for spatial in-game audio,
/// or from an AudioCapture for desktop window audio.
/// </summary>
internal sealed class VirtualMic : IDisposable
{
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    // Vtable indices — matched to AudioCapture.cs (proven working)
    private const int AudioClient_Initialize = 3;
    private const int AudioClient_GetBufferSize = 4;
    private const int AudioClient_GetCurrentPadding = 6;
    private const int AudioClient_Start = 10;
    private const int AudioClient_Stop = 11;
    private const int AudioClient_GetService = 14;

    private const int RenderClient_GetBuffer = 3;
    private const int RenderClient_ReleaseBuffer = 4;

    private IntPtr _audioClient;
    private IntPtr _renderClient;
    private uint _bufferFrameCount;
    private Thread _renderThread;
    private volatile bool _disposed;

    // Audio source — either desktop capture or in-game listener ring buffer
    private AudioCapture _desktopSource;
    private long _desktopReadPos;

    // In-game audio ring buffer — written by AudioListener callback, read by render thread
    private float[] _gameAudioRing;
    private long _gameAudioWritePos;
    private long _gameAudioReadPos;
    private readonly object _gameAudioLock = new();
    private const int GAME_RING_SAMPLES = 48000 * 2 * 2; // 2 seconds stereo

    private float[] _scratch;

    internal bool IsActive => _renderClient != IntPtr.Zero;
    internal volatile bool Muted;

    internal unsafe bool Start()
    {
        try
        {
        // Ensure COM is initialized on this thread
        CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED

        string deviceId = VBCableSetup.FindCableInputDeviceId();
        if (deviceId == null)
        {
            Log.Msg("[VirtualMic] CABLE Input device not found");
            return false;
        }

        Log.Msg($"[VirtualMic] Opening device: {deviceId}");
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr enumerator);
        if (hr < 0) { Log.Msg($"[VirtualMic] CoCreateInstance failed: 0x{hr:X8}"); return false; }
        Log.Msg("[VirtualMic] MMDeviceEnumerator created");

        try
        {
            var enumVt = *(IntPtr**)enumerator;
            IntPtr deviceIdPtr = Marshal.StringToCoTaskMemUni(deviceId);
            var getDeviceFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)enumVt[5];
            hr = getDeviceFn(enumerator, deviceIdPtr, out IntPtr device);
            Marshal.FreeCoTaskMem(deviceIdPtr);
            if (hr < 0 || device == IntPtr.Zero) { Log.Msg($"[VirtualMic] GetDevice failed: 0x{hr:X8}"); return false; }
            Log.Msg("[VirtualMic] Device opened");

            try
            {
                var devVt = *(IntPtr**)device;
                var activateFn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, out IntPtr, int>)devVt[3];
                var audioClientIid = IID_IAudioClient;
                hr = activateFn(device, &audioClientIid, 1, IntPtr.Zero, out _audioClient);
                if (hr < 0 || _audioClient == IntPtr.Zero) { Log.Msg($"[VirtualMic] Activate IAudioClient failed: 0x{hr:X8}"); return false; }
                Log.Msg($"[VirtualMic] IAudioClient activated: 0x{_audioClient:X}");
            }
            finally { Marshal.Release(device); }
        }
        finally { Marshal.Release(enumerator); }

        // 48kHz, 32-bit float, stereo (WAVEFORMATEXTENSIBLE)
        var wfx = stackalloc byte[40];
        *(short*)(wfx + 0) = unchecked((short)0xFFFE);
        *(short*)(wfx + 2) = 2;
        *(int*)(wfx + 4) = 48000;
        *(int*)(wfx + 8) = 48000 * 2 * 4;
        *(short*)(wfx + 12) = 8;
        *(short*)(wfx + 14) = 32;
        *(short*)(wfx + 16) = 22;
        *(short*)(wfx + 18) = 32;
        *(int*)(wfx + 20) = 3;
        *(Guid*)(wfx + 24) = new Guid("00000003-0000-0010-8000-00aa00389b71");

        {
            var acVt = *(IntPtr**)_audioClient;
            var initFn = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, byte*, IntPtr, int>)acVt[AudioClient_Initialize];
            hr = initFn(_audioClient, 0, 0, 200000, 0, wfx, IntPtr.Zero);
            if (hr < 0) { Log.Msg($"[VirtualMic] IAudioClient::Initialize failed: 0x{hr:X8}"); Cleanup(); return false; }
            Log.Msg("[VirtualMic] IAudioClient initialized");

            var getBufFn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)acVt[AudioClient_GetBufferSize];
            hr = getBufFn(_audioClient, out _bufferFrameCount);
            if (hr < 0) { Log.Msg($"[VirtualMic] GetBufferSize failed: 0x{hr:X8}"); Cleanup(); return false; }
            Log.Msg($"[VirtualMic] Buffer size: {_bufferFrameCount} frames");

            var getSvcFn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, int>)acVt[AudioClient_GetService];
            var renderIid = IID_IAudioRenderClient;
            hr = getSvcFn(_audioClient, &renderIid, out _renderClient);
            if (hr < 0 || _renderClient == IntPtr.Zero) { Log.Msg($"[VirtualMic] GetService IAudioRenderClient failed: 0x{hr:X8}"); Cleanup(); return false; }
            Log.Msg($"[VirtualMic] IAudioRenderClient: 0x{_renderClient:X}");

            var startFn = (delegate* unmanaged[Stdcall]<IntPtr, int>)acVt[AudioClient_Start];
            hr = startFn(_audioClient);
            if (hr < 0) { Log.Msg($"[VirtualMic] IAudioClient::Start failed: 0x{hr:X8}"); Cleanup(); return false; }
            Log.Msg("[VirtualMic] IAudioClient started");
        }

        _gameAudioRing = new float[GAME_RING_SAMPLES];
        _gameAudioWritePos = 0;
        _gameAudioReadPos = 0;
        _scratch = new float[_bufferFrameCount * 2];
        _renderThread = new Thread(RenderLoop) { Name = "VirtualMic:Render", IsBackground = true };
        _renderThread.Start();

        Log.Msg($"[VirtualMic] Started: 48kHz float32 stereo, buffer={_bufferFrameCount} frames");
        return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualMic] Start failed: {ex}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Called from the audio system to write spatial audio samples into the ring buffer.
    /// Thread-safe — can be called from the audio render thread.
    /// </summary>
    internal void WriteGameAudio(Span<float> samples)
    {
        if (_disposed || Muted || samples.Length == 0) return;

        lock (_gameAudioLock)
        {
            int ringSize = _gameAudioRing.Length;
            int toWrite = Math.Min(samples.Length, ringSize);
            int offset = (int)(_gameAudioWritePos % ringSize);
            int first = Math.Min(toWrite, ringSize - offset);

            samples.Slice(0, first).CopyTo(_gameAudioRing.AsSpan(offset, first));
            if (first < toWrite)
                samples.Slice(first, toWrite - first).CopyTo(_gameAudioRing.AsSpan(0, toWrite - first));

            _gameAudioWritePos += toWrite;
        }
    }

    private int ReadGameAudio(float[] output, int maxSamples)
    {
        lock (_gameAudioLock)
        {
            long available = _gameAudioWritePos - _gameAudioReadPos;
            if (available <= 0) return 0;
            if (available > _gameAudioRing.Length)
            {
                _gameAudioReadPos = _gameAudioWritePos - _gameAudioRing.Length;
                available = _gameAudioRing.Length;
            }

            int toRead = (int)Math.Min(available, maxSamples);
            int ringSize = _gameAudioRing.Length;
            int offset = (int)(_gameAudioReadPos % ringSize);
            int first = Math.Min(toRead, ringSize - offset);
            Array.Copy(_gameAudioRing, offset, output, 0, first);
            if (first < toRead)
                Array.Copy(_gameAudioRing, 0, output, first, toRead - first);
            _gameAudioReadPos += toRead;
            return toRead;
        }
    }

    private unsafe void RenderLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(10);
            if (_disposed || _renderClient == IntPtr.Zero) break;

            try
            {
                var acVt = *(IntPtr**)_audioClient;
                var getPaddingFn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)acVt[AudioClient_GetCurrentPadding];
                getPaddingFn(_audioClient, out uint padding);

                uint available = _bufferFrameCount - padding;
                if (available == 0) continue;

                int samplesNeeded = (int)available * 2;
                int read = ReadGameAudio(_scratch, samplesNeeded);

                if (read <= 0 || Muted)
                {
                    // Write silence to keep WASAPI happy
                    var rcVt = *(IntPtr**)_renderClient;
                    var getBufFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)rcVt[RenderClient_GetBuffer];
                    int hr = getBufFn(_renderClient, available, out IntPtr bufPtr);
                    if (hr >= 0)
                    {
                        // AUDCLNT_BUFFERFLAGS_SILENT = 2
                        var releaseFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)rcVt[RenderClient_ReleaseBuffer];
                        releaseFn(_renderClient, available, 2);
                    }
                    continue;
                }

                uint framesToWrite = (uint)(read / 2);

                var rcVt2 = *(IntPtr**)_renderClient;
                var getBuf2 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)rcVt2[RenderClient_GetBuffer];
                int hr2 = getBuf2(_renderClient, framesToWrite, out IntPtr bufPtr2);
                if (hr2 < 0) continue;

                fixed (float* src = _scratch)
                {
                    Buffer.MemoryCopy(src, (void*)bufPtr2, framesToWrite * 2 * 4, read * 4);
                }

                var release2 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)rcVt2[RenderClient_ReleaseBuffer];
                release2(_renderClient, framesToWrite, 0);
            }
            catch (Exception ex)
            {
                Log.Msg($"[VirtualMic] Render error: {ex.Message}");
            }
        }
    }

    private unsafe void Cleanup()
    {
        if (_audioClient != IntPtr.Zero)
        {
            try
            {
                var acVt = *(IntPtr**)_audioClient;
                var stopFn = (delegate* unmanaged[Stdcall]<IntPtr, int>)acVt[AudioClient_Stop];
                stopFn(_audioClient);
            }
            catch { }
        }
        if (_renderClient != IntPtr.Zero) { Marshal.Release(_renderClient); _renderClient = IntPtr.Zero; }
        if (_audioClient != IntPtr.Zero) { Marshal.Release(_audioClient); _audioClient = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderThread?.Join(2000);
        Cleanup();
        _scratch = null;
        _gameAudioRing = null;
        Log.Msg("[VirtualMic] Disposed");
    }
}

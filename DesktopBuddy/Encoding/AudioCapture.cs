using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBuddy;

public enum AudioCaptureMode
{
    IncludeProcess,
    ExcludeProcess
}

public sealed class AudioCapture : IDisposable
{
    internal static Action<string> LogHandler = Console.WriteLine;
    internal static void Log(string msg) => LogHandler(msg);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);

    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    private const int AC_Initialize = 3;
    private const int AC_Start = 10;
    private const int AC_Stop = 11;
    private const int AC_SetEventHandle = 13;
    private const int AC_GetService = 14;

    private const int ACC_GetBuffer = 3;
    private const int ACC_ReleaseBuffer = 4;
    private const int ACC_GetNextPacketSize = 5;

    private float[] _audioBuffer;
    private long _writePos;
    public long WritePosition => Volatile.Read(ref _writePos);
    private const int AUDIO_RING_SAMPLES = 48000 * 2 * 2;

    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private IntPtr _captureEvent;
    private Thread _captureThread;
    private volatile bool _disposed;

    public int SampleRate => 48000;
    public int Channels => 2;
    public int BitsPerSample => 32;
    public bool IsCapturing => _audioClient != IntPtr.Zero && !_disposed;

    public bool Start(IntPtr hwnd, AudioCaptureMode mode)
    {
        try
        {
            uint targetPid;
            int loopbackMode;

            if (mode == AudioCaptureMode.IncludeProcess)
            {
                GetWindowThreadProcessId(hwnd, out targetPid);
                loopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
                Log($"[AudioCapture] Capturing audio for PID {targetPid} (window 0x{hwnd:X})");
            }
            else
            {
                targetPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                loopbackMode = PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;
                Log($"[AudioCapture] Capturing all audio except PID {targetPid} (Resonite)");
            }

            var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = targetPid,
                    ProcessLoopbackMode = loopbackMode
                }
            };

            int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
            IntPtr paramsPtr = Marshal.AllocCoTaskMem(paramsSize);
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            IntPtr propVariant = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, 0x41);
            Marshal.WriteInt32(propVariant, 8, paramsSize);
            Marshal.WriteIntPtr(propVariant, 16, paramsPtr);

            var completionHandler = new ActivationHandler();
            var iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                ref iid,
                propVariant,
                completionHandler,
                out _);

            if (hr < 0) { Log($"[AudioCapture] ActivateAudioInterfaceAsync failed: 0x{hr:X8}"); return false; }

            if (!completionHandler.WaitForCompletion(5000))
            {
                Log("[AudioCapture] Activation timed out");
                return false;
            }

            Marshal.FreeCoTaskMem(propVariant);
            Marshal.FreeCoTaskMem(paramsPtr);

            if (completionHandler.HResult < 0 || completionHandler.Result == null)
            {
                Log($"[AudioCapture] Activation failed: 0x{completionHandler.HResult:X8}");
                return false;
            }

            _audioClient = Marshal.GetIUnknownForObject(completionHandler.Result);
            Log($"[AudioCapture] IAudioClient activated: 0x{_audioClient:X}");

            var captureFormat = new WAVEFORMATEX
            {
                wFormatTag = 3,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = 48000 * 8,
                cbSize = 0
            };

            IntPtr formatPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(captureFormat, formatPtr, false);

            hr = AudioClientInitialize(_audioClient,
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                0, 0, formatPtr, IntPtr.Zero);
            Marshal.FreeCoTaskMem(formatPtr);

            if (hr < 0) { Log($"[AudioCapture] Initialize failed: 0x{hr:X8}"); return false; }
            Log("[AudioCapture] IAudioClient initialized (event mode)");

            _captureEvent = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            hr = AudioClientSetEventHandle(_audioClient, _captureEvent);
            if (hr < 0) { Log($"[AudioCapture] SetEventHandle failed: 0x{hr:X8}"); return false; }
            Log("[AudioCapture] WASAPI event handle set");

            hr = AudioClientGetService(_audioClient, IID_IAudioCaptureClient, out _captureClient);
            if (hr < 0) { Log($"[AudioCapture] GetService failed: 0x{hr:X8}"); return false; }
            Log($"[AudioCapture] IAudioCaptureClient: 0x{_captureClient:X}");

            _audioBuffer = new float[AUDIO_RING_SAMPLES];
            _writePos = 0;

            hr = AudioClientStart(_audioClient);
            if (hr < 0) { Log($"[AudioCapture] Start failed: 0x{hr:X8}"); return false; }

            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "AudioCapture" };
            _captureThread.Start();

            Log($"[AudioCapture] Started: 48000Hz float32 stereo");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[AudioCapture] Start failed: {ex}");
            return false;
        }
    }

    private void CaptureLoop()
    {
        while (!_disposed)
        {
            WaitForSingleObject(_captureEvent, 100);
            if (_disposed) break;
            try
            {
                DrainCaptureBuffer();
            }
            catch (Exception ex)
            {
                if (!_disposed) Log($"[AudioCapture] Capture error: {ex.Message}");
            }
        }
    }

    private unsafe void DrainCaptureBuffer()
    {
        uint packetSize;
        int hr = CaptureClientGetNextPacketSize(_captureClient, out packetSize);
        if (hr < 0) return;

        while (packetSize > 0)
        {
            hr = CaptureClientGetBuffer(_captureClient, out IntPtr data, out uint numFrames, out uint flags, out _, out _);
            if (hr < 0) break;

            if (numFrames > 0)
            {
                bool silent = (flags & 0x2) != 0;
                int sampleCount = (int)numFrames * Channels;

                int ringSize = _audioBuffer.Length;
                long wp = _writePos;
                int offset = (int)(wp % ringSize);
                if (silent)
                {
                    int first = Math.Min(sampleCount, ringSize - offset);
                    Array.Clear(_audioBuffer, offset, first);
                    if (first < sampleCount)
                        Array.Clear(_audioBuffer, 0, sampleCount - first);
                }
                else
                {
                    float* src = (float*)data;
                    int first = Math.Min(sampleCount, ringSize - offset);
                    fixed (float* dst = &_audioBuffer[offset])
                        Buffer.MemoryCopy(src, dst, first * sizeof(float), first * sizeof(float));
                    if (first < sampleCount)
                    {
                        fixed (float* dst = &_audioBuffer[0])
                            Buffer.MemoryCopy(src + first, dst, (sampleCount - first) * sizeof(float), (sampleCount - first) * sizeof(float));
                    }
                }
                Volatile.Write(ref _writePos, wp + sampleCount);
            }

            CaptureClientReleaseBuffer(_captureClient, numFrames);
            hr = CaptureClientGetNextPacketSize(_captureClient, out packetSize);
            if (hr < 0) break;
        }
    }

    public int ReadSamples(float[] output, int maxSamples, ref long readPos)
    {
        long writePos = Volatile.Read(ref _writePos);
        long available = writePos - readPos;
        if (available <= 0) return 0;
        if (available > _audioBuffer.Length)
        {
            readPos = writePos - _audioBuffer.Length;
            available = _audioBuffer.Length;
        }

        int toRead = (int)Math.Min(available, maxSamples);
        int ringSize = _audioBuffer.Length;
        int offset = (int)(readPos % ringSize);
        int first = Math.Min(toRead, ringSize - offset);
        Array.Copy(_audioBuffer, offset, output, 0, first);
        if (first < toRead)
            Array.Copy(_audioBuffer, 0, output, first, toRead - first);
        readPos += toRead;
        return toRead;
    }

    public int ReadSamples(Span<float> output, ref long readPos)
    {
        long writePos = Volatile.Read(ref _writePos);
        long available = writePos - readPos;
        if (available <= 0) return 0;
        if (available > _audioBuffer.Length)
        {
            readPos = writePos - _audioBuffer.Length;
            available = _audioBuffer.Length;
        }

        int toRead = (int)Math.Min(available, output.Length);
        int ringSize = _audioBuffer.Length;
        int offset = (int)(readPos % ringSize);
        int first = Math.Min(toRead, ringSize - offset);
        _audioBuffer.AsSpan(offset, first).CopyTo(output);
        if (first < toRead)
            _audioBuffer.AsSpan(0, toRead - first).CopyTo(output.Slice(first));
        readPos += toRead;
        return toRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_audioClient != IntPtr.Zero) try { AudioClientStop(_audioClient); } catch (Exception ex) { DesktopBuddy.Log.Msg($"[AudioCapture] AudioClientStop error: {ex.Message}"); }
        _captureThread?.Join(1000);

        if (_captureClient != IntPtr.Zero) { Marshal.Release(_captureClient); _captureClient = IntPtr.Zero; }
        if (_audioClient != IntPtr.Zero) { Marshal.Release(_audioClient); _audioClient = IntPtr.Zero; }
        if (_captureEvent != IntPtr.Zero) { CloseHandle(_captureEvent); _captureEvent = IntPtr.Zero; }

        Log("[AudioCapture] Disposed");
    }

    private static unsafe int AudioClientInitialize(IntPtr client, int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr pFormat, IntPtr sessionGuid)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, IntPtr, IntPtr, int>)vtable[AC_Initialize];
        return fn(client, shareMode, streamFlags, bufferDuration, periodicity, pFormat, sessionGuid);
    }

    private static unsafe int AudioClientSetEventHandle(IntPtr client, IntPtr eventHandle)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[AC_SetEventHandle];
        return fn(client, eventHandle);
    }

    private static unsafe int AudioClientStart(IntPtr client)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[AC_Start];
        return fn(client);
    }

    private static unsafe int AudioClientStop(IntPtr client)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[AC_Stop];
        return fn(client);
    }

    private static unsafe int AudioClientGetService(IntPtr client, Guid riid, out IntPtr service)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[AC_GetService];
        Guid localRiid = riid;
        IntPtr svc;
        int hr = fn(client, &localRiid, &svc);
        service = svc;
        return hr;
    }

    private static unsafe int CaptureClientGetBuffer(IntPtr client, out IntPtr data, out uint numFrames, out uint flags, out ulong devPos, out ulong qpcPos)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, ulong*, ulong*, int>)vtable[ACC_GetBuffer];
        IntPtr d; uint nf, fl; ulong dp, qp;
        int hr = fn(client, &d, &nf, &fl, &dp, &qp);
        data = d; numFrames = nf; flags = fl; devPos = dp; qpcPos = qp;
        return hr;
    }

    private static unsafe int CaptureClientReleaseBuffer(IntPtr client, uint numFrames)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtable[ACC_ReleaseBuffer];
        return fn(client, numFrames);
    }

    private static unsafe int CaptureClientGetNextPacketSize(IntPtr client, out uint numFrames)
    {
        var vtable = *(IntPtr**)client;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtable[ACC_GetNextPacketSize];
        uint nf;
        int hr = fn(client, &nf);
        numFrames = nf;
        return hr;
    }

    private class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _event = new(false);
        public object Result;
        public int HResult;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out HResult, out Result);
            _event.Set();
        }

        public bool WaitForCompletion(int timeoutMs) => _event.Wait(timeoutMs);
    }
}

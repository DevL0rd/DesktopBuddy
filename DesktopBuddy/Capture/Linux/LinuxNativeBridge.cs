using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBuddy;

internal sealed unsafe class LinuxNativeBridge : IDisposable
{
    private delegate* unmanaged[Cdecl]<byte*, nuint, DbLinuxSelection*, int> _selectStream;
    private delegate* unmanaged[Cdecl]<uint, ulong*, int> _startNode;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int> _pollFrame;
    private delegate* unmanaged[Cdecl]<ulong, void> _stopCapture;
    private delegate* unmanaged[Cdecl]<ulong, int> _captureAlive;
    private delegate* unmanaged[Cdecl]<DbLinuxFrame*, byte*, UIntPtr, int> _copyAndCloseFrame;
    private delegate* unmanaged[Cdecl]<DbLinuxFrame*, void> _closeFrame;
    private delegate* unmanaged[Cdecl]<ulong*, int> _audioStart;
    private delegate* unmanaged[Cdecl]<ulong, float*, int, int> _audioPoll;
    private delegate* unmanaged[Cdecl]<ulong, void> _audioStop;
    private delegate* unmanaged[Cdecl]<uint, int, int, int, uint, byte*, UIntPtr, byte*, UIntPtr, int, int, int, ulong*, int> _streamStart;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int> _streamPushFrame;
    private delegate* unmanaged[Cdecl]<ulong, float*, int, int> _streamPushAudio;
    private delegate* unmanaged[Cdecl]<ulong, byte*, int, long*, int*, long, int*, int*, int> _streamRead;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxStreamInfo*, int> _streamInfo;
    private delegate* unmanaged[Cdecl]<ulong, void> _streamStop;
    private delegate* unmanaged[Cdecl]<IntPtr> _streamLastError;
    private delegate* unmanaged[Cdecl]<int, int, int> _vcamOpen;
    private delegate* unmanaged[Cdecl]<int, byte*, int, int> _vcamWrite;
    private delegate* unmanaged[Cdecl]<int, void> _vcamClose;
    private delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, int> _inputStart;
    private delegate* unmanaged[Cdecl]<ulong, double, double, void> _inputMotion;
    private delegate* unmanaged[Cdecl]<ulong, int, int, void> _inputButton;
    private delegate* unmanaged[Cdecl]<ulong, int, void> _inputScroll;
    private delegate* unmanaged[Cdecl]<ulong, int, int, void> _inputKey;
    private delegate* unmanaged[Cdecl]<ulong, void> _inputStop;
    private IntPtr _module;
    private IntPtr _streamModule;
    private bool _disposed;

    private bool IsLoaded => _module != IntPtr.Zero;

    internal bool TryLoad()
    {
        if (IsLoaded) return true;

        string nativePath = ResolveNativePath();
        if (string.IsNullOrWhiteSpace(nativePath) || !File.Exists(nativePath))
        {
            Log.Msg($"[LinuxNativeBridge] Native library not found: {nativePath ?? "(null)"}");
            return false;
        }

        try
        {
            _module = NativeLibrary.Load(nativePath);
            _selectStream = (delegate* unmanaged[Cdecl]<byte*, nuint, DbLinuxSelection*, int>)NativeLibrary.GetExport(_module, "db_linux_select_stream");
            _startNode = (delegate* unmanaged[Cdecl]<uint, ulong*, int>)NativeLibrary.GetExport(_module, "db_linux_capture_start_node");
            _pollFrame = (delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int>)NativeLibrary.GetExport(_module, "db_linux_capture_poll");
            _stopCapture = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_module, "db_linux_capture_stop");
            _captureAlive = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_module, "db_linux_capture_alive");
            _copyAndCloseFrame = (delegate* unmanaged[Cdecl]<DbLinuxFrame*, byte*, UIntPtr, int>)NativeLibrary.GetExport(_module, "db_linux_frame_copy_and_close");
            _closeFrame = (delegate* unmanaged[Cdecl]<DbLinuxFrame*, void>)NativeLibrary.GetExport(_module, "db_linux_frame_close");
            _audioStart = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(_module, "db_linux_audio_start");
            _audioPoll = (delegate* unmanaged[Cdecl]<ulong, float*, int, int>)NativeLibrary.GetExport(_module, "db_linux_audio_poll");
            _audioStop = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_module, "db_linux_audio_stop");
            _inputStart = (delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, int>)NativeLibrary.GetExport(_module, "db_linux_input_start");
            _inputMotion = (delegate* unmanaged[Cdecl]<ulong, double, double, void>)NativeLibrary.GetExport(_module, "db_linux_input_motion");
            _inputButton = (delegate* unmanaged[Cdecl]<ulong, int, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_button");
            _inputScroll = (delegate* unmanaged[Cdecl]<ulong, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_scroll");
            _inputKey = (delegate* unmanaged[Cdecl]<ulong, int, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_key");
            _inputStop = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_module, "db_linux_input_stop");
            Log.Msg($"[LinuxNativeBridge] Loaded {nativePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxNativeBridge] Load failed path={nativePath}: {ex.Message}");
            if (_module != IntPtr.Zero)
            {
                NativeLibrary.Free(_module);
                _module = IntPtr.Zero;
            }
            _selectStream = null;
            _startNode = null;
            _pollFrame = null;
            _stopCapture = null;
            _captureAlive = null;
            _copyAndCloseFrame = null;
            _closeFrame = null;
            _audioStart = null;
            _audioPoll = null;
            _audioStop = null;
            _inputStart = null;
            _inputMotion = null;
            _inputButton = null;
            _inputScroll = null;
            _inputKey = null;
            _inputStop = null;
            return false;
        }
    }

    internal int SelectStream(string restoreToken, out DbLinuxSelection selection, out string newRestoreToken,
        out string sourceName, out bool isMonitor)
    {
        selection = default;
        newRestoreToken = null;
        sourceName = null;
        isMonitor = false;
        if (!TryLoad() || _selectStream == null) return -1;

        byte[] tokenBytes = string.IsNullOrEmpty(restoreToken)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(restoreToken);

        int status;
        fixed (DbLinuxSelection* ptr = &selection)
        fixed (byte* tok = tokenBytes)
            status = _selectStream(tok, (nuint)(tokenBytes?.Length ?? 0), ptr);

        if (status == 0)
        {
            if (selection.RestoreTokenLen > 0)
            {
                int len = (int)Math.Min(selection.RestoreTokenLen, 256u);
                fixed (byte* p = selection.RestoreToken)
                    newRestoreToken = System.Text.Encoding.UTF8.GetString(p, len);
            }
            if (selection.NameLen > 0)
            {
                int len = (int)Math.Min(selection.NameLen, 256u);
                fixed (byte* p = selection.Name)
                    sourceName = System.Text.Encoding.UTF8.GetString(p, len);
            }
            isMonitor = selection.IsMonitor != 0;
        }
        return status;
    }

    internal ulong InputStart(string restoreToken)
    {
        if (!TryLoad() || _inputStart == null) return 0;
        byte[] tokenBytes = string.IsNullOrEmpty(restoreToken)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(restoreToken);
        ulong id = 0;
        fixed (byte* tok = tokenBytes)
            _inputStart(tok, (nuint)(tokenBytes?.Length ?? 0), &id);
        return id;
    }

    internal void InputMotion(ulong sessionId, double u, double v)
    {
        if (_inputMotion == null || sessionId == 0) return;
        _inputMotion(sessionId, u, v);
    }

    internal void InputButton(ulong sessionId, int evdevButton, bool pressed)
    {
        if (_inputButton == null || sessionId == 0) return;
        _inputButton(sessionId, evdevButton, pressed ? 1 : 0);
    }

    internal void InputScroll(ulong sessionId, int steps)
    {
        if (_inputScroll == null || sessionId == 0) return;
        _inputScroll(sessionId, steps);
    }

    internal void InputKey(ulong sessionId, int keysym, bool pressed)
    {
        if (_inputKey == null || sessionId == 0) return;
        _inputKey(sessionId, keysym, pressed ? 1 : 0);
    }

    internal void InputStop(ulong sessionId)
    {
        if (_inputStop == null || sessionId == 0) return;
        _inputStop(sessionId);
    }

    internal int StartCapture(uint nodeId, out ulong captureId)
    {
        captureId = 0;
        if (!TryLoad() || _startNode == null) return -1;
        ulong id = 0;
        int status = _startNode(nodeId, &id);
        captureId = id;
        return status;
    }

    internal int PollFrame(ulong captureId, out DbLinuxFrame frame)
    {
        frame = default;
        if (!TryLoad() || _pollFrame == null || captureId == 0) return -1;
        DbLinuxFrame local = default;
        int status = _pollFrame(captureId, &local);
        frame = local;
        return status;
    }

    internal void StopCapture(ulong captureId)
    {
        if (_stopCapture == null || captureId == 0) return;
        _stopCapture(captureId);
    }

    internal bool IsCaptureAlive(ulong captureId)
    {
        if (!TryLoad() || _captureAlive == null || captureId == 0) return true;
        return _captureAlive(captureId) != 0;
    }

    internal int CopyAndCloseFrame(DbLinuxFrame frame, byte[] destination)
    {
        if (!TryLoad() || _copyAndCloseFrame == null || destination == null || destination.Length == 0)
            return -1;

        fixed (byte* dst = destination)
            return _copyAndCloseFrame(&frame, dst, (UIntPtr)(ulong)destination.LongLength);
    }

    internal void CloseFrame(DbLinuxFrame frame)
    {
        if (_closeFrame == null || frame.Fd < 0) return;
        _closeFrame(&frame);
    }

    internal int StartStream(uint nodeId, int fps, int bitrateMbps, int maxResolution, uint adapterVendorId, string encoderPreference, string rtspUrl, bool audioEnabled, int audioSampleRate, int audioChannels, out ulong streamId)
    {
        streamId = 0;
        if (!TryLoadStream() || _streamStart == null) return -1;
        byte[] encoderBytes = string.IsNullOrEmpty(encoderPreference) ? Encoding.UTF8.GetBytes("auto") : Encoding.UTF8.GetBytes(encoderPreference);
        byte[] rtspBytes = string.IsNullOrEmpty(rtspUrl) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(rtspUrl);
        fixed (byte* encoderPtr = encoderBytes)
        fixed (byte* rtspPtr = rtspBytes)
        {
            ulong id = 0;
            int status = _streamStart(
                nodeId,
                fps,
                bitrateMbps,
                maxResolution,
                adapterVendorId,
                encoderPtr,
                (UIntPtr)(ulong)encoderBytes.LongLength,
                rtspPtr,
                (UIntPtr)(ulong)rtspBytes.LongLength,
                audioEnabled ? 1 : 0,
                audioSampleRate,
                audioChannels,
                &id);
            streamId = id;
            return status;
        }
    }

    internal int StartAudioCapture(out ulong captureId)
    {
        captureId = 0;
        if (!TryLoad() || _audioStart == null) return -1;
        ulong id = 0;
        int status = _audioStart(&id);
        captureId = id;
        return status;
    }

    internal int PollAudio(ulong captureId, float[] buffer)
    {
        if (!TryLoad() || _audioPoll == null || captureId == 0 || buffer == null || buffer.Length == 0)
            return 0;
        fixed (float* ptr = buffer)
            return _audioPoll(captureId, ptr, buffer.Length);
    }

    internal void StopAudioCapture(ulong captureId)
    {
        if (_audioStop == null || captureId == 0) return;
        _audioStop(captureId);
    }

    internal int PushStreamAudio(ulong streamId, float[] buffer, int frameCount)
    {
        if (!TryLoadStream() || _streamPushAudio == null || streamId == 0 || buffer == null || frameCount <= 0)
            return -1;
        fixed (float* ptr = buffer)
            return _streamPushAudio(streamId, ptr, frameCount);
    }

    internal int ReadStream(ulong streamId, byte[] destination, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool keyframeAligned)
    {
        keyframeAligned = aligned;
        if (!TryLoadStream() || _streamRead == null || streamId == 0 || destination == null || destination.Length == 0)
            return 0;

        int alignedValue = aligned ? 1 : 0;
        int keyAlignedValue = keyframeAligned ? 1 : 0;
        int bytesRead = 0;
        long localReadPos = readPos;
        fixed (byte* dst = destination)
        {
            int status = _streamRead(streamId, dst, destination.Length, &localReadPos, &alignedValue, minimumKeyframePos, &keyAlignedValue, &bytesRead);
            readPos = localReadPos;
            aligned = alignedValue != 0;
            keyframeAligned = keyAlignedValue != 0;
            return status == 0 ? bytesRead : 0;
        }
    }

    internal int PushStreamFrame(ulong streamId, DbLinuxFrame frame)
    {
        if (!TryLoadStream() || _streamPushFrame == null || streamId == 0 || frame.Fd < 0)
            return -1;
        return _streamPushFrame(streamId, &frame);
    }

    internal DbLinuxStreamInfo GetStreamInfo(ulong streamId)
    {
        var info = default(DbLinuxStreamInfo);
        if (!TryLoadStream() || _streamInfo == null || streamId == 0)
            return info;
        _streamInfo(streamId, &info);
        return info;
    }

    internal void StopStream(ulong streamId)
    {
        if (_streamStop == null || streamId == 0) return;
        _streamStop(streamId);
    }

    private bool TryLoadStream()
    {
        if (_streamModule != IntPtr.Zero) return true;

        string streamPath = ResolveSiblingNativePath("libdesktopbuddy_linux_stream.so");
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath))
        {
            Log.Msg($"[LinuxNativeBridge] Stream library not found: {streamPath ?? "(null)"}");
            return false;
        }

        try
        {
            _streamModule = NativeLibrary.Load(streamPath);
            _streamStart = (delegate* unmanaged[Cdecl]<uint, int, int, int, uint, byte*, UIntPtr, byte*, UIntPtr, int, int, int, ulong*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_start");
            _streamPushFrame = (delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_push_frame");
            _streamPushAudio = (delegate* unmanaged[Cdecl]<ulong, float*, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_push_audio");
            _streamRead = (delegate* unmanaged[Cdecl]<ulong, byte*, int, long*, int*, long, int*, int*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_read");
            _streamInfo = (delegate* unmanaged[Cdecl]<ulong, DbLinuxStreamInfo*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_info");
            _streamStop = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_stop");
            _streamLastError = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_last_error");
            _vcamOpen = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_open");
            _vcamWrite = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_write");
            _vcamClose = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_close");
            Log.Msg($"[LinuxNativeBridge] Loaded stream library {streamPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxNativeBridge] Stream library load failed path={streamPath}: {ex.Message}");
            if (_streamModule != IntPtr.Zero)
            {
                NativeLibrary.Free(_streamModule);
                _streamModule = IntPtr.Zero;
            }
            _streamStart = null;
            _streamPushFrame = null;
            _streamPushAudio = null;
            _streamRead = null;
            _streamInfo = null;
            _streamStop = null;
            _streamLastError = null;
            _vcamOpen = null;
            _vcamWrite = null;
            _vcamClose = null;
            return false;
        }
    }

    internal int VcamOpen(int width, int height)
    {
        if (!TryLoadStream() || _vcamOpen == null) return -1;
        return _vcamOpen(width, height);
    }

    internal int VcamWrite(int fd, byte[] data, int length)
    {
        if (_vcamWrite == null || fd < 0 || data == null || length <= 0) return -1;
        fixed (byte* ptr = data)
            return _vcamWrite(fd, ptr, length);
    }

    internal void VcamClose(int fd)
    {
        if (_vcamClose == null || fd < 0) return;
        _vcamClose(fd);
    }

    internal string GetStreamLastError()
    {
        if (_streamLastError == null)
            return null;
        try
        {
            IntPtr ptr = _streamLastError();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch { return null; }
    }

    private static string ResolveNativePath()
    {
        string overridePath = Environment.GetEnvironmentVariable("DESKTOPBUDDY_LINUX_NATIVE");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty;
        string alongside = Path.Combine(assemblyDir, "libdesktopbuddy_linux_native.so");
        if (File.Exists(alongside))
            return alongside;

        try { return DesktopBuddyRuntimePaths.FindFile("libdesktopbuddy_linux_native.so"); }
        catch { return Path.Combine(assemblyDir, "DesktopBuddyRuntime", "libdesktopbuddy_linux_native.so"); }
    }

    private static string ResolveSiblingNativePath(string fileName)
    {
        string overridePath = Environment.GetEnvironmentVariable("DESKTOPBUDDY_LINUX_STREAM");
        if (fileName == "libdesktopbuddy_linux_stream.so" && !string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string nativePath = ResolveNativePath();
        string nativeDir = Path.GetDirectoryName(nativePath);
        if (!string.IsNullOrWhiteSpace(nativeDir))
        {
            string sibling = Path.Combine(nativeDir, fileName);
            if (File.Exists(sibling))
                return sibling;
        }

        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty;
        string alongside = Path.Combine(assemblyDir, fileName);
        if (File.Exists(alongside))
            return alongside;

        try { return DesktopBuddyRuntimePaths.FindFile(fileName); }
        catch { return Path.Combine(assemblyDir, "DesktopBuddyRuntime", fileName); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selectStream = null;
        _startNode = null;
        _pollFrame = null;
        _stopCapture = null;
        _captureAlive = null;
        _copyAndCloseFrame = null;
        _closeFrame = null;
        _audioStart = null;
        _audioPoll = null;
        _audioStop = null;
        _inputStart = null;
        _inputMotion = null;
        _inputButton = null;
        _inputScroll = null;
        _inputKey = null;
        _inputStop = null;
        _streamStart = null;
        _streamPushFrame = null;
        _streamPushAudio = null;
        _streamRead = null;
        _streamInfo = null;
        _streamStop = null;
        _streamLastError = null;
        _vcamOpen = null;
        _vcamWrite = null;
        _vcamClose = null;
        if (_streamModule != IntPtr.Zero)
        {
            NativeLibrary.Free(_streamModule);
            _streamModule = IntPtr.Zero;
        }
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
            _module = IntPtr.Zero;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DbLinuxSelection
{
    public int Status;
    public uint NodeId;
    public uint Width;
    public uint Height;
    public int PositionX;
    public int PositionY;
    public uint HasPosition;
    public uint RestoreTokenLen;
    public fixed byte RestoreToken[256];
    public uint IsMonitor;
    public uint NameLen;
    public fixed byte Name[256];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DbLinuxFrame
{
    public int Status;
    public int Fd;
    public uint Width;
    public uint Height;
    public uint Fourcc;
    public uint Offset;
    public int Stride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DbLinuxStreamInfo
{
    public int Running;
    public int Broken;
    public int Width;
    public int Height;
    public long WritePos;
    public long KeyframePos;
    public long Frames;
}

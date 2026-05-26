using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal sealed unsafe class LinuxNativeBridge : IDisposable
{
    private delegate* unmanaged[Cdecl]<DbLinuxSelection*, int> _selectStream;
    private delegate* unmanaged[Cdecl]<ulong*, nuint, int> _start;
    private delegate* unmanaged[Cdecl]<DbLinuxFrame*, int> _poll;
    private delegate* unmanaged[Cdecl]<void> _stop;
    private IntPtr _module;
    private bool _disposed;

    internal bool IsLoaded => _module != IntPtr.Zero;

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
            _selectStream = (delegate* unmanaged[Cdecl]<DbLinuxSelection*, int>)NativeLibrary.GetExport(_module, "db_linux_select_stream");
            _start = (delegate* unmanaged[Cdecl]<ulong*, nuint, int>)NativeLibrary.GetExport(_module, "db_linux_capture_start");
            _poll = (delegate* unmanaged[Cdecl]<DbLinuxFrame*, int>)NativeLibrary.GetExport(_module, "db_linux_capture_poll");
            _stop = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(_module, "db_linux_capture_stop");
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
            _start = null;
            _poll = null;
            _stop = null;
            return false;
        }
    }

    internal int SelectStream(out DbLinuxSelection selection)
    {
        selection = default;
        if (!TryLoad() || _selectStream == null) return -1;
        fixed (DbLinuxSelection* ptr = &selection)
            return _selectStream(ptr);
    }

    internal int Start(ulong[] modifiers)
    {
        if (!TryLoad() || _start == null) return -1;
        modifiers ??= Array.Empty<ulong>();

        fixed (ulong* modifierPtr = modifiers)
            return _start(modifierPtr, (nuint)modifiers.Length);
    }

    internal int Poll(out DbLinuxFrame frame)
    {
        frame = default;
        if (!IsLoaded || _poll == null) return -1;

        fixed (DbLinuxFrame* ptr = &frame)
            return _poll(ptr);
    }

    internal void Stop()
    {
        if (!IsLoaded || _stop == null) return;
        _stop();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _selectStream = null;
        _start = null;
        _poll = null;
        _stop = null;
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
    public ulong Modifier;
    public uint HasModifier;
    public uint PlaneCount;
    public uint MouseValid;
    public float MouseX;
    public float MouseY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DbLinuxBridgeCall
{
    public uint Op;
    public int Status;
    public ulong Modifiers;
    public uint ModifierCount;
    public uint Reserved;
    public DbLinuxFrame Frame;
}

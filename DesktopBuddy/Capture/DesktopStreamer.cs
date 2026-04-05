using System;

namespace DesktopBuddy;

/// <summary>
/// Desktop capture using WGC (GPU-accelerated).
/// </summary>
public sealed class DesktopStreamer : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _monitorHandle;
    private WgcCapture _wgc;
    private int _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IntPtr WindowHandle => _hwnd;
    public int FramesCaptured => _wgc?.FramesCaptured ?? 0;
    public bool IsValid => _wgc?.IsValid ?? false;
    public bool UsingWgc => true;
    public object D3dContextLock => _wgc?.D3dContextLock;

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame
    {
        get => _wgc?.OnGpuFrame;
        set { if (_wgc != null) _wgc.OnGpuFrame = value; }
    }

    public DesktopStreamer(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _monitorHandle = monitorHandle;
    }

    public bool TryInitialCapture()
    {
        var wgc = new WgcCapture();
        bool success = false;

        // Run WGC init on a background thread with timeout to avoid freezing the engine.
        // D3D11CreateDevice and CreateForWindow can hang on certain windows or when GPU
        // resources are exhausted.
        // WGC uses WinRT COM — must init on the calling thread (engine thread).
        // Cannot move to background thread due to COM apartment requirements.
        try { success = wgc.Init(_hwnd, _monitorHandle); }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[DesktopStreamer] WGC init exception: {ex.Message}");
        }

        if (!success)
        {
            wgc.Dispose();
            return false;
        }

        _wgc = wgc;
        Width = _wgc.Width;
        Height = _wgc.Height;
        ResoniteModLoader.ResoniteMod.Msg($"[DesktopStreamer] WGC capture initialized ({Width}x{Height})");
        return true;
    }

    public byte[] CaptureFrame(out int width, out int height)
    {
        var frame = _wgc.TakeFrame(out width, out height);
        if (frame != null)
        {
            Width = width;
            Height = height;
        }
        return frame;
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wgc?.Dispose();
        _wgc = null;
    }
}

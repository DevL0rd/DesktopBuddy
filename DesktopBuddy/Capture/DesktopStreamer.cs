using System;

namespace DesktopBuddy;

public sealed class DesktopStreamer : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _monitorHandle;
    private WgcCapture _wgc;
    private int _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsValid => _wgc?.IsValid ?? false;
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

        try { success = wgc.Init(_hwnd, _monitorHandle); }
        catch (Exception ex)
        {
            Log.Msg($"[DesktopStreamer] WGC init exception: {ex.Message}");
        }

        if (!success)
        {
            wgc.Dispose();
            return false;
        }

        _wgc = wgc;
        Width = _wgc.Width;
        Height = _wgc.Height;
        Log.Msg($"[DesktopStreamer] WGC capture initialized ({Width}x{Height})");
        return true;
    }

    public void SetTextureTarget(DesktopTextureSource tex) => _wgc?.SetTextureTarget(tex);

    public void RecreatePoolIfNeeded()
    {
        if (_wgc == null) return;
        _wgc.RecreatePoolIfNeeded();
        Width = _wgc.Width;
        Height = _wgc.Height;
    }

    public void FlushD3dContext() => _wgc?.FlushD3dContext();

    public void StopCapture()
    {
        _wgc?.StopCapture();
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wgc?.Dispose();
        _wgc = null;
    }
}

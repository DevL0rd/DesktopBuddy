using System;

namespace DesktopBuddy;

internal sealed class WgcCaptureBackend : IDesktopCaptureBackend
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _monitorHandle;
    private WgcCapture _wgc;

    public int Width => _wgc?.Width ?? 0;
    public int Height => _wgc?.Height ?? 0;
    public bool IsValid => _wgc?.IsValid ?? false;
    public object D3dContextLock => _wgc?.D3dContextLock;
    public IntPtr D3dDevice => _wgc?.D3dDevice ?? IntPtr.Zero;
    public IntPtr D3dContext => _wgc?.D3dContext ?? IntPtr.Zero;
    public IntPtr SharedTexture => _wgc?.SharedTexture ?? IntPtr.Zero;
    public IntPtr SharedTextureHandle => _wgc?.SharedTextureHandle ?? IntPtr.Zero;
    public int SharedTextureWidth => _wgc?.SharedTextureWidth ?? 0;
    public int SharedTextureHeight => _wgc?.SharedTextureHeight ?? 0;
    public bool HasCurrentSharedFrame => _wgc?.HasCurrentSharedFrame ?? false;
    public bool IsResizeRecreatePending => _wgc?.IsResizeRecreatePending ?? false;

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame
    {
        get => _wgc?.OnGpuFrame;
        set { if (_wgc != null) _wgc.OnGpuFrame = value; }
    }

    internal WgcCaptureBackend(IntPtr hwnd, IntPtr monitorHandle)
    {
        _hwnd = hwnd;
        _monitorHandle = monitorHandle;
    }

    public bool TryInitialCapture()
    {
        Log.Msg($"[WgcCaptureBackend] Initial capture starting hwnd={_hwnd} monitor=0x{_monitorHandle:X}");
        var wgc = new WgcCapture();
        bool success = false;

        try { success = wgc.Init(_hwnd, _monitorHandle); }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCaptureBackend] WGC init exception: {ex.Message}");
        }

        if (!success)
        {
            Log.Msg($"[WgcCaptureBackend] Initial capture failed hwnd={_hwnd} monitor=0x{_monitorHandle:X}");
            wgc.Dispose();
            return false;
        }

        _wgc = wgc;
        Log.Msg($"[WgcCaptureBackend] WGC capture initialized ({Width}x{Height})");
        return true;
    }

    public void RecreatePoolIfNeeded() => _wgc?.RecreatePoolIfNeeded();
    public void FlushD3dContext() => _wgc?.FlushD3dContext();
    public void StopCapture() => _wgc?.StopCapture();

    public void Dispose()
    {
        _wgc?.Dispose();
        _wgc = null;
    }
}

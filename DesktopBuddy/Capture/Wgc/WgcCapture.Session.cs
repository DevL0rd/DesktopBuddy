using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;

namespace DesktopBuddy;

public sealed partial class WgcCapture
{

    private static readonly Guid DxgiAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid TexGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiResourceGuid = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
    public IntPtr D3dDevice => _d3dDevice;
    public IntPtr SharedTexture => _sharedTexture;
    public IntPtr SharedTextureHandle { get; private set; }
    public int SharedTextureWidth { get; private set; }
    public int SharedTextureHeight { get; private set; }
    public bool IsValid => !_disposed && !_closed && _item != null && (_isDesktop || (IsWindow(_hwnd) && !IsIconic(_hwnd)));

    public void RecreatePoolIfNeeded()
    {
        if (!_needsPoolRecreate || _disposed) return;
        lock (_disposeLock)
        {
            if (!_needsPoolRecreate || _disposed) return;
            try
            {
                _framePool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                    new SizeInt32 { Width = Width, Height = Height });
                TryCreateSharedTexture(Width, Height);
                _needsPoolRecreate = false;
                Log.Msg($"[WgcCapture] FramePool/shared texture recreated for {Width}x{Height}");
            }
            catch (Exception ex)
            {
                _needsPoolRecreate = false;
                Log.Msg($"[WgcCapture] FramePool.Recreate failed: {ex.Message}");
            }
        }
    }

    public bool Init(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _isDesktop = hwnd == IntPtr.Zero;
        try
        {
            if (!EnsureSharedD3dDevice()) return false;
            _d3dDevice = _sharedD3dDevice;
            _d3dContext = _sharedD3dContext;
            _winrtDevice = _sharedWinrtDevice;

            if (hwnd == IntPtr.Zero)
            {
                IntPtr hMon = monitorHandle != default ? monitorHandle : MonitorFromPoint(0, 0, 1);
                Log.Msg($"[WgcCapture] Creating capture for monitor 0x{hMon:X} (explicit={monitorHandle != default})");
                _item = CreateItemForMonitor(hMon);
            }
            else
            {
                _item = CreateItemForWindow(hwnd);
            }

            if (_item == null) { Log.Msg("[WgcCapture] CaptureItem is null"); return false; }

            _itemClosedHandler = (_, _) => { _closed = true; };
            _item.Closed += _itemClosedHandler;

            Width = _item.Size.Width;
            Height = _item.Size.Height;
            TryCreateSharedTexture(Width, Height);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Msg($"[WgcCapture] IsBorderRequired not supported (Win11+ only): {ex.Message}"); }
            TrySetIncludeSecondaryWindows(_session);
            _session.IsCursorCaptureEnabled = true;

            _session.StartCapture();

            Log.Msg($"[WgcCapture] Init complete: {Width}x{Height}, hwnd={hwnd}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] Init failed: {ex}");
            return false;
        }
    }

    private static void TrySetIncludeSecondaryWindows(GraphicsCaptureSession session)
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IncludeSecondaryWindows"))
            {
                Log.Msg("[WgcCapture] IncludeSecondaryWindows unsupported on this Windows API");
                return;
            }

            session.IncludeSecondaryWindows = true;
            Log.Msg("[WgcCapture] IncludeSecondaryWindows enabled");
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] IncludeSecondaryWindows failed: {ex.Message}");
        }
    }

}

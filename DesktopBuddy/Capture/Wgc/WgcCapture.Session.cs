using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
    public bool HasCurrentSharedFrame => _sharedFramesCopied > 0 && !_needsPoolRecreate;
    public bool IsResizeRecreatePending => _needsPoolRecreate;
    public IntPtr D3dDevice => _d3dDevice;
    public IntPtr SharedTexture => _sharedTexture;
    public IntPtr SharedTextureHandle { get; private set; }
    public int SharedTextureWidth { get; private set; }
    public int SharedTextureHeight { get; private set; }
    public bool IsValid => !_disposed && !_closed && _item != null && (_isDesktop || (IsWindow(_hwnd) && !IsIconic(_hwnd)));

    public void RecreatePoolIfNeeded()
    {
        if (!_needsPoolRecreate || _disposed) return;
        if (_updateCountForLogging() % 30 == 0)
            Log.Msg($"[WgcCapture] Resize recreate pending hwnd={_hwnd} target={_resizeTargetWidth}x{_resizeTargetHeight} worker={_resizeRecreateWorkerRunning}");
        QueuePoolRecreateWorker();
    }

    private void RequestPoolRecreate(int width, int height)
    {
        if (width <= 0 || height <= 0 || _disposed) return;

        Width = width;
        Height = height;
        _sharedFramesCopied = 0;
        lock (_resizeRecreateLock)
        {
            _resizeTargetWidth = width;
            _resizeTargetHeight = height;
            _needsPoolRecreate = true;
        }

        Log.Msg($"[WgcCapture] Resize recreate requested hwnd={_hwnd} target={width}x{height} worker={_resizeRecreateWorkerRunning}");
        QueuePoolRecreateWorker();
    }

    private void QueuePoolRecreateWorker()
    {
        if (Interlocked.CompareExchange(ref _resizeRecreateWorkerRunning, 1, 0) != 0)
        {
            Log.Msg($"[WgcCapture] Resize recreate worker already running hwnd={_hwnd} target={_resizeTargetWidth}x{_resizeTargetHeight}");
            return;
        }

        Log.Msg($"[WgcCapture] Resize recreate worker queued hwnd={_hwnd} target={_resizeTargetWidth}x{_resizeTargetHeight}");
        ThreadPool.QueueUserWorkItem(_ => RunPoolRecreateWorker());
    }

    private void RunPoolRecreateWorker()
    {
        Log.Msg($"[WgcCapture] Resize recreate worker START hwnd={_hwnd}");
        try
        {
            while (!_disposed)
            {
                int width;
                int height;
                lock (_resizeRecreateLock)
                {
                    if (!_needsPoolRecreate) return;
                    width = _resizeTargetWidth;
                    height = _resizeTargetHeight;
                }

                if (width <= 0 || height <= 0)
                {
                    Log.Msg($"[WgcCapture] Resize recreate worker ignored invalid target hwnd={_hwnd} target={width}x{height}");
                    return;
                }
                if (!TryEnterCaptureResourceUse())
                {
                    Log.Msg($"[WgcCapture] Resize recreate worker could not enter resource use hwnd={_hwnd} disposed={_disposed}");
                    return;
                }
                try
                {
                    Direct3D11CaptureFramePool framePool;
                    IDirect3DDevice winrtDevice;
                    lock (_disposeLock)
                    {
                        if (_disposed) return;
                        framePool = _framePool;
                        winrtDevice = _winrtDevice;
                    }

                    if (framePool == null || winrtDevice == null)
                    {
                        Log.Msg($"[WgcCapture] Resize recreate worker missing capture resources hwnd={_hwnd} pool={(framePool != null)} device={(winrtDevice != null)}");
                        return;
                    }

                    var startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    Log.Msg($"[WgcCapture] FramePool.Recreate START {width}x{height} hwnd={_hwnd}");
                    framePool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                        new SizeInt32 { Width = width, Height = height });
                    double recreateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Log.Msg($"[WgcCapture] FramePool.Recreate DONE {width}x{height} hwnd={_hwnd} {recreateMs:F2}ms");

                    bool textureReady = TryCreateSharedTexture(width, height);
                    lock (_resizeRecreateLock)
                    {
                        if (_resizeTargetWidth == width && _resizeTargetHeight == height)
                            _needsPoolRecreate = false;
                    }

                    Log.Msg(textureReady
                        ? $"[WgcCapture] FramePool/shared texture recreated for {width}x{height}"
                        : $"[WgcCapture] FramePool recreated but shared texture was not ready for {width}x{height}");

                    lock (_resizeRecreateLock)
                    {
                        if (!_needsPoolRecreate)
                            return;
                    }
                }
                catch (Exception ex)
                {
                    lock (_resizeRecreateLock) _needsPoolRecreate = false;
                    Log.Msg($"[WgcCapture] FramePool.Recreate failed: {ex}");
                    return;
                }
                finally
                {
                    LeaveCaptureResourceUse();
                }
            }
        }
        finally
        {
            Log.Msg($"[WgcCapture] Resize recreate worker EXIT hwnd={_hwnd} needs={_needsPoolRecreate} disposed={_disposed}");
            Interlocked.Exchange(ref _resizeRecreateWorkerRunning, 0);
            if (_needsPoolRecreate && !_disposed)
                QueuePoolRecreateWorker();
        }
    }

    private static int _wgcResizeLogCounter;
    private static int _updateCountForLogging() => Interlocked.Increment(ref _wgcResizeLogCounter);

    public bool Init(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _isDesktop = hwnd == IntPtr.Zero;
        try
        {
            Log.Msg($"[WgcCapture] Init starting hwnd={hwnd} monitor=0x{monitorHandle:X}");
            if (!CreateD3dDevice()) return false;
            Log.Msg($"[WgcCapture] D3D ready for hwnd={hwnd} device=0x{_d3dDevice:X}");

            if (hwnd == IntPtr.Zero)
            {
                IntPtr hMon = monitorHandle != default ? monitorHandle : MonitorFromPoint(0, 0, 1);
                Log.Msg($"[WgcCapture] Creating capture for monitor 0x{hMon:X} (explicit={monitorHandle != default})");
                _item = CreateItemForMonitor(hMon);
            }
            else
            {
                Log.Msg($"[WgcCapture] Creating capture for window 0x{hwnd:X}");
                _item = CreateItemForWindow(hwnd);
            }

            if (_item == null) { Log.Msg("[WgcCapture] CaptureItem is null"); return false; }
            Log.Msg($"[WgcCapture] CaptureItem ready: {_item.Size.Width}x{_item.Size.Height}, hwnd={hwnd}");

            _itemClosedHandler = (_, _) => { _closed = true; };
            _item.Closed += _itemClosedHandler;

            Width = _item.Size.Width;
            Height = _item.Size.Height;
            Log.Msg($"[WgcCapture] Creating shared texture {Width}x{Height}, hwnd={hwnd}");
            if (!TryCreateSharedTexture(Width, Height))
            {
                Log.Msg($"[WgcCapture] Init failed: shared texture unavailable hwnd={hwnd}");
                return false;
            }

            Log.Msg($"[WgcCapture] Creating frame pool {Width}x{Height}, hwnd={hwnd}");
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            Log.Msg($"[WgcCapture] Creating capture session hwnd={hwnd}");
            _session = _framePool.CreateCaptureSession(_item);
            try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Msg($"[WgcCapture] IsBorderRequired not supported (Win11+ only): {ex.Message}"); }
            TrySetIncludeSecondaryWindows(_session);
            _session.IsCursorCaptureEnabled = true;

            Log.Msg($"[WgcCapture] Starting capture hwnd={hwnd}");
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

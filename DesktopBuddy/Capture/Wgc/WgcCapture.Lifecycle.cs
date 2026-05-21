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

public sealed partial class WgcCapture : IDisposable
{

    public void StopCapture()
    {
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.StopCapture ENTER hwnd={_hwnd} disposed={_disposed}");
        GraphicsCaptureSession session;
        Direct3D11CaptureFramePool framePool;
        GraphicsCaptureItem item;
        TypedEventHandler<GraphicsCaptureItem, object> itemClosedHandler;
        lock (_disposeLock)
        {
            Log.MsgImmediate($"[CleanupTrace] WgcCapture.StopCapture dispose lock ACQUIRED hwnd={_hwnd}");
            if (_disposed) return;
            _disposed = true;
            session = _session;
            framePool = _framePool;
            item = _item;
            itemClosedHandler = _itemClosedHandler;
            _session = null;
            _framePool = null;
            _itemClosedHandler = null;
        }
        Log.Msg($"[WgcCapture:StopCapture] Stopping session hwnd={_hwnd}");
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.StopCapture unhook FrameArrived START hwnd={_hwnd}");
        try { if (framePool != null) framePool.FrameArrived -= OnFrameArrived; } catch (Exception ex) { Log.Msg($"[WgcCapture:StopCapture] Unhook error: {ex.Message}"); }
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.StopCapture unhook ItemClosed START hwnd={_hwnd}");
        try { if (item != null && itemClosedHandler != null) item.Closed -= itemClosedHandler; } catch (Exception ex) { Log.Msg($"[WgcCapture:StopCapture] Item closed unhook error: {ex.Message}"); }

        DisposeCaptureObjects(session, framePool, "StopCapture");
        Log.Msg("[WgcCapture:StopCapture] Session stopped, events unhooked/disposed");
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.StopCapture EXIT hwnd={_hwnd}");
    }

    public void Dispose()
    {
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.Dispose ENTER hwnd={_hwnd} disposed={_disposed}");
        bool alreadyStopped;
        GraphicsCaptureSession session;
        Direct3D11CaptureFramePool framePool;
        GraphicsCaptureItem item;
        TypedEventHandler<GraphicsCaptureItem, object> itemClosedHandler;
        lock (_disposeLock)
        {
            Log.MsgImmediate($"[CleanupTrace] WgcCapture.Dispose dispose lock ACQUIRED hwnd={_hwnd}");
            alreadyStopped = _disposed;
            _disposed = true;
            session = _session;
            framePool = _framePool;
            item = _item;
            itemClosedHandler = _itemClosedHandler;
            _session = null;
            _framePool = null;
            _itemClosedHandler = null;
        }

        if (!alreadyStopped)
        {
            Log.Msg($"[WgcCapture:Dispose] Unhooking events");
            try { if (framePool != null) framePool.FrameArrived -= OnFrameArrived; }
            catch (Exception ex) { Log.Msg($"[WgcCapture:Dispose] Unhook error: {ex.Message}"); }
            try { if (item != null && itemClosedHandler != null) item.Closed -= itemClosedHandler; }
            catch (Exception ex) { Log.Msg($"[WgcCapture:Dispose] Item closed unhook error: {ex.Message}"); }

            DisposeCaptureObjects(session, framePool, "Dispose");
        }
        _item = null;
        OnGpuFrame = null;

        _winrtDevice = null;
        _d3dDevice = IntPtr.Zero;
        _d3dContext = IntPtr.Zero;
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.Dispose ReleaseSharedTexture START hwnd={_hwnd}");
        ReleaseSharedTexture();
        Log.Msg($"[WgcCapture:Dispose] Detached from shared D3D device hwnd={_hwnd}");
        Log.MsgImmediate($"[CleanupTrace] WgcCapture.Dispose EXIT hwnd={_hwnd}");
    }

    private static void DisposeCaptureObjects(GraphicsCaptureSession session, Direct3D11CaptureFramePool framePool, string reason)
    {
        try { session?.Dispose(); }
        catch (Exception ex) { Log.Msg($"[WgcCapture:{reason}] Session dispose error: {ex.Message}"); }

        try { framePool?.Dispose(); }
        catch (Exception ex) { Log.Msg($"[WgcCapture:{reason}] FramePool dispose error: {ex.Message}"); }
    }
}

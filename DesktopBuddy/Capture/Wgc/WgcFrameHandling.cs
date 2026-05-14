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

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        lock (_disposeLock)
        {
        if (_disposed) return;
        try
        {
        var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        var size = frame.ContentSize;
        int w = size.Width;
        int h = size.Height;
        if (w <= 0 || h <= 0) { frame.Dispose(); return; }

        if (_needsPoolRecreate) { frame.Dispose(); return; }

        if (w != Width || h != Height)
        {
            Log.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
            Width = w; Height = h;
            _needsPoolRecreate = true;
            frame.Dispose();
            return;
        }

        IntPtr surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        frame.Dispose();
        if (surfaceAbi == IntPtr.Zero) return;

        var dxgiAccessGuid = DxgiAccessGuid;
        int qiHr = Marshal.QueryInterface(surfaceAbi, in dxgiAccessGuid, out IntPtr dxgiAccessPtr);
        Marshal.Release(surfaceAbi);
        if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) return;

        IntPtr srcTexture;
        unsafe
        {
            var vtable = *(IntPtr**)dxgiAccessPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            Guid localTexGuid = TexGuid;
            IntPtr tex;
            int getHr = fn(dxgiAccessPtr, &localTexGuid, &tex);
            srcTexture = tex;
            if (getHr < 0) { Marshal.Release(dxgiAccessPtr); return; }
        }
        Marshal.Release(dxgiAccessPtr);

        try
        {
            CopyFrameToSharedTexture(srcTexture, w, h);

            using (DesktopBuddyMod.Perf.Time("queue_frame"))
            {
                var gpuCb = OnGpuFrame;
                try { gpuCb?.Invoke(_d3dDevice, srcTexture, w, h); }
                catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
            }

            _framesCaptured++;
            if (_framesCaptured == 1) Log.Msg($"[WgcCapture] First frame: {w}x{h}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] OnFrameArrived error: {ex.Message}");
        }
        finally { Marshal.Release(srcTexture); }
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] OnFrameArrived OUTER error: {ex.Message}\n{ex.StackTrace}");
        }
        }
    }

}

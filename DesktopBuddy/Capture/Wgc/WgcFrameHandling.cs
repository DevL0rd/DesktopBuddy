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
            Direct3D11CaptureFrame frame = null;
            IntPtr surfaceAbi = IntPtr.Zero;
            IntPtr dxgiAccessPtr = IntPtr.Zero;
            IntPtr srcTexture = IntPtr.Zero;
            try
            {
                frame = sender.TryGetNextFrame();
                if (frame == null) return;

                var size = frame.ContentSize;
                int w = size.Width;
                int h = size.Height;
                if (w <= 0 || h <= 0) return;

                if (_needsPoolRecreate) return;

                if (w != Width || h != Height)
                {
                    Log.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
                    Width = w;
                    Height = h;
                    _needsPoolRecreate = true;
                    return;
                }

                surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
                if (surfaceAbi == IntPtr.Zero) return;

                var dxgiAccessGuid = DxgiAccessGuid;
                int qiHr = Marshal.QueryInterface(surfaceAbi, in dxgiAccessGuid, out dxgiAccessPtr);
                Marshal.Release(surfaceAbi);
                surfaceAbi = IntPtr.Zero;
                if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) return;

                unsafe
                {
                    var vtable = *(IntPtr**)dxgiAccessPtr;
                    var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                    Guid localTexGuid = TexGuid;
                    int getHr = fn(dxgiAccessPtr, &localTexGuid, &srcTexture);
                    if (getHr < 0 || srcTexture == IntPtr.Zero) return;
                }

                IntPtr encoderTexture = CopyFrameToSharedTexture(srcTexture, w, h);
                if (encoderTexture != IntPtr.Zero)
                {
                    using (DesktopBuddyMod.Perf.Time("queue_frame"))
                    {
                        var gpuCb = OnGpuFrame;
                        try { gpuCb?.Invoke(_d3dDevice, encoderTexture, w, h); }
                        catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
                    }
                }

                _framesCaptured++;
                if (_framesCaptured == 1) Log.Msg($"[WgcCapture] First frame: {w}x{h}");
            }
            catch (Exception ex)
            {
                Log.Msg($"[WgcCapture] OnFrameArrived error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (srcTexture != IntPtr.Zero) Marshal.Release(srcTexture);
                if (dxgiAccessPtr != IntPtr.Zero) Marshal.Release(dxgiAccessPtr);
                if (surfaceAbi != IntPtr.Zero) Marshal.Release(surfaceAbi);
                frame?.Dispose();
            }
        }
    }

}

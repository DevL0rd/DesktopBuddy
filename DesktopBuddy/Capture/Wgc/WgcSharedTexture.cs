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

    private readonly object _disposeLock = new();
    private readonly ManualResetEventSlim _frameCallbacksDrained = new(true);
    private int _activeFrameCallbacks;
    private int _frameProcessing;
    private int _d3dBusyFrameDrops;
    private int _frameBusyDrops;
    private IntPtr _sharedTexture;

    private bool TryEnterFrameProcessing()
    {
        lock (_disposeLock)
        {
            if (_disposed) return false;
            if (_frameProcessing != 0)
            {
                LogFrameBusyDrop();
                return false;
            }

            _frameProcessing = 1;
            _activeFrameCallbacks++;
            _frameCallbacksDrained.Reset();
            return true;
        }
    }

    private void LeaveFrameProcessing()
    {
        lock (_disposeLock)
        {
            _frameProcessing = 0;
            if (_activeFrameCallbacks > 0 && --_activeFrameCallbacks == 0)
                _frameCallbacksDrained.Set();
        }
    }

    private bool TryEnterCaptureResourceUse()
    {
        lock (_disposeLock)
        {
            if (_disposed) return false;
            _activeFrameCallbacks++;
            _frameCallbacksDrained.Reset();
            return true;
        }
    }

    private void LeaveCaptureResourceUse()
    {
        lock (_disposeLock)
        {
            if (_activeFrameCallbacks > 0 && --_activeFrameCallbacks == 0)
                _frameCallbacksDrained.Set();
        }
    }

    private bool WaitForFrameCallbacksToDrain(string reason)
    {
        if (_frameCallbacksDrained.Wait(TimeSpan.FromSeconds(2)))
            return true;

        int active;
        lock (_disposeLock) active = _activeFrameCallbacks;
        Log.Msg($"[WgcCapture:{reason}] Timed out waiting for {active} active frame callback(s); leaving D3D resources alive to avoid use-after-release hwnd={_hwnd}");
        return false;
    }

    private void LogFrameBusyDrop()
    {
        var dropped = Interlocked.Increment(ref _frameBusyDrops);
        if (dropped == 1 || dropped % 120 == 0)
            Log.Msg($"[WgcCapture] Dropped {dropped} frame callback(s): previous frame still processing hwnd={_hwnd}");
    }

    private void LogD3dBusyFrameDrop()
    {
        var dropped = Interlocked.Increment(ref _d3dBusyFrameDrops);
        if (dropped == 1 || dropped % 120 == 0)
            Log.Msg($"[WgcCapture] Dropped {dropped} shared texture copy frame(s): D3D context busy hwnd={_hwnd}");
    }

    private unsafe bool TryCreateSharedTexture(int width, int height)
    {
        if (_d3dDevice == IntPtr.Zero || width <= 0 || height <= 0)
            return false;

        if (_sharedTexture != IntPtr.Zero && SharedTextureWidth == width && SharedTextureHeight == height)
            return true;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_TYPELESS,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        lock (_d3dLock)
        {
            ReleaseSharedTextureUnlocked();

            var vtable = *(IntPtr**)_d3dDevice;
            var createTexture = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtable[ID3D11Device_CreateTexture2D];
            IntPtr texture;
            var createStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Log.Msg($"[WgcCapture] Shared texture CreateTexture2D START {width}x{height} hwnd={_hwnd}");
            int hr = createTexture(_d3dDevice, &desc, IntPtr.Zero, &texture);
            double createMs = (System.Diagnostics.Stopwatch.GetTimestamp() - createStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.Msg($"[WgcCapture] Shared texture CreateTexture2D DONE {width}x{height} hwnd={_hwnd} hr=0x{hr:X8} {createMs:F2}ms");
            if (hr < 0 || texture == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] Shared texture CreateTexture2D failed hr=0x{hr:X8} size={width}x{height}");
                return false;
            }

            var dxgiGuid = DxgiResourceGuid;
            hr = Marshal.QueryInterface(texture, in dxgiGuid, out IntPtr dxgiResource);
            if (hr < 0 || dxgiResource == IntPtr.Zero)
            {
                Marshal.Release(texture);
                Log.Msg($"[WgcCapture] Shared texture IDXGIResource QueryInterface failed hr=0x{hr:X8}");
                return false;
            }

            try
            {
                var dxgiVtable = *(IntPtr**)dxgiResource;
                var getSharedHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)dxgiVtable[8];
                IntPtr handle;
                hr = getSharedHandle(dxgiResource, &handle);
                if (hr < 0 || handle == IntPtr.Zero)
                {
                    Marshal.Release(texture);
                    Log.Msg($"[WgcCapture] GetSharedHandle failed hr=0x{hr:X8}");
                    return false;
                }

                _sharedTexture = texture;
                SharedTextureHandle = handle;
                SharedTextureWidth = width;
                SharedTextureHeight = height;
                _sharedFramesCopied = 0;
                Log.Msg($"[WgcCapture] Shared texture ready handle=0x{handle:X} ptr=0x{texture:X} {width}x{height}");
                return true;
            }
            finally
            {
                Marshal.Release(dxgiResource);
            }
        }
    }

    private unsafe IntPtr CopyFrameToSharedTexture(IntPtr srcTexture, int width, int height)
    {
        if (_sharedTexture == IntPtr.Zero || srcTexture == IntPtr.Zero)
            return IntPtr.Zero;
        if (width != SharedTextureWidth || height != SharedTextureHeight)
            return IntPtr.Zero;

        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_d3dLock, 0, ref lockTaken);
            if (!lockTaken)
            {
                LogD3dBusyFrameDrop();
                return IntPtr.Zero;
            }

            if (_disposed || _sharedTexture == IntPtr.Zero || _d3dContext == IntPtr.Zero)
                return IntPtr.Zero;

            var vtable = *(IntPtr**)_d3dContext;
            var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[ID3D11DeviceContext_CopyResource];
            copyResource(_d3dContext, _sharedTexture, srcTexture);
            return _sharedTexture;
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_d3dLock);
        }
    }

    private void ReleaseSharedTexture()
    {
        lock (_d3dLock)
        {
            ReleaseSharedTextureUnlocked();
        }
    }

    private void ReleaseSharedTextureUnlocked()
    {
        SharedTextureHandle = IntPtr.Zero;
        SharedTextureWidth = 0;
        SharedTextureHeight = 0;
        if (_sharedTexture != IntPtr.Zero)
        {
            try { Marshal.Release(_sharedTexture); }
            catch { }
            _sharedTexture = IntPtr.Zero;
        }
    }

    public object D3dContextLock => _d3dLock;

    public unsafe void FlushD3dContext()
    {
        Log.Msg($"[CleanupTrace] WgcCapture.FlushD3dContext ENTER hwnd={_hwnd} disposed={_disposed} ctx=0x{_d3dContext:X}");
        lock (_d3dLock)
        {
            Log.Msg($"[CleanupTrace] WgcCapture.FlushD3dContext D3D lock ACQUIRED hwnd={_hwnd}");
            if (_d3dContext == IntPtr.Zero) return;
            try
            {
                var vtable = *(IntPtr**)_d3dContext;
                Log.Msg($"[CleanupTrace] WgcCapture.FlushD3dContext Flush START hwnd={_hwnd}");
                var flushFn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[ID3D11DeviceContext_Flush];
                flushFn(_d3dContext);
                Log.Msg("[WgcCapture] D3D11 Flush OK");
                Log.Msg($"[CleanupTrace] WgcCapture.FlushD3dContext DONE hwnd={_hwnd}");
            }
            catch (Exception ex) { Log.Msg($"[WgcCapture] D3D11 flush error: {ex.Message}"); }
        }
        Log.Msg($"[CleanupTrace] WgcCapture.FlushD3dContext EXIT hwnd={_hwnd}");
    }

}

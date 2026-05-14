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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        WindowsCreateString(className, className.Length, out IntPtr hstring);
        RoGetActivationFactory(hstring, ref iid, out IntPtr factory);
        WindowsDeleteString(hstring);
        return factory;
    }

    private static bool EnsureCaptureInterop()
    {
        lock (_captureInteropLock)
        {
            if (_captureInterop != null) return true;

            var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
            if (factoryPtr == IntPtr.Zero)
            {
                Log.Msg("[WgcCapture] GraphicsCaptureItem activation factory unavailable");
                return false;
            }

            _captureInterop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            Log.Msg("[WgcCapture] GraphicsCaptureItem interop factory ready");
            return true;
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        if (!EnsureCaptureInterop()) return null;
        try
        {
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var ptr = _captureInterop.CreateForWindow(hwnd, ref itemGuid);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
            Marshal.Release(ptr);
            return item;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] CreateForWindow failed: {ex.Message}");
            return null;
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        if (!EnsureCaptureInterop()) return null;
        try
        {
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var ptr = _captureInterop.CreateForMonitor(hmon, ref itemGuid);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
            Marshal.Release(ptr);
            return item;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] CreateForMonitor failed: {ex.Message}");
            return null;
        }
    }

}

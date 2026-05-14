using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public static partial class WindowInput
{

    private static bool EnsureTouchInit()
    {
        if (_touchInitialized) return true;
        lock (_initLock)
        {
            if (_touchInitialized) return true;
            bool ok = InitializeTouchInjection(MAX_TOUCH_CONTACTS, TOUCH_FEEDBACK_NONE);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[WindowInput] InitializeTouchInjection FAILED err={err}");
                return false;
            }
            _touchInitialized = true;
            Log.Msg($"[WindowInput] Touch injection initialized (max={MAX_TOUCH_CONTACTS}, feedback=NONE)");
            return true;
        }
    }

    internal static bool PrewarmTouchInjection()
    {
        StartFocusEventHook();
        return EnsureTouchInit();
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void RestoreIfMinimized(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (IsIconic(hWnd))
        {
            Log.Msg($"[WindowInput] Restoring minimized window hwnd={hWnd}");
            ShowWindow(hWnd, SW_RESTORE);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private static int NormalizedToPixel(float value, int size)
    {
        if (size <= 1 || float.IsNaN(value) || float.IsInfinity(value))
            return 0;

        int max = size - 1;
        float scaled = value * size;
        if (scaled <= 0f) return 0;
        if (scaled >= max) return max;
        return (int)scaled;
    }

    private static bool TryGetWindowCaptureBounds(IntPtr hWnd, out RECT bounds)
    {
        bounds = default;
        if (hWnd == IntPtr.Zero)
            return false;

        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) == 0 &&
            bounds.Width > 0 && bounds.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hWnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    private static POINT UvToScreen(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        int px = NormalizedToPixel(u, clientW);
        int py = NormalizedToPixel(v, clientH);
        if (hWnd != IntPtr.Zero)
        {
            if (TryGetWindowCaptureBounds(hWnd, out var bounds))
            {
                return new POINT
                {
                    X = bounds.Left + NormalizedToPixel(u, bounds.Width),
                    Y = bounds.Top + NormalizedToPixel(v, bounds.Height)
                };
            }

            var pt = new POINT { X = px, Y = py };
            ClientToScreen(hWnd, ref pt);
            return pt;
        }

        var monitorPoint = new POINT { X = px, Y = py };
        if (monitorHandle != IntPtr.Zero)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitorHandle, ref mi))
            {
                monitorPoint.X += mi.rcMonitor.Left;
                monitorPoint.Y += mi.rcMonitor.Top;
            }
        }
        return monitorPoint;
    }

    private static IntPtr FocusRoot(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hWnd, GA_ROOTOWNER);
        return root != IntPtr.Zero ? root : hWnd;
    }

}

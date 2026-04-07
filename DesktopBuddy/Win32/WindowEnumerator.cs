using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBuddy;

public static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static readonly HashSet<string> _ignoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Progman", "WorkerW",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    public struct RECT { public int Left, Top, Right, Bottom; }

    public record WindowInfo(IntPtr Handle, string Title, uint ProcessId);

    public static List<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();
        var shellWindow = GetShellWindow();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindowVisible(hWnd)) return true;

            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
            if (cloaked) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            windows.Add(new WindowInfo(hWnd, title, pid));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static List<WindowInfo> GetProcessWindows(uint processId)
    {
        var windows = new List<WindowInfo>();
        var shellWindow = GetShellWindow();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != processId) return true;

            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
            if (cloaked) return true;

            var classBuf = new StringBuilder(64);
            GetClassNameW(hWnd, classBuf, classBuf.Capacity);
            if (_ignoredClasses.Contains(classBuf.ToString())) return true;

            int length = GetWindowTextLength(hWnd);
            string title = "";
            if (length > 0)
            {
                var sb = new StringBuilder(length + 1);
                GetWindowTextW(hWnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            windows.Add(new WindowInfo(hWnd, title, pid));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool TryGetWindowRect(IntPtr hwnd, out int x, out int y, out int width, out int height)
    {
        if (GetWindowRect(hwnd, out RECT rect))
        {
            x = rect.Left;
            y = rect.Top;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return true;
        }
        x = y = width = height = 0;
        return false;
    }
}

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

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static readonly HashSet<string> _ignoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Progman", "WorkerW",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    private const uint GW_OWNER = 4;
    private const uint GA_ROOTOWNER = 3;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    public struct RECT { public int Left, Top, Right, Bottom; }

    public record WindowInfo(IntPtr Handle, string Title, uint ProcessId);

    public static bool IsResoniteWindow(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return title.Equals("Resonite", StringComparison.OrdinalIgnoreCase) ||
               title.StartsWith("Resonite ", StringComparison.OrdinalIgnoreCase);
    }

    public static List<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();
        var shellWindow = GetShellWindow();
        _titleBuf ??= new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindowVisible(hWnd)) return true;

            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
            if (cloaked) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            _titleBuf.Clear().EnsureCapacity(length + 1);
            GetWindowTextW(hWnd, _titleBuf, _titleBuf.Capacity);
            string title = _titleBuf.ToString();

            if (string.IsNullOrWhiteSpace(title)) return true;
            if (!IsStandaloneTopLevelWindow(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            windows.Add(new WindowInfo(hWnd, title, pid));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    [ThreadStatic] private static StringBuilder _classBuf;
    [ThreadStatic] private static StringBuilder _titleBuf;

    public static List<WindowInfo> GetProcessWindows(uint processId)
    {
        var windows = new List<WindowInfo>();
        var shellWindow = GetShellWindow();
        _classBuf ??= new StringBuilder(64);
        _titleBuf ??= new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != processId) return true;

            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
            if (cloaked) return true;

            _classBuf.Clear();
            GetClassNameW(hWnd, _classBuf, _classBuf.Capacity);
            if (_ignoredClasses.Contains(_classBuf.ToString())) return true;
            if (!IsStandaloneTopLevelWindow(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            string title = "";
            if (length > 0)
            {
                _titleBuf.Clear().EnsureCapacity(length + 1);
                GetWindowTextW(hWnd, _titleBuf, _titleBuf.Capacity);
                title = _titleBuf.ToString();
            }

            windows.Add(new WindowInfo(hWnd, title, pid));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool IsStandaloneTopLevelWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd)) return false;

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

        if ((style & WS_CHILD) != 0) return false;

        IntPtr owner = GetWindow(hwnd, GW_OWNER);
        if (owner != IntPtr.Zero) return false;

        IntPtr rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
        if (rootOwner != IntPtr.Zero && rootOwner != hwnd) return false;

        bool toolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
        bool appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (toolWindow && !appWindow) return false;

        return true;
    }

    public static bool TryValidateStandaloneProcessWindow(
        IntPtr hwnd,
        uint expectedProcessId,
        out string title,
        out string reason)
    {
        title = "";
        reason = "";

        if (hwnd == IntPtr.Zero) { reason = "zero hwnd"; return false; }
        if (!IsWindow(hwnd)) { reason = "invalid hwnd"; return false; }
        if (!IsWindowVisible(hwnd)) { reason = "not visible"; return false; }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (expectedProcessId != 0 && pid != expectedProcessId)
        {
            reason = $"pid mismatch expected={expectedProcessId} actual={pid}";
            return false;
        }

        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
        if (cloaked) { reason = "cloaked"; return false; }

        if (!IsStandaloneTopLevelWindow(hwnd))
        {
            reason = "not standalone top-level";
            return false;
        }

        int length = GetWindowTextLength(hwnd);
        if (length > 0)
        {
            _titleBuf ??= new StringBuilder(256);
            _titleBuf.Clear().EnsureCapacity(length + 1);
            GetWindowTextW(hwnd, _titleBuf, _titleBuf.Capacity);
            title = _titleBuf.ToString();
        }

        return true;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
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

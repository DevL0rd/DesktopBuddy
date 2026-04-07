using System;
using System.Runtime.InteropServices;
using System.Text;
using ResoniteModLoader;

namespace DesktopBuddy;

public static class WindowIconExtractor
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCL_HICON = -14;
    private const int GCL_HICONSM = -34;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    public static byte[] GetIconRGBA(IntPtr hwnd, out int width, out int height)
    {
        width = height = 0;

        IntPtr hIcon = IntPtr.Zero;
        var swIcon = System.Diagnostics.Stopwatch.StartNew();
        IntPtr resultBig = SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out hIcon);
        if (resultBig == IntPtr.Zero && hIcon == IntPtr.Zero)
        {
            long ms = swIcon.ElapsedMilliseconds;
            if (ms >= 180)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                string name = "unknown";
                try { name = System.Diagnostics.Process.GetProcessById((int)pid)?.ProcessName ?? "unknown"; } catch (Exception ex) { Log.Msg($"[IconExtractor] Process name lookup failed for pid={pid}: {ex.Message}"); }
                Log.Msg($"[IconExtractor] WM_GETICON timed out after {ms}ms for hwnd={hwnd} pid={pid} process={name}");
            }
            SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out hIcon);
        }
        if (hIcon == IntPtr.Zero)
            hIcon = GetClassLongPtr(hwnd, GCL_HICON);
        if (hIcon == IntPtr.Zero)
            hIcon = GetClassLongPtr(hwnd, GCL_HICONSM);
        if (hIcon == IntPtr.Zero)
            return null;

        return ExtractIconPixels(hIcon, out width, out height, destroyIcon: false);
    }

    public static byte[] GetLargeIconRGBA(IntPtr hwnd, out int width, out int height, int requestedSize = 128)
    {
        width = height = 0;
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return GetIconRGBA(hwnd, out width, out height);

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return GetIconRGBA(hwnd, out width, out height);

            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            bool ok = QueryFullProcessImageNameW(hProc, 0, sb, ref size);
            CloseHandle(hProc);

            if (!ok || size == 0) return GetIconRGBA(hwnd, out width, out height);

            string exePath = sb.ToString();
            Log.Msg($"[IconExtractor] Exe path for hwnd={hwnd}: {exePath}");

            var icons = new IntPtr[1];
            var ids = new uint[1];
            uint count = PrivateExtractIconsW(exePath, 0, requestedSize, requestedSize, icons, ids, 1, 0);
            if (count == 0 || icons[0] == IntPtr.Zero)
            {
                Log.Msg($"[IconExtractor] PrivateExtractIcons returned 0, falling back to WM_GETICON");
                return GetIconRGBA(hwnd, out width, out height);
            }

            Log.Msg($"[IconExtractor] Extracted {requestedSize}x{requestedSize} icon from exe");
            var result = ExtractIconPixels(icons[0], out width, out height, destroyIcon: true);
            return result;
        }
        catch (Exception ex)
        {
            Log.Msg($"[IconExtractor] Large icon error: {ex.Message}");
            return GetIconRGBA(hwnd, out width, out height);
        }
    }

    private static byte[] ExtractIconPixels(IntPtr hIcon, out int width, out int height, bool destroyIcon)
    {
        width = height = 0;
        if (hIcon == IntPtr.Zero) return null;

        if (!GetIconInfo(hIcon, out ICONINFO iconInfo))
        {
            if (destroyIcon) DestroyIcon(hIcon);
            return null;
        }

        try
        {
            if (iconInfo.hbmColor == IntPtr.Zero)
                return null;

            var bmp = new BITMAP();
            GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), ref bmp);
            if (bmp.bmWidth <= 0 || bmp.bmHeight <= 0)
                return null;

            width = bmp.bmWidth;
            height = bmp.bmHeight;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            byte[] pixels = new byte[width * height * 4];
            IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height, pixels, ref bmi, 0);
            }
            finally
            {
                DeleteDC(hdc);
            }

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte tmp = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = tmp;
            }

            return pixels;
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (destroyIcon) DestroyIcon(hIcon);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HarmonyLib;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

[HarmonyPatch(typeof(ContextMenu), nameof(ContextMenu.OpenMenu))]
public static class ContextMenuPatch
{
    private const int PAGE_SIZE = 8;

    // Cache: SHA256 of icon pixel data → local:// asset URI
    private static readonly ConcurrentDictionary<string, Uri> _iconCache = new();

    // Cached desktop icon URI (generated once)
    private static Uri _desktopIconUri;
    private static bool _desktopIconGenerated;

    // Exact titles to hide (case-insensitive exact match)
    private static readonly string[] IgnoredExactTitles = { "Resonite" };
    // Substrings to hide (case-insensitive contains)
    private static readonly string[] IgnoredSubstrings = { "vrmonitor", "SteamVR Status", "rainmeter" };

    private static bool ShouldIgnore(string title)
    {
        foreach (var exact in IgnoredExactTitles)
            if (title.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var sub in IgnoredSubstrings)
            if (title.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly FieldInfo _itemsRootField = typeof(ContextMenu)
        .GetField("_itemsRoot", BindingFlags.NonPublic | BindingFlags.Instance);

    private static void ClearMenu(ContextMenu menu)
    {
        var itemsRoot = _itemsRootField?.GetValue(menu) as SyncRef<Slot>;
        itemsRoot?.Target?.DestroyChildren();
    }

    // Monitor enumeration
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private record MonitorInfo(IntPtr Handle, string Name, int Width, int Height);

    private static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(hMon, ref info);
            int w = info.rcMonitor.Right - info.rcMonitor.Left;
            int h = info.rcMonitor.Bottom - info.rcMonitor.Top;
            monitors.Add(new MonitorInfo(hMon, info.szDevice, w, h));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>
    /// Generate a simple 32x32 monitor icon as RGBA bytes.
    /// Blue screen with dark border, small stand at bottom.
    /// </summary>
    private static byte[] GenerateDesktopIcon(int size = 32)
    {
        var pixels = new byte[size * size * 4];
        var border = new byte[] { 40, 40, 50, 255 };     // dark gray border
        var screen = new byte[] { 60, 140, 220, 255 };    // blue screen
        var stand  = new byte[] { 80, 80, 90, 255 };      // stand color

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = (y * size + x) * 4;
                byte[] color;

                // Monitor body: rows 2-22, cols 2-29
                if (y >= 2 && y <= 22 && x >= 2 && x <= 29)
                {
                    // Border (outer 2px)
                    if (y <= 3 || y >= 21 || x <= 3 || x >= 28)
                        color = border;
                    else
                        color = screen;
                }
                // Stand neck: rows 23-25, cols 13-18
                else if (y >= 23 && y <= 25 && x >= 13 && x <= 18)
                {
                    color = stand;
                }
                // Stand base: rows 26-27, cols 10-21
                else if (y >= 26 && y <= 27 && x >= 10 && x <= 21)
                {
                    color = stand;
                }
                else
                {
                    // Transparent
                    pixels[idx] = 0; pixels[idx + 1] = 0; pixels[idx + 2] = 0; pixels[idx + 3] = 0;
                    continue;
                }

                pixels[idx]     = color[0]; // R
                pixels[idx + 1] = color[1]; // G
                pixels[idx + 2] = color[2]; // B
                pixels[idx + 3] = color[3]; // A
            }
        }
        return pixels;
    }

    /// <summary>
    /// Get a StaticTexture2D with the desktop icon. Generates and caches on first call.
    /// </summary>
    private static StaticTexture2D GetDesktopIconTexture(Engine engine, Slot slot)
    {
        try
        {
            var tex = slot.AttachComponent<StaticTexture2D>();

            if (_desktopIconGenerated && _desktopIconUri != null)
            {
                tex.URL.Value = _desktopIconUri;
                DesktopBuddyMod.Msg("[Icon] Using cached desktop icon");
                return tex;
            }

            DesktopBuddyMod.Msg("[Icon] Generating desktop icon bitmap");
            var iconData = GenerateDesktopIcon(32);
            var capturedTex = tex;

            Task.Run(async () =>
            {
                try
                {
                    var bitmap = new Bitmap2D(iconData, 32, 32,
                        Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                    {
                        _desktopIconUri = uri;
                        _desktopIconGenerated = true;
                        DesktopBuddyMod.Msg($"[Icon] Desktop icon saved: {uri}");
                        capturedTex.World.RunInUpdates(0, () =>
                        {
                            if (!capturedTex.IsDestroyed)
                                capturedTex.URL.Value = uri;
                        });
                    }
                }
                catch (Exception ex)
                {
                    DesktopBuddyMod.Msg($"[Icon] Desktop icon save error: {ex.Message}");
                }
            });
            return tex;
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Icon] Desktop icon error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get or create icon texture for a window.
    /// </summary>
    private static StaticTexture2D GetIconTexture(IntPtr hwnd, Engine engine, Slot slot)
    {
        try
        {
            var iconData = WindowIconExtractor.GetIconRGBA(hwnd, out int w, out int h);
            if (iconData == null || w <= 0 || h <= 0) return null;

            var hash = Convert.ToHexString(SHA256.HashData(iconData));

            var tex = slot.AttachComponent<StaticTexture2D>();

            if (_iconCache.TryGetValue(hash, out var cached))
            {
                tex.URL.Value = cached;
                return tex;
            }

            var capturedData = iconData;
            var capturedW = w;
            var capturedH = h;
            var capturedHash = hash;
            var capturedTex = tex;
            Task.Run(async () =>
            {
                try
                {
                    var bitmap = new Bitmap2D(capturedData, capturedW, capturedH,
                        Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                    {
                        _iconCache[capturedHash] = uri;
                        capturedTex.World.RunInUpdates(0, () =>
                        {
                            if (!capturedTex.IsDestroyed)
                                capturedTex.URL.Value = uri;
                        });
                    }
                }
                catch (Exception ex)
                {
                    DesktopBuddyMod.Msg($"[Icon] Save error: {ex.Message}");
                }
            });
            return tex;
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Icon] Error for hwnd={hwnd}: {ex.Message}");
            return null;
        }
    }

    public static void Postfix(ContextMenu __instance)
    {
        DesktopBuddyMod.Msg("[ContextMenu] Postfix fired, adding Desktop item");
        LocaleString label = "Desktop";
        colorX? color = colorX.Cyan;

        // Add desktop icon to the top-level menu item
        var engine = __instance.World.Engine;
        var iconTex = GetDesktopIconTexture(engine, __instance.Slot);

        ContextMenuItem item;
        if (iconTex != null)
            item = __instance.AddItem(in label, (IAssetProvider<ITexture2D>)iconTex, in color);
        else
            item = __instance.AddItem(in label, (Uri)null!, in color);

        item.Button.LocalPressed += (IButton btn, ButtonEventData data) =>
        {
            DesktopBuddyMod.Msg("[ContextMenu] Desktop item pressed, showing picker");
            ShowPickerPage(__instance, 0);
        };
    }

    private static void ShowPickerPage(ContextMenu menu, int page)
    {
        DesktopBuddyMod.Msg($"[ContextMenu] ShowPickerPage page={page}");
        ClearMenu(menu);
        var world = menu.World;
        var engine = world.Engine;

        // Build combined list: monitors first, then windows
        var entries = new List<(string label, colorX color, Action action, IntPtr hwnd)>();

        // Monitors
        var monitors = GetMonitors();
        DesktopBuddyMod.Msg($"[ContextMenu] Found {monitors.Count} monitors");
        for (int i = 0; i < monitors.Count; i++)
        {
            var mon = monitors[i];
            int idx = i;
            entries.Add(($"Monitor {idx + 1} ({mon.Width}x{mon.Height})",
                new colorX(0.1f, 0.25f, 0.4f, 1f),
                () => { menu.Close(); DesktopBuddyMod.SpawnStreaming(world, IntPtr.Zero, $"Monitor {idx + 1}", mon.Handle); },
                IntPtr.Zero));
        }

        // Windows
        var allWindows = WindowEnumerator.GetOpenWindows();
        DesktopBuddyMod.Msg($"[ContextMenu] Found {allWindows.Count} windows");
        foreach (var win in allWindows)
        {
            if (ShouldIgnore(win.Title)) continue;
            var handle = win.Handle;
            var title = win.Title;
            string display = title.Length > 30 ? title[..27] + "..." : title;
            entries.Add((display,
                new colorX(0.15f, 0.15f, 0.25f, 1f),
                () => { menu.Close(); DesktopBuddyMod.SpawnStreaming(world, handle, title); },
                handle));
        }

        int totalPages = (entries.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        int start = page * PAGE_SIZE;
        int end = Math.Min(start + PAGE_SIZE, entries.Count);

        DesktopBuddyMod.Msg($"[ContextMenu] Showing entries {start}-{end} of {entries.Count} (page {page + 1}/{totalPages})");

        for (int i = start; i < end; i++)
        {
            var entry = entries[i];
            LocaleString lbl = entry.label;
            colorX? c = entry.color;
            var act = entry.action;

            StaticTexture2D iconTex = null;
            if (entry.hwnd != IntPtr.Zero)
                iconTex = GetIconTexture(entry.hwnd, engine, menu.Slot);

            ContextMenuItem mi;
            if (iconTex != null)
                mi = menu.AddItem(in lbl, (IAssetProvider<ITexture2D>)iconTex, in c);
            else
                mi = menu.AddItem(in lbl, (Uri)null!, in c);
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => act();
        }

        // Pagination controls
        if (page > 0)
        {
            LocaleString lbl = $"< Prev (Page {page}/{totalPages})";
            colorX? c = new colorX(0.3f, 0.3f, 0.1f, 1f);
            var mi = menu.AddItem(in lbl, (Uri)null!, in c);
            int prev = page - 1;
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => ShowPickerPage(menu, prev);
        }
        if (page < totalPages - 1)
        {
            LocaleString lbl = $"Next > (Page {page + 2}/{totalPages})";
            colorX? c = new colorX(0.3f, 0.3f, 0.1f, 1f);
            var mi = menu.AddItem(in lbl, (Uri)null!, in c);
            int next = page + 1;
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => ShowPickerPage(menu, next);
        }
    }
}

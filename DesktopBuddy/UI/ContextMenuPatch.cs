using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HarmonyLib;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public static class ContextMenuPatch
{
    private const int PAGE_SIZE = 8;
    private const string DesktopIconFileName = "icon_transparent.png";

    private static readonly ConcurrentDictionary<IntPtr, Uri> _iconCache = new();
    private static readonly ConcurrentDictionary<IntPtr, byte> _iconCacheRequests = new();

    private static Uri _desktopIconUri;
    private static bool _desktopIconLoaded;

    private static readonly string[] IgnoredSubstrings = { "vrmonitor", "SteamVR Status", "rainmeter" };

    private enum MenuOptions
    {
        Default,
        Locomotion,
        Grabbing,
        LaserGrab,
        HandGrab
    }

    private static bool ShouldIgnore(string title)
    {
        if (WindowEnumerator.IsResoniteWindow(title)) return true;
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

    internal static StaticTexture2D GetDesktopIconTexture(Engine engine, Slot slot)
    {
        try
        {
            var tex = TextureProviderSettings.ClampWrap(slot.AttachComponent<StaticTexture2D>());

            if (_desktopIconLoaded && _desktopIconUri != null)
            {
                tex.URL.Value = _desktopIconUri;
                DesktopBuddyMod.Msg("[Icon] Using cached desktop icon");
                return tex;
            }

            var iconPath = Path.Combine(Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty, DesktopIconFileName);
            if (!File.Exists(iconPath))
            {
                DesktopBuddyMod.Msg($"[Icon] Desktop icon file not found: {iconPath}");
                return tex;
            }

            var capturedTex = tex;

            Task.Run(async () =>
            {
                try
                {
                    var bitmap = Bitmap2D.Load(iconPath, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                    {
                        _desktopIconUri = uri;
                        _desktopIconLoaded = true;
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

    internal static StaticTexture2D GetIconTexture(IntPtr hwnd, Engine engine, Slot slot)
    {
        try
        {
            if (_iconCache.TryGetValue(hwnd, out var cached))
            {
                var tex = TextureProviderSettings.ClampWrap(slot.AttachComponent<StaticTexture2D>());
                tex.URL.Value = cached;
                return tex;
            }

            var capturedHwnd = hwnd;
            if (!_iconCacheRequests.TryAdd(capturedHwnd, 0))
                return null;

            Task.Run(async () =>
            {
                try
                {
                    var iconData = WindowIconExtractor.GetIconRGBA(capturedHwnd, out int w, out int h);
                    if (iconData == null || w <= 0 || h <= 0)
                        return;

                    var bitmap = new Bitmap2D(iconData, w, h,
                        Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                        _iconCache[capturedHwnd] = uri;
                }
                catch (Exception ex)
                {
                    DesktopBuddyMod.Msg($"[Icon] Save error: {ex.Message}");
                }
            });
            return null;
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Icon] Error for hwnd={hwnd}: {ex.Message}");
            return null;
        }
    }
    private static void ShowPickerPage(ContextMenu menu, int page)
    {
        if (DesktopBuddyPlatform.IsLinuxProton)
        {
            ShowLinuxPickerPage(menu);
            return;
        }

        DesktopBuddyMod.Msg($"[ContextMenu] ShowPickerPage page={page}");
        ClearMenu(menu);
        var world = menu.World;
        var engine = world.Engine;

        var entries = new List<(string label, colorX color, Action action, IntPtr hwnd)>();

        var monitors = GetMonitors();
        DesktopBuddyMod.Msg($"[ContextMenu] Found {monitors.Count} monitors");
        for (int i = 0; i < monitors.Count; i++)
        {
            var mon = monitors[i];
            int idx = i;
            entries.Add(($"Monitor {idx + 1} ({mon.Width}x{mon.Height})",
                new colorX(0.1f, 0.25f, 0.4f, 1f),
                () => { menu.Close(); DesktopBuddyMod.SpawnStreaming(world, IntPtr.Zero, $"Monitor {idx + 1}", mon.Handle, monitorIndex: idx); },
                IntPtr.Zero));
        }

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

    private static void ShowLinuxPickerPage(ContextMenu menu)
    {
        DesktopBuddyMod.Msg("[ContextMenu] Showing Linux Proton picker actions");
        ClearMenu(menu);

        LocaleString pickerLabel = "Open Desktop Picker";
        colorX? pickerColor = new colorX(0.1f, 0.35f, 0.35f, 1f);
        var picker = menu.AddItem(in pickerLabel, (Uri)null!, in pickerColor);
        picker.Button.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            menu.Close();
            OpenLinuxPortalPickerThenSpawn(menu.World);
        };
    }

    private static void OpenLinuxPortalPickerThenSpawn(World world)
    {
        DesktopBuddyMod.Msg("[ContextMenu] Opening Linux portal picker before spawning DesktopBuddy");
        Task.Run(() =>
        {
            try
            {
                using var bridge = new LinuxNativeBridge();
                int status = bridge.SelectStream(out var selection);
                if (status != 0 || selection.Status != 0 || selection.NodeId == 0)
                {
                    DesktopBuddyMod.Msg($"[ContextMenu] Linux portal selection failed status={status} selectionStatus={selection.Status} node={selection.NodeId}");
                    return;
                }

                int width = selection.Width > 0 ? checked((int)selection.Width) : 1280;
                int height = selection.Height > 0 ? checked((int)selection.Height) : 720;
                LinuxPortalSelectionStore.Set(new LinuxPortalSelection(selection.NodeId, width, height));
                DesktopBuddyMod.Msg($"[ContextMenu] Linux portal selected node={selection.NodeId} size={width}x{height}");
                world.RunInUpdates(0, () => DesktopBuddyMod.SpawnStreaming(world, IntPtr.Zero, "Linux Desktop"));
            }
            catch (Exception ex)
            {
                DesktopBuddyMod.Msg($"[ContextMenu] Linux portal picker error: {ex}");
            }
        });
    }


    [HarmonyPatch(typeof(InteractionHandler), "OpenContextMenu")]
    private class ContextMenuOpenMenuPatch
    {
        public static void Postfix(InteractionHandler __instance, MenuOptions options)
        {
            try
            {
                if (__instance == null || !__instance.IsOwnedByLocalUser)
                    return;

                ContextMenu ctx = __instance.ContextMenu;
                if (ctx == null)
                    return;

                if (options == MenuOptions.Default)
                {
                    if (DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.ShowContextMenuItem) == false)
                        return;

                    DesktopBuddyMod.Msg("[ContextMenu] Postfix fired, adding Desktop item");
                    LocaleString label = "Desktop";
                    colorX? color = colorX.Cyan;

                    var engine = __instance.World.Engine;
                    var iconTex = GetDesktopIconTexture(engine, __instance.Slot);

                    ContextMenuItem item;
                    if (iconTex != null)
                        item = ctx.AddItem(in label, (IAssetProvider<ITexture2D>)iconTex, in color);
                    else
                        item = ctx.AddItem(in label, (Uri)null!, in color);

                    item.Button.LocalPressed += (IButton btn, ButtonEventData data) =>
                    {
                        try
                        {
                            if (DesktopBuddyMod.ShowSetupNoticeFromDesktopClick(__instance.World))
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Setup notice shown from Desktop item");
                                ctx.Close();
                                return;
                            }

                            if (DesktopBuddyPlatform.IsLinuxProton)
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Linux Desktop item pressed, opening portal picker");
                                ctx.Close();
                                OpenLinuxPortalPickerThenSpawn(__instance.World);
                            }
                            else
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Desktop item pressed, showing picker");
                                ShowPickerPage(ctx, 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            DesktopBuddyMod.Msg($"[ContextMenu] Desktop item pressed error: {ex}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                DesktopBuddyMod.Msg($"[ContextMenu] Postfix error: {ex}");
            }
        }
    }
}

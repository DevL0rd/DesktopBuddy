using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void ProcessWindowEvents(World world)
    {
        int pending = _windowEvents.Count;
        for (int processed = 0; processed < pending && _windowEvents.TryDequeue(out var evt); processed++)
        {
            if (evt.Session.Cleaned || evt.Session.Root == null || evt.Session.Root.IsDestroyed) continue;
            if (evt.Session.Root.World != world)
            {
                _windowEvents.Enqueue(evt);
                continue;
            }

            switch (evt.EventType)
            {
                case WindowEventType.TitleChanged:
                    evt.Session.LastTitle = evt.Title;
                    if (evt.Session.TitleText != null && !evt.Session.TitleText.IsDestroyed)
                        evt.Session.TitleText.Text.Value = evt.Title;
                    if (evt.Session.Root != null && !evt.Session.Root.IsDestroyed)
                        evt.Session.Root.Name = $"Desktop: {evt.Title}";
                    break;

                case WindowEventType.NewTopLevelWindow:
                    if (!(Config?.GetValue(SpawnNewWindowsInGame) ?? true))
                    {
                        Msg($"[WindowPoller] Ignored new window hwnd={evt.WindowHwnd} title='{evt.Title}': automatic new-window spawning disabled");
                        break;
                    }

                    if (!WindowEnumerator.TryValidateStandaloneProcessWindow(
                            evt.WindowHwnd,
                            0,
                            out string currentTitle,
                            out string validationReason))
                    {
                        Msg($"[WindowPoller] Ignored new window hwnd={evt.WindowHwnd} title='{evt.Title}': {validationReason}");
                        break;
                    }

                    var spawnTitle = !string.IsNullOrWhiteSpace(currentTitle) ? currentTitle : evt.Title;
                    bool spawnPrivate = Config?.GetValue(SpawnNewWindowsPrivate) ?? true;
                    Msg($"[WindowPoller] Detected new top-level window: hwnd={evt.WindowHwnd} title='{spawnTitle}' private={spawnPrivate}");
                    SpawnStreaming(evt.Session.Root.World, evt.WindowHwnd, spawnTitle, startPrivate: spawnPrivate);
                    break;
            }
        }
    }
}

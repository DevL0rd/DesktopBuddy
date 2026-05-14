using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void ProcessWindowEvents(World world)
    {
        while (_windowEvents.TryDequeue(out var evt))
        {
            if (evt.Session.Cleaned || evt.Session.Root == null || evt.Session.Root.IsDestroyed) continue;
            if (evt.Session.Root.World != world) continue;

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
                    if (!WindowEnumerator.TryValidateStandaloneProcessWindow(
                            evt.WindowHwnd,
                            evt.Session.ProcessId,
                            out string currentTitle,
                            out string validationReason))
                    {
                        Msg($"[WindowPoller] Ignored new window hwnd={evt.WindowHwnd} title='{evt.Title}': {validationReason}");
                        break;
                    }

                    var spawnTitle = !string.IsNullOrWhiteSpace(currentTitle) ? currentTitle : evt.Title;
                    Msg($"[WindowPoller] Detected new top-level window: hwnd={evt.WindowHwnd} title='{spawnTitle}'");
                    SpawnStreaming(evt.Session.Root.World, evt.WindowHwnd, spawnTitle);
                    break;
            }
        }
    }
}

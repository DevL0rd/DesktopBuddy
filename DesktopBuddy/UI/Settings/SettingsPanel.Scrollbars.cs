using Elements.Core;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static UIBuilder BeginRoundedScroll(UIBuilder ui, SettingsPanelState state, string name, float minHeight, Alignment alignment, out ScrollRect scroll, colorX? frameTint = null)
    {
        return UiScrollbars.BeginRoundedScroll(
            ui,
            state.Canvas.World,
            state.Scrollbars,
            () => ScheduleScrollbarGeometryUpdate(state),
            SettingsScrollbarStyle,
            name,
            minHeight,
            alignment,
            out scroll,
            frameTint);
    }

    private static void ScheduleScrollbarGeometryUpdate(SettingsPanelState state)
    {
        var world = state?.OwnerRoot?.World;
        if (world == null)
            return;

        world.RunInUpdates(2, () => UpdateScrollbarGeometry(state));
    }

    private static void UpdateScrollbarGeometry(SettingsPanelState state)
    {
        if (state?.Scrollbars == null)
            return;

        UiScrollbars.UpdateGeometry(state.Scrollbars);
    }
}

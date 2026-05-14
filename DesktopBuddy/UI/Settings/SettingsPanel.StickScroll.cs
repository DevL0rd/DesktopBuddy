namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void StartSettingsStickScrollLoop(SettingsPanelState state)
    {
        if (state == null)
            return;

        UiStickScroll.StartLoop(
            state.OwnerRoot,
            state.SurfaceSlot,
            state.RenderRoot,
            () => ++state.StickScrollGeneration,
            () => state.StickScrollGeneration,
            SettingsStickScrollDeadzone,
            SettingsStickScrollPixelsPerTick);
    }

    private static void ProcessSettingsStickScroll(SettingsPanelState state)
    {
        if (state?.OwnerRoot?.World == null)
            return;

        UiStickScroll.Process(
            state.OwnerRoot.World,
            state.RenderRoot,
            SettingsStickScrollDeadzone,
            SettingsStickScrollPixelsPerTick);
    }
}

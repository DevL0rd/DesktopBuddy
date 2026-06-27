using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void BuildGeneralTab(UIBuilder ui, SettingsPanelState state)
    {
        AddSectionHeader(ui, "General");
        AddCheckbox(ui, state, "Show in context menu", Config?.GetValue(ShowContextMenuItem) ?? true, value =>
        {
            SaveConfigValue(ShowContextMenuItem, value);
        });
        AddCheckbox(ui, state, "Throw to destroy", Config?.GetValue(ThrowToDestroy) ?? true, value =>
        {
            SaveConfigValue(ThrowToDestroy, value);
        });
        AddCheckbox(ui, state, "Dynamic lights", Config?.GetValue(DynamicLightsEnabled) ?? false, value =>
        {
            SaveConfigValue(DynamicLightsEnabled, value);
        });

        AddSectionHeader(ui, "Auto-Spawned Windows");
        AddCheckbox(ui, state, "Auto-open new app windows", Config?.GetValue(SpawnNewWindowsInGame) ?? true, value =>
        {
            SaveConfigValue(SpawnNewWindowsInGame, value);
        });
        AddCheckbox(ui, state, "Auto-opened windows start private", Config?.GetValue(SpawnNewWindowsPrivate) ?? true, value =>
        {
            SaveConfigValue(SpawnNewWindowsPrivate, value);
        });

        AddSectionHeader(ui, "Manually Spawned Windows");
        AddCheckbox(ui, state, "Windows I open start private", Config?.GetValue(NewWindowsStartPrivate) ?? true, value =>
        {
            SaveConfigValue(NewWindowsStartPrivate, value);
        });

        AddSectionHeader(ui, "Audio");
        AddCheckboxWithBadge(ui, state, "Spatial audio", "(Experimental)", SettingsExperimentalOrange, Config?.GetValue(SpatialAudioEnabled) ?? false,
            value => SaveConfigValue(SpatialAudioEnabled, value));
    }
}

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

        AddSectionHeader(ui, "Audio");
        AddCheckboxWithBadge(ui, state, "Spatial audio", "(Experimental)", SettingsExperimentalOrange, Config?.GetValue(SpatialAudioEnabled) ?? false,
            value => SaveConfigValue(SpatialAudioEnabled, value));
    }
}

using System;
using Elements.Core;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void AddCheckbox(UIBuilder ui, SettingsPanelState state, string label, bool initial, Action<bool> changed)
    {
        ui.Style.MinHeight = 54f;
        ui.Style.PreferredHeight = 54f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 17f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 74f;
        ui.Style.PreferredWidth = 74f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var toggle = ui.Button(initial ? "On" : "Off", initial ? SettingsAccentSoft : SettingsPanelSoft);
        StyleSettingsButton(toggle, initial);
        bool lastApplied = initial;
        UpdateToggleButton(toggle, lastApplied);
        toggle.LocalPressed += (_, _) =>
        {
            lastApplied = !lastApplied;
            UpdateToggleButton(toggle, lastApplied);
            changed?.Invoke(lastApplied);
        };
        ui.NestOut();
    }

    private static void AddCheckboxWithBadge(UIBuilder ui, SettingsPanelState state, string label, string badge, colorX badgeColor, bool initial, Action<bool> changed)
    {
        ui.Style.MinHeight = 54f;
        ui.Style.PreferredHeight = 54f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 17f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 146f;
        ui.Style.PreferredWidth = 146f;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        var badgePill = ui.Image(badgeColor);
        StyleBadgePill(badgePill, badgeColor);
        ui.NestInto(badgePill.RectTransform);
        ui.LayoutTarget = badgePill.Slot;
        var badgeLayout = ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        badgeLayout.ForceExpandWidth.Value = true;
        badgeLayout.ForceExpandHeight.Value = true;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        ui.Style.FlexibleWidth = 1f;
        var badgeText = ui.Text(badge, bestFit: true, alignment: Alignment.MiddleCenter);
        badgeText.Size.Value = 14f;
        badgeText.Color.Value = SettingsText;
        ui.NestOut();

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 74f;
        ui.Style.PreferredWidth = 74f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var toggle = ui.Button(initial ? "On" : "Off", initial ? SettingsAccentSoft : SettingsPanelSoft);
        StyleSettingsButton(toggle, initial);
        bool lastApplied = initial;
        UpdateToggleButton(toggle, lastApplied);
        toggle.LocalPressed += (_, _) =>
        {
            lastApplied = !lastApplied;
            UpdateToggleButton(toggle, lastApplied);
            changed?.Invoke(lastApplied);
        };
        ui.NestOut();
    }
}

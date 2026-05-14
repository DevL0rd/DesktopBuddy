using System;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void AddButtonRow(UIBuilder ui, SettingsPanelState state, string label, Action pressed, bool selected = false, string buttonLabel = null)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 116f;
        ui.Style.PreferredWidth = 116f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var btn = ui.Button(buttonLabel ?? label, selected ? SettingsAccent : SettingsPanelSoft);
        StyleSettingsButton(btn, selected);
        btn.LocalPressed += (_, _) =>
        {
            pressed?.Invoke();
            RebuildSettingsContent(state, null);
        };
        ui.NestOut();
    }

    private static void AddLinkButtonRow(UIBuilder ui, SettingsPanelState state, string label, string url, string buttonLabel = null)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 116f;
        ui.Style.PreferredWidth = 116f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var btn = ui.Button(buttonLabel ?? label, SettingsPanelSoft);
        StyleSettingsButton(btn, false);

        var link = btn.Slot.AttachComponent<Hyperlink>();
        link.URL.Value = new Uri(url);
        link.OpenOnce.Value = false;
        link.Reason.Value = "DesktopBuddy";
        ui.NestOut();
    }
}

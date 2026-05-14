using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void DestroyLayoutControllers(Slot slot)
    {
        UiPrimitiveStyles.DestroyLayoutControllers(slot);
    }

    private static void AddSectionHeader(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var bg = ui.Image(new colorX(0.035f, 0.04f, 0.052f, 0.58f));
        bg.Sprite.Target = CreateRoundedSprite(bg.Slot, ui.Root.World, 16f);
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 14f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleLeft);

        ui.Style.MinWidth = 5f;
        ui.Style.PreferredWidth = 5f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = -1f;
        var accent = ui.Image(SettingsAccent);
        accent.Sprite.Target = CreateRoundedSprite(accent.Slot, ui.Root.World, 5f);
        accent.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ApplyPurpleBlueGradient(accent, 5f, 1f, interactionTarget: false);

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        ui.Style.FlexibleWidth = 1f;
        var label = ui.Text(text, bestFit: true, alignment: Alignment.MiddleLeft);
        label.Size.Value = 23f;
        label.Color.Value = SettingsText;
        ui.NestOut();
    }

    private static void AddBodyText(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        ui.Style.FlexibleWidth = 1f;
        var label = ui.Text(text ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        label.Size.Value = 16f;
        label.Color.Value = SettingsSubtext;
    }

    private static void AddInfoRow(UIBuilder ui, SettingsPanelState state, string label, string value)
    {
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.Style.FlexibleWidth = 1f;
        var bg = ui.Image(SettingsPanel);
        bg.Sprite.Target = CreateRoundedSprite(bg.Slot, state.Canvas.World, 13f);
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(12f, paddingTop: 7f, paddingRight: 12f, paddingBottom: 7f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 32f;
        ui.Style.PreferredHeight = 32f;
        var name = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        name.Size.Value = 16f;
        name.Color.Value = SettingsSubtext;

        ui.Style.FlexibleWidth = 1f;
        var val = ui.Text(value ?? "", bestFit: true, alignment: Alignment.MiddleRight);
        val.Size.Value = 16f;
        val.Color.Value = SettingsText;
        ui.NestOut();
    }

    private static void AddStatusRow(UIBuilder ui, SettingsPanelState state, string label, string status, colorX badgeColor)
    {
        const float badgeWidth = 76f;
        const float badgeHeight = 26f;

        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(10f, paddingTop: 7f, paddingRight: 10f, paddingBottom: 7f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 16f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = badgeWidth;
        ui.Style.PreferredWidth = badgeWidth;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        var badgePill = ui.Image(badgeColor);
        var badgeElement = badgePill.Slot.GetComponent<LayoutElement>() ?? badgePill.Slot.AttachComponent<LayoutElement>();
        badgeElement.MinWidth.Value = badgeWidth;
        badgeElement.PreferredWidth.Value = badgeWidth;
        badgeElement.FlexibleWidth.Value = -1f;
        badgeElement.MinHeight.Value = badgeHeight;
        badgeElement.PreferredHeight.Value = badgeHeight;
        badgeElement.FlexibleHeight.Value = -1f;
        StyleBadgePill(badgePill, badgeColor);
        ui.NestInto(badgePill.RectTransform);
        ui.LayoutTarget = badgePill.Slot;
        var badgeLayout = ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        badgeLayout.ForceExpandWidth.Value = true;
        badgeLayout.ForceExpandHeight.Value = true;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        ui.Style.FlexibleWidth = 1f;
        var badgeText = ui.Text(status ?? "", bestFit: true, alignment: Alignment.MiddleCenter);
        badgeText.Size.Value = 13f;
        badgeText.Color.Value = SettingsText;
        ui.NestOut();
        ui.NestOut();
    }
}

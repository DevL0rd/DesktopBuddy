using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static SpriteProvider CreateRoundedSprite(Slot slot, World world, float fixedSize)
    {
        return UiPrimitiveStyles.CreateRoundedSprite(slot, world, fixedSize);
    }

    private static GradientImage ApplyPurpleBlueGradient(Image image, float fixedSize, float alpha, bool interactionTarget)
    {
        return UiPrimitiveStyles.ApplyDiagonalGradient(image, fixedSize, alpha, interactionTarget, SettingsGradientPalette);
    }

    private static void ApplyButtonGradient(Button button, bool selected)
    {
        var bg = button?.Slot?.GetComponent<Image>();
        if (bg == null)
            return;

        if (!selected)
        {
            var existing = button.Slot.GetComponent<GradientImage>();
            if (existing != null)
                existing.Enabled = false;
            bg.Enabled = true;
            bg.InteractionTarget.Value = true;
            bg.Tint.Value = SettingsPanelSoft;
            if (button.ColorDrivers.Count > 0)
            {
                button.ColorDrivers[0].ColorDrive.Target = bg.Tint;
                button.ColorDrivers[0].SetColors(SettingsPanelSoft);
            }
            return;
        }

        var gradient = ApplyPurpleBlueGradient(bg, selected ? 14f : 12f, selected ? 0.98f : 0.54f, interactionTarget: true);
        if (gradient == null)
            return;

        gradient.Enabled = true;

        UiPrimitiveStyles.ConfigureGradientButtonDriver(button, 1, gradient.TintTopLeft, SettingsGradientPurple.SetA(selected ? 0.98f : 0.54f));
        UiPrimitiveStyles.ConfigureGradientButtonDriver(button, 2, gradient.TintTopRight, SettingsGradientMid.SetA(selected ? 0.98f : 0.54f));
        UiPrimitiveStyles.ConfigureGradientButtonDriver(button, 3, gradient.TintBottomLeft, SettingsGradientMid.SetA(selected ? 0.98f : 0.54f));
        UiPrimitiveStyles.ConfigureGradientButtonDriver(button, 4, gradient.TintBottomRight, SettingsGradientBlue.SetA(selected ? 0.98f : 0.54f));
    }

    private static void StyleBadgePill(Image image, colorX color)
    {
        UiPrimitiveStyles.StyleRoundedPill(image, color, 12f, interactionTarget: false);
    }

    private static void StyleSettingsButton(Button button, bool selected)
    {
        if (button == null) return;

        var bg = button.Slot.GetComponent<Image>();
        if (bg != null)
        {
            bg.Sprite.Target = CreateRoundedSprite(button.Slot, button.Slot.World, selected ? 14f : 12f);
            bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
            bg.Tint.Value = selected ? SettingsAccent : SettingsPanelSoft;
        }
        ApplyButtonGradient(button, selected);

        if (button.ColorDrivers.Count > 0 && !selected)
            button.ColorDrivers[0].SetColors(selected ? SettingsAccent : SettingsPanelSoft);

        if (button.Label != null)
        {
            button.Label.Align = Alignment.MiddleCenter;
            button.Label.Color.Value = SettingsText;
            button.Label.Size.Value = 17f;
        }
    }

    private static void UpdateToggleButton(Button button, bool enabled)
    {
        if (button == null) return;

        var bg = button.Slot.GetComponent<Image>();
        var color = enabled ? SettingsAccent : SettingsPanelSoft;
        if (bg != null)
        {
            bg.Tint.Value = color;
            ApplyButtonGradient(button, enabled);
        }
        if (button.ColorDrivers.Count > 0)
            button.ColorDrivers[0].SetColors(color);
        if (button.Label != null)
        {
            button.Label.Content.Value = enabled ? "On" : "Off";
            button.Label.Color.Value = enabled ? SettingsText : SettingsSubtext;
            button.Label.Align = Alignment.MiddleCenter;
        }
    }

    private static void StyleTextFieldSlot(Slot slot, SettingsPanelState state)
    {
        if (slot == null || state == null) return;
        var bg = slot.GetComponent<Image>();
        if (bg != null)
        {
            bg.Tint.Value = new colorX(0.18f, 0.19f, 0.23f, 0.96f);
            bg.Sprite.Target = CreateRoundedSprite(slot, state.Canvas.World, 12f);
            bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        }
        var text = slot.GetComponentInChildren<Text>();
        if (text != null)
        {
            text.Color.Value = SettingsText;
            text.Size.Value = 16f;
            text.Align = Alignment.MiddleLeft;
            text.RectTransform.AddFixedPadding(18f, 0f, 10f, 0f);
        }
    }
}

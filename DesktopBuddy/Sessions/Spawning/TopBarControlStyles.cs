using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

internal static class TopBarControlStyles
{
    internal static void StyleButton(Button button, UI_TextUnlitMaterial textMaterial, SpriteProvider roundedSprite, UI_UnlitMaterial elementMaterial)
    {
        var textComp = button.Slot.GetComponentInChildren<FrooxEngine.UIX.Text>();
        if (textComp != null)
        {
            textComp.Size.Value = 18f;
            textComp.Color.Value = new colorX(0.92f, 0.93f, 0.98f, 1f);
            textComp.Material.Target = textMaterial;
        }

        var textRenderer = button.Slot.GetComponentInChildren<TextRenderer>();
        if (textRenderer != null)
            textRenderer.Color.Value = new colorX(0.92f, 0.93f, 0.98f, 1f);

        if (button.ColorDrivers.Count > 0)
        {
            var colors = button.ColorDrivers[0];
            colors.NormalColor.Value = new colorX(0.12f, 0.13f, 0.17f, 0.88f);
            colors.HighlightColor.Value = new colorX(0.24f, 0.18f, 0.42f, 0.95f);
            colors.PressColor.Value = new colorX(0.12f, 0.34f, 0.58f, 0.95f);
        }

        var image = button.Slot.GetComponent<Image>();
        if (image == null)
            return;

        image.Tint.Value = new colorX(0.12f, 0.13f, 0.17f, 0.88f);
        image.Sprite.Target = roundedSprite;
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Material.Target = elementMaterial;
    }
}

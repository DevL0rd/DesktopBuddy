using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

internal readonly struct UiGradientPalette
{
    internal UiGradientPalette(colorX start, colorX middle, colorX end)
    {
        Start = start;
        Middle = middle;
        End = end;
    }

    internal colorX Start { get; }
    internal colorX Middle { get; }
    internal colorX End { get; }
}

internal static class UiPrimitiveStyles
{
    internal static void DestroyLayoutControllers(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return;

        foreach (var layout in slot.GetComponents<HorizontalLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<VerticalLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<GridLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<OverlappingLayout>())
            layout.Destroy();
    }

    internal static SpriteProvider CreateRoundedSprite(Slot slot, World world, float fixedSize)
    {
        var sprite = TextureProviderSettings.ClampWrap(slot.GetComponent<SpriteProvider>() ?? slot.AttachComponent<SpriteProvider>());
        sprite.Texture.Target = UIBuilder.GetCircleTexture(world);
        sprite.Borders.Value = float4.One * 0.49f;
        sprite.FixedSize.Value = fixedSize;
        return sprite;
    }

    internal static GradientImage ApplyDiagonalGradient(
        Image image,
        float fixedSize,
        float alpha,
        bool interactionTarget,
        UiGradientPalette palette)
    {
        if (image == null || image.Slot == null || image.Slot.IsDestroyed)
            return null;

        image.Enabled = false;
        image.InteractionTarget.Value = false;
        var gradient = image.Slot.GetComponent<GradientImage>() ?? image.Slot.AttachComponent<GradientImage>();
        gradient.Sprite.Target = CreateRoundedSprite(image.Slot, image.Slot.World, fixedSize);
        gradient.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        gradient.PreserveAspect.Value = false;
        gradient.InteractionTarget.Value = interactionTarget;

        colorX start = palette.Start.SetA(alpha);
        colorX middle = palette.Middle.SetA(alpha);
        colorX end = palette.End.SetA(alpha);
        gradient.TintTopLeft.Value = start;
        gradient.TintBottomLeft.Value = middle;
        gradient.TintTopRight.Value = middle;
        gradient.TintBottomRight.Value = end;
        return gradient;
    }

    internal static void ConfigureGradientButtonDriver(Button button, int index, Sync<colorX> target, colorX color)
    {
        if (button == null || target == null)
            return;

        while (button.ColorDrivers.Count <= index)
            button.ColorDrivers.Add();

        var driver = button.ColorDrivers[index];
        driver.ColorDrive.Target = target;
        driver.SetColors(color);
    }

    internal static void StyleRoundedPill(Image image, colorX color, float fixedSize, bool interactionTarget)
    {
        if (image == null)
            return;

        image.Sprite.Target = CreateRoundedSprite(image.Slot, image.Slot.World, fixedSize);
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Tint.Value = color;
        image.InteractionTarget.Value = interactionTarget;
    }
}

using System;
using System.Collections.Generic;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public class UiScrollbarState
{
    public ScrollRect Scroll;
    public Slot Root;
    public RectTransform ThumbRect;
    public Slider<float> Slider;
    public float TrackPadding = 8f;
    public float TrackHeight;
    public float ThumbHeight;
}

internal readonly struct UiScrollbarStyle
{
    internal UiScrollbarStyle(
        colorX frameTint,
        colorX railTint,
        colorX thumbTint,
        UiGradientPalette thumbGradient)
    {
        FrameTint = frameTint;
        RailTint = railTint;
        ThumbTint = thumbTint;
        ThumbGradient = thumbGradient;
    }

    internal colorX FrameTint { get; }
    internal colorX RailTint { get; }
    internal colorX ThumbTint { get; }
    internal UiGradientPalette ThumbGradient { get; }
}

internal static class UiScrollbars
{
    internal static UIBuilder BeginRoundedScroll(
        UIBuilder ui,
        World world,
        IList<UiScrollbarState> scrollbars,
        Action scheduleGeometryUpdate,
        UiScrollbarStyle style,
        string name,
        float minHeight,
        Alignment alignment,
        out ScrollRect scroll,
        colorX? frameTint = null)
    {
        ui.Style.MinHeight = minHeight;
        ui.Style.PreferredHeight = minHeight;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = minHeight <= 0f ? 1f : -1f;
        var frame = ui.Image(frameTint ?? style.FrameTint);
        frame.Sprite.Target = UiPrimitiveStyles.CreateRoundedSprite(frame.Slot, world, 16f);
        frame.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(frame.RectTransform);
        ui.LayoutTarget = frame.Slot;
        ui.HorizontalLayout(10f, paddingTop: 12f, paddingRight: 10f, paddingBottom: 12f, paddingLeft: 12f, childAlignment: Alignment.MiddleCenter);

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 0f;
        ui.Style.PreferredHeight = 0f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = 1f;
        var viewport = ui.Empty(name + "Viewport");
        scroll = ScrollRect.CreateScrollRect<Image>(viewport, out var content, out var mask, out var viewportGraphic);
        scroll.Alignment = alignment;
        mask.ShowMaskGraphic.Value = false;
        viewportGraphic.Tint.Value = colorX.White;
        viewportGraphic.Sprite.Target = UiPrimitiveStyles.CreateRoundedSprite(viewport, world, 14f);
        viewportGraphic.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        var scrollUi = new UIBuilder(content);
        scrollUi.LayoutTarget = content;
        scrollUi.VerticalLayout(10f, paddingTop: 4f, paddingRight: 4f, paddingBottom: 4f, paddingLeft: 4f, childAlignment: Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        scrollUi.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);

        AddScrollbarSlider(ui, world, scrollbars, scheduleGeometryUpdate, style, name + "Scrollbar", scroll);
        ui.NestOut();
        return scrollUi;
    }

    internal static void UpdateGeometry(IEnumerable<UiScrollbarState> scrollbars)
    {
        if (scrollbars == null)
            return;

        foreach (var bar in scrollbars)
        {
            if (bar?.Root == null || bar.Root.IsDestroyed || bar.Scroll == null || bar.Scroll.IsDestroyed || bar.ThumbRect == null || bar.ThumbRect.IsDestroyed)
                continue;

            var contentRect = bar.Scroll.Slot.GetComponent<RectTransform>()?.LocalComputeRect ?? default;
            var viewportRect = bar.Scroll.Slot.Parent?.GetComponent<RectTransform>()?.LocalComputeRect ?? default;
            float contentHeight = Math.Abs(contentRect.height);
            float viewportHeight = Math.Abs(viewportRect.height);
            bool needsScroll = contentHeight > viewportHeight + 2f && viewportHeight > 1f;
            bar.Root.ActiveSelf = needsScroll;
            if (!needsScroll)
                continue;

            const float padding = 8f;
            float trackHeight = Math.Max(1f, viewportHeight - padding * 2f);
            float thumbHeight = Math.Clamp(trackHeight * viewportHeight / Math.Max(contentHeight, viewportHeight), 34f, trackHeight);
            float sliderValue = bar.Slider == null || bar.Slider.IsDestroyed ? 1f - Math.Clamp(bar.Scroll.NormalizedPosition.Value.y, 0f, 1f) : bar.Slider.Value.Value;
            bar.TrackPadding = padding;
            bar.TrackHeight = trackHeight;
            bar.ThumbHeight = thumbHeight;
            SetThumbValue(bar, sliderValue);
        }
    }

    private static void AddScrollbarSlider(
        UIBuilder ui,
        World world,
        IList<UiScrollbarState> scrollbars,
        Action scheduleGeometryUpdate,
        UiScrollbarStyle style,
        string name,
        ScrollRect scroll)
    {
        ui.Style.MinWidth = 18f;
        ui.Style.PreferredWidth = 18f;
        ui.Style.MinHeight = 0f;
        ui.Style.PreferredHeight = 0f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = 1f;
        var root = ui.Empty(name);
        var hit = root.AttachComponent<Image>();
        hit.Tint.Value = colorX.Clear;

        var slider = root.AttachComponent<Slider<float>>();
        slider.RequireLockInToInteract.Value = true;
        slider.RequireInitialPress.Value = true;
        slider.SlideDirection.Value = Slider<float>.Direction.Vertical;
        slider.AnchorOffset.Value = new float2(0.5f, 0f);
        slider.Min.Value = 0f;
        slider.Max.Value = 1f;
        slider.Value.Value = 1f - Math.Clamp(scroll?.NormalizedPosition.Value.y ?? 0f, 0f, 1f);

        var railSlot = root.AddSlot("Rail");
        var railRect = railSlot.GetComponentOrAttach<RectTransform>();
        railRect.AnchorMin.Value = new float2(0.5f, 0f);
        railRect.AnchorMax.Value = new float2(0.5f, 1f);
        railRect.OffsetMin.Value = new float2(-5f, 8f);
        railRect.OffsetMax.Value = new float2(5f, -8f);
        var rail = railSlot.AttachComponent<Image>();
        rail.Tint.Value = style.RailTint;
        rail.Sprite.Target = UiPrimitiveStyles.CreateRoundedSprite(railSlot, world, 8f);
        rail.NineSliceSizing.Value = NineSliceSizing.FixedSize;

        var thumbSlot = root.AddSlot("Thumb");
        var thumbRect = thumbSlot.GetComponentOrAttach<RectTransform>();
        thumbRect.SetFixedRect(new Rect(-7f, -36f, 14f, 72f), new float2(0.5f, slider.Value.Value));
        var thumb = thumbSlot.AttachComponent<Image>();
        thumb.Tint.Value = style.ThumbTint;
        thumb.Sprite.Target = UiPrimitiveStyles.CreateRoundedSprite(thumbSlot, world, 8f);
        thumb.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        UiPrimitiveStyles.ApplyDiagonalGradient(thumb, 8f, 1f, interactionTarget: false, style.ThumbGradient);

        var barState = new UiScrollbarState
        {
            Scroll = scroll,
            Root = root,
            ThumbRect = thumbRect,
            Slider = slider
        };
        slider.Value.LocalFilter = (candidate, field) => Math.Clamp(candidate, 0f, 1f);
        slider.Value.OnValueChange += (SyncField<float> field) =>
        {
            float clamped = Math.Clamp(field.Value, 0f, 1f);
            SetThumbValue(barState, clamped);
            if (scroll != null && !scroll.IsDestroyed)
            {
                var pos = scroll.NormalizedPosition.Value;
                scroll.NormalizedPosition.Value = new float2(pos.x, 1f - clamped);
            }
        };
        if (scroll != null)
        {
            scroll.NormalizedPosition.OnValueChange += (SyncField<float2> field) =>
            {
                if (slider.IsDestroyed)
                    return;
                float y = 1f - Math.Clamp(field.Value.y, 0f, 1f);
                SetThumbValue(barState, y);
            };
        }

        scrollbars?.Add(barState);
        scheduleGeometryUpdate?.Invoke();
    }

    private static void SetThumbValue(UiScrollbarState bar, float value)
    {
        if (bar?.ThumbRect == null || bar.ThumbRect.IsDestroyed)
            return;

        float clamped = Math.Clamp(value, 0f, 1f);
        float trackHeight = Math.Max(1f, bar.TrackHeight);
        float thumbHeight = Math.Clamp(bar.ThumbHeight <= 0f ? 34f : bar.ThumbHeight, 1f, trackHeight);
        float travel = Math.Max(0f, trackHeight - thumbHeight);
        float thumbBottom = bar.TrackPadding + travel * clamped;
        bar.ThumbRect.SetFixedRect(new Rect(-7f, thumbBottom, 14f, thumbHeight), new float2(0.5f, 0f));
    }
}

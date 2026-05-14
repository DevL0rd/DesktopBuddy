using System;
using System.Globalization;
using Elements.Core;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void AddFloatSlider(UIBuilder ui, SettingsPanelState state, string label, float value, float min, float max, Action<float> changed, bool commitOnReleaseOnly = false, bool wholeNumbers = false)
    {
        value = wholeNumbers ? MathF.Round(value) : value;
        ui.Style.MinHeight = 92f;
        ui.Style.PreferredHeight = 92f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.VerticalLayout(8f, paddingTop: 10f, paddingRight: 14f, paddingBottom: 12f, paddingLeft: 14f, childAlignment: Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);

        ui.Style.MinHeight = 24f;
        ui.Style.PreferredHeight = 24f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        string FormatSliderValue(float v) => wholeNumbers
            ? MathF.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
        var valueLabel = ui.Text($"{label}: {FormatSliderValue(value)}", bestFit: true, alignment: Alignment.MiddleLeft);
        valueLabel.Size.Value = 16f;
        valueLabel.Color.Value = new colorX(0.72f, 0.74f, 0.78f, 1f);

        ui.Style.MinHeight = 36f;
        ui.Style.PreferredHeight = 36f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var slider = ui.Slider<float>(22f, value, min, max, false, out var line, out var fillLine, out var handle);
        line.Tint.Value = SettingsPanelSoft;
        fillLine.Tint.Value = SettingsAccent;
        handle.Tint.Value = SettingsText;
        ApplyPurpleBlueGradient(fillLine, 10f, 0.98f, interactionTarget: false);
        var handleGradient = ApplyPurpleBlueGradient(handle, 18f, 0.98f, interactionTarget: false);
        if (handleGradient != null && slider.ColorDrivers.Count > 0)
            slider.ColorDrivers[0].ColorDrive.Target = handleGradient.TintBottomRight;
        float lastApplied = Math.Clamp(value, min, max);
        float lastCommitted = lastApplied;
        slider.Value.LocalFilter = (candidate, field) =>
        {
            float clamped = Math.Clamp(candidate, min, max);
            if (wholeNumbers)
                clamped = MathF.Round(clamped);
            valueLabel.Content.Value = $"{label}: {FormatSliderValue(clamped)}";
            if (Math.Abs(clamped - lastApplied) > 0.0001f)
            {
                lastApplied = clamped;
                if (!commitOnReleaseOnly)
                    changed?.Invoke(clamped);
            }

            return clamped;
        };
        if (commitOnReleaseOnly)
        {
            slider.IsPressed.OnValueChange += field =>
            {
                if (field.Value)
                    return;
                float valueOnRelease = Math.Clamp(slider.Value.Value, min, max);
                if (wholeNumbers)
                    valueOnRelease = MathF.Round(valueOnRelease);
                if (Math.Abs(valueOnRelease - lastCommitted) <= 0.0001f)
                    return;
                lastCommitted = valueOnRelease;
                changed?.Invoke(valueOnRelease);
            };
        }
        ui.NestOut();
    }
}

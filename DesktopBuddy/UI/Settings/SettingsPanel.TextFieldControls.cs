using System;
using System.Globalization;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void AddIntField(UIBuilder ui, SettingsPanelState state, string label, int value, int min, int max, Action<int> changed)
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

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var parser = ui.IntegerField(min, max, 1, parseContinuously: false);
        StyleTextFieldSlot(parser.TextEditor?.Slot, state);
        parser.ParsedValue.Value = value;
        parser.TextEditor.LocalEditingFinished += editor =>
        {
            if (int.TryParse(editor.TargetString, out int parsed))
                changed?.Invoke(Math.Clamp(parsed, min, max));
        };
        AddCopyButton(ui, state, (parser.TextEditor?.Text.Target as Text)?.Content);
        AddPasteButton(ui, state, parser.TextEditor, pasted =>
        {
            if (!int.TryParse(pasted, out int parsed))
                return;
            parsed = Math.Clamp(parsed, min, max);
            parser.ParsedValue.Value = parsed;
            parser.TextEditor.TargetString = parsed.ToString(CultureInfo.InvariantCulture);
            changed?.Invoke(parsed);
        });
        ui.NestOut();
    }

    private static void AddStringField(UIBuilder ui, SettingsPanelState state, string label, string value, Action<string> changed)
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

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var field = ui.TextField(value ?? "", undo: false, parseRTF: false);
        StyleTextFieldSlot(field.Slot, state);
        field.Editor.Target.LocalEditingFinished += editor =>
        {
            changed?.Invoke(field.TargetString ?? "");
        };
        AddCopyButton(ui, state, field.Text?.Content);
        AddPasteButton(ui, state, field.Editor.Target, pasted =>
        {
            field.TargetString = pasted ?? "";
            changed?.Invoke(field.TargetString ?? "");
        });
        ui.NestOut();
    }

    private static void AddCopyButton(UIBuilder ui, SettingsPanelState state, IField<string> source)
    {
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleHeight = -1f;
        var copy = ui.Button("\u29C9");
        StyleSettingsButton(copy, false);
        if (copy.Label != null)
        {
            copy.Label.Size.Value = 18f;
            copy.Label.Color.Value = SettingsText;
        }
        var copier = copy.Slot.AttachComponent<ButtonClipboardCopyText>();
        copier.Source.Target = source;
    }

    private static void AddPasteButton(UIBuilder ui, SettingsPanelState state, TextEditor editor, Action<string> pasted)
    {
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleHeight = -1f;
        var paste = ui.Button("📋");
        StyleSettingsButton(paste, false);
        if (paste.Label != null)
        {
            paste.Label.Size.Value = 16f;
            paste.Label.Color.Value = SettingsText;
        }
        paste.LocalPressed += (_, _) =>
        {
            try
            {
                var clipboard = state?.OwnerRoot?.World?.InputInterface?.Clipboard;
                if (clipboard == null || !clipboard.ContainsText)
                    return;
                string text = clipboard.GetText().Result ?? "";
                if (editor != null && !editor.IsDestroyed)
                    editor.TargetString = text;
                pasted?.Invoke(text);
            }
            catch (Exception ex)
            {
                Msg($"[Settings] Paste failed: {ex.Message}");
            }
        };
    }
}

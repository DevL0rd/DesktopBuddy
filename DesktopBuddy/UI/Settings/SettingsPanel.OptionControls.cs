using System;
using Elements.Core;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void AddOptionRow(UIBuilder ui, SettingsPanelState state, string label, string current, (string Value, string Label)[] options, Action<string> selected, int? preferredColumns = null, float cellWidth = 126f)
    {
        int columns = EstimateOptionColumns(state, options.Length, cellWidth, preferredColumns);
        int rows = (int)Math.Ceiling(options.Length / (double)columns);
        float rowHeight = Math.Max(62f, rows * 46f + 18f);
        ui.Style.MinHeight = rowHeight;
        ui.Style.PreferredHeight = rowHeight;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(14f, paddingTop: 9f, paddingRight: 12f, paddingBottom: 9f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.MinHeight = 40f;
        ui.Style.PreferredHeight = 40f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = Math.Max(42f, rowHeight - 18f);
        ui.Style.PreferredHeight = Math.Max(42f, rowHeight - 18f);
        var gridRoot = ui.Empty(label + " options");
        ui.NestOut();

        var gridUi = new UIBuilder(gridRoot);
        gridUi.LayoutTarget = gridRoot;
        var grid = gridUi.GridLayout(new float2(cellWidth, 38f), new float2(8f, 8f), Alignment.MiddleRight);
        grid.AlignLastRowIndividually.Value = true;
        var rowUi = new UIBuilder(gridRoot);
        foreach (var option in options)
        {
            rowUi.Style.MinWidth = cellWidth;
            rowUi.Style.PreferredWidth = cellWidth;
            rowUi.Style.MinHeight = 38f;
            rowUi.Style.PreferredHeight = 38f;
            rowUi.Style.FlexibleWidth = -1f;
            var tint = option.Value == current ? new colorX(0.22f, 0.34f, 0.42f, 0.98f) : new colorX(0.13f, 0.135f, 0.155f, 0.94f);
            var btn = rowUi.Button(option.Label, tint);
            StyleSettingsButton(btn, option.Value == current);
            string captured = option.Value;
            btn.LocalPressed += (_, _) =>
            {
                selected?.Invoke(captured);
                RebuildSettingsContent(state, null);
            };
        }
    }

    private static int EstimateOptionColumns(SettingsPanelState state, int optionCount, float cellWidth, int? preferredColumns)
    {
        float available = Math.Max(cellWidth, (state?.ModalWidth ?? 820) - 300f);
        int maxColumns = (int)Math.Floor((available + 8f) / (cellWidth + 8f));
        int columns = preferredColumns.HasValue && preferredColumns.Value <= maxColumns ? preferredColumns.Value : maxColumns;
        return Math.Clamp(columns, 1, Math.Max(1, optionCount));
    }
}

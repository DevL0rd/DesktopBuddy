using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void BuildDebugTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Debug");
        AddButtonRow(ui, state, "Export combined log", () =>
        {
            try { DesktopBuddy.Log.ExportCombinedLog(); }
            catch (Exception ex) { Msg($"[Log] Combined export failed: {ex.Message}"); }
            RebuildSettingsPanel(state, session);
        }, buttonLabel: "Export");

        AddSectionHeader(ui, "Debug Log");
        float logHeight = Math.Clamp(state.ModalHeight - 360f, 180f, 340f);
        ui.Style.MinHeight = logHeight;
        ui.Style.PreferredHeight = logHeight;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var logUi = BeginRoundedScroll(ui, state, "DebugLogScroll", logHeight, Alignment.BottomLeft, out var logScroll, new colorX(0f, 0f, 0f, 0.8f));
        logUi.Style.MinHeight = Math.Max(160f, logHeight - 24f);
        logUi.Style.PreferredHeight = Math.Max(160f, logHeight - 24f);
        logUi.Style.FlexibleWidth = 1f;
        logUi.PushStyle();
        logUi.Style.SupressLayoutElement = true;
        state.DebugLogText = logUi.Text("", bestFit: false, alignment: Alignment.BottomLeft);
        logUi.PopStyle();
        state.DebugLogText.Size.Value = 13f;
        state.DebugLogText.Color.Value = new colorX(0.73f, 0.78f, 0.84f, 1f);
        state.DebugLogText.LineHeight.Value = 1.08f;
        state.DebugLogText.ParseRichText.Value = false;
        state.DebugLogScroll = logScroll;
        UpdateDebugLogText(state);
        state.OwnerRoot.World.RunInUpdates(1, () =>
        {
            if (state.DebugLogScroll != null && !state.DebugLogScroll.IsDestroyed)
                state.DebugLogScroll.MoveToBottom();
        });
        ScheduleDebugLogRefresh(state, session);
    }

    private static void BuildUpdateInfoTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        QueueUpdateInfoCheck(state);

        AddSectionHeader(ui, "Update & Info");
        AddInfoRow(ui, state, "About", "Made with love by DevL0rd and the Resonite community \u2764\uFE0F");

        bool hasUpdate = !string.IsNullOrWhiteSpace(_latestVersion);
        string updateStatus;
        colorX updateColor;
        if (_updateCheckInProgress)
        {
            updateStatus = "Checking";
            updateColor = SettingsStatusWarn;
        }
        else if (hasUpdate)
        {
            updateStatus = "Available";
            updateColor = SettingsStatusWarn;
        }
        else if (!string.IsNullOrWhiteSpace(_remoteVersion))
        {
            updateStatus = "Current";
            updateColor = SettingsStatusGood;
        }
        else if (!string.IsNullOrWhiteSpace(_updateCheckError))
        {
            updateStatus = "Failed";
            updateColor = SettingsStatusBad;
        }
        else
        {
            updateStatus = "Unknown";
            updateColor = SettingsStatusNeutral;
        }

        AddStatusRow(ui, state, "Update", updateStatus, updateColor);
        AddInfoRow(ui, state, "Current version", $"{DesktopBuddyVersion} ({BuildInfo.GitSha})");
        AddLinkButtonRow(ui, state, "Releases", "https://github.com/DevL0rd/DesktopBuddy/releases", buttonLabel: "Open");
        AddLinkButtonRow(ui, state, "Repository", "https://github.com/DevL0rd/DesktopBuddy", buttonLabel: "GitHub");

        AddSectionHeader(ui, "Settings");
        AddButtonRow(ui, state, "Reset settings to defaults", () => ResetSettingsToDefaults(state, session), buttonLabel: "Reset");

        AddSectionHeader(ui, "Changelog");
        float changelogHeight = Math.Clamp((state.ModalHeight - 540f) * 3f, 360f, 720f);
        var changelogUi = BeginRoundedScroll(ui, state, "UpdateChangelogScroll", changelogHeight, Alignment.TopLeft, out _);
        string changelog = string.IsNullOrWhiteSpace(_remoteChangelog)
            ? (_updateCheckInProgress ? "Checking CHANGELOG.md..." : "No changelog found.")
            : _remoteChangelog;
        changelogUi.Style.MinHeight = Math.Max(140f, changelogHeight - 24f);
        changelogUi.Style.PreferredHeight = Math.Max(140f, changelogHeight - 24f);
        changelogUi.Style.FlexibleWidth = 1f;
        changelogUi.PushStyle();
        changelogUi.Style.SupressLayoutElement = true;
        var text = changelogUi.Text(changelog, bestFit: false, alignment: Alignment.TopLeft);
        changelogUi.PopStyle();
        text.Size.Value = 14f;
        text.Color.Value = SettingsSubtext;
        text.LineHeight.Value = 1.12f;
        text.ParseRichText.Value = false;
    }

    private static void ResetSettingsToDefaults(SettingsPanelState state, DesktopSession session)
    {
        try
        {
            if (Config == null)
                return;

            ApplyFreshConfigDefaults();
            RefreshRuntimeStreamSettingsFromConfig();
            _settingsConfigDirty = false;
            Config.Save();

            ApplyStreamNetworkMode();
            foreach (var active in ActiveSessions.ToList())
            {
                if (active == null || active.Cleaned)
                    continue;
                UpdateViewerCullingTrigger(active);
                if (active.SettingsPanel != null)
                {
                    SyncLiveCullingStateFromConfig(active.SettingsPanel);
                    UpdateCullingPreview(active, active.SettingsPanel);
                }
            }
            ApplyStreamAudioSettingsToAllSessions();
            RequestStreamEncoderRestart(session, "settings reset");
            Msg("[Settings] Reset settings to defaults");
        }
        catch (Exception ex)
        {
            Msg($"[Settings] Failed to reset defaults: {ex.Message}");
        }
    }

    private static void UpdateDebugLogText(SettingsPanelState state)
    {
        if (state?.DebugLogText == null || state.DebugLogText.IsDestroyed)
            return;

        string content = string.Join("\n", DesktopBuddy.Log.GetRecentLines(100));
        if (content == state.DebugLogContent)
            return;

        state.DebugLogContent = content;
        state.DebugLogText.Content.Value = content;
        ScheduleScrollbarGeometryUpdate(state);
        state.OwnerRoot?.World?.RunInUpdates(1, () =>
        {
            if (state.DebugLogScroll != null && !state.DebugLogScroll.IsDestroyed)
                state.DebugLogScroll.MoveToBottom();
        });
    }

    private static void ScheduleDebugLogRefresh(SettingsPanelState state, DesktopSession session)
    {
        if (state?.OwnerRoot?.World == null)
            return;

        int generation = ++state.DebugLogRefreshGeneration;
        state.OwnerRoot.World.RunInUpdates(60, () =>
        {
            if (state.SurfaceSlot == null || state.SurfaceSlot.IsDestroyed || !state.SurfaceSlot.ActiveSelf)
                return;
            if (state.ActiveTab != SettingsPanelTab.Debug || state.DebugLogRefreshGeneration != generation)
                return;

            UpdateDebugLogText(state);
            ScheduleDebugLogRefresh(state, session);
        });
    }

}

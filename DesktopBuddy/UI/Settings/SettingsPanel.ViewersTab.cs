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

    private static void BuildViewersTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Viewers");
        var users = state.OwnerRoot.World.AllUsers.Where(u => u.IsPresentInWorld).OrderBy(u => u.UserName).ToList();
        state.ViewerListSignature = GetViewerListSignature(users, session);
        if (users.Count == 0)
        {
            AddBodyText(ui, "No present users found.");
        }
        else
        {
            float viewerListHeight = Math.Clamp(users.Count * 68f + 20f, 96f, 260f);
            var viewerUi = BeginRoundedScroll(ui, state, "ViewerListScroll", viewerListHeight, Alignment.TopLeft, out _);
            foreach (var user in users)
            {
                AddViewerRow(viewerUi, state, session, user);
            }
        }
        ScheduleViewerListRefresh(state, session);

        AddSectionHeader(ui, "Culling");
        AddOptionRow(ui, state, "Mode", NormalizeViewerCullingMode(Config?.GetValue(ViewerCullingMode)),
            new[] { ("frustum", "Frustum"), ("distance", "Distance") },
            value =>
            {
                state.ViewerCullingMode = NormalizeViewerCullingMode(value);
                SaveConfigValue(ViewerCullingMode, value);
                RebuildSettingsPanel(state, session);
            });
        AddCheckbox(ui, state, "Preview culling guide", Config?.GetValue(ViewerCullingPreview) ?? false, value =>
        {
            state.ViewerCullingPreviewEnabled = value;
            SaveConfigValue(ViewerCullingPreview, value);
            UpdateCullingPreview(session, state);
            session?.Root?.World?.RunInUpdates(1, () => UpdateCullingPreview(session, state));
        });

        AddFloatSlider(ui, state, "Range", state.ViewerDistance, 1f, 10f, value =>
        {
            state.ViewerDistance = value;
            state.ViewerFrustumDepth = value;
            SaveConfigValue(ViewerDistance, value);
            SaveConfigValue(ViewerFrustumDepth, value);
            UpdateCullingPreview(session, state);
            session?.Root?.World?.RunInUpdates(1, () => UpdateCullingPreview(session, state));
        });

        string mode = state.ViewerCullingMode;
        if (mode != "distance")
        {
            AddFloatSlider(ui, state, "Frustum angle", state.ViewerFrustumAngle, 30f, 170f, value =>
            {
                state.ViewerFrustumAngle = value;
                SaveConfigValue(ViewerFrustumWidth, value);
                UpdateCullingPreview(session, state);
                session?.Root?.World?.RunInUpdates(1, () => UpdateCullingPreview(session, state));
            });
        }
    }

    private static void RequestStreamEncoderRestart(DesktopSession session, string reason)
    {
        try
        {
            var candidates = ActiveSessions
                .Where(s => s != null && !s.Cleaned && s.StreamId > 0 &&
                    (session == null || s == session || s.Root?.World == session.Root?.World))
                .ToList();

            var targets = candidates
                .GroupBy(s => s.StreamId)
                .Select(group =>
                {
                    foreach (var candidate in group)
                    {
                        var driver = GetSharedStreamDriver(candidate.Hwnd, candidate.StreamId);
                        if (driver != null && group.Contains(driver))
                            return driver;
                    }

                    return group.FirstOrDefault(s => s.Streamer != null) ?? group.First();
                })
                .Distinct()
                .ToList();

            foreach (var target in targets)
            {
                int width = Math.Max(1, target.LastKnownW);
                int height = Math.Max(1, target.LastKnownH);
                target.PendingResizeW = width;
                target.PendingResizeH = height;
                target.ResizeDebounceUntil = target.Root?.World?.Time.WorldTime + 0.05 ?? 0.05;
            }

            if (targets.Count > 0)
                Msg($"[Settings] Scheduled stream encoder refresh ({reason}) for {targets.Count} session(s)");
        }
        catch (Exception ex)
        {
            Msg($"[Settings] Failed to schedule stream encoder refresh: {ex.Message}");
        }
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static string ViewerKey(User user)
    {
        if (!string.IsNullOrWhiteSpace(user?.UserID))
            return user.UserID;
        return user?.UserName ?? "";
    }

    private static bool IsOwnerViewer(DesktopSession session, User user)
    {
        string ownerId = session?.OwnerUserId;
        return !string.IsNullOrWhiteSpace(ownerId) &&
            string.Equals(ownerId, user?.UserID, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsViewerStreamEnabled(DesktopSession session, User user)
    {
        if (session == null || user == null || IsOwnerViewer(session, user))
            return false;

        string key = ViewerKey(user);
        if (string.IsNullOrWhiteSpace(key))
            return true;

        return !session.ViewerStreamEnabledByUserId.TryGetValue(key, out bool enabled) || enabled;
    }

    private static void SetViewerStreamEnabled(DesktopSession session, User user, bool enabled)
    {
        if (session == null || user == null)
            return;

        if (IsOwnerViewer(session, user))
            enabled = false;

        string key = ViewerKey(user);
        if (!string.IsNullOrWhiteSpace(key))
            session.ViewerStreamEnabledByUserId[key] = enabled;

        if (session.ViewerStreamAllowed != null && !session.ViewerStreamAllowed.IsDestroyed)
            session.ViewerStreamAllowed.SetOverride(user, enabled);
    }

    private static void EnsureViewerStreamOverride(DesktopSession session, User user)
    {
        if (session?.ViewerStreamAllowed == null || session.ViewerStreamAllowed.IsDestroyed || user == null)
            return;

        if (IsOwnerViewer(session, user))
        {
            session.ViewerStreamAllowed.SetOverride(user, false);
            return;
        }

        session.ViewerStreamAllowed.SetOverride(user, IsViewerStreamEnabled(session, user));
    }

    private static string GetViewerCullingBadgeText(DesktopSession session, User user)
    {
        if (IsOwnerViewer(session, user))
            return "Owner";
        if (!IsViewerStreamEnabled(session, user))
            return "Off";
        return IsViewerInConfiguredRange(session, user) ? "In range" : "Out";
    }

    private static colorX GetViewerCullingBadgeColor(DesktopSession session, User user)
    {
        if (IsOwnerViewer(session, user))
            return SettingsStatusNeutral;
        if (!IsViewerStreamEnabled(session, user))
            return SettingsStatusBad;
        return IsViewerInConfiguredRange(session, user) ? SettingsStatusGood : SettingsStatusWarn;
    }

    private static bool IsViewerInConfiguredRange(DesktopSession session, User user)
    {
        try
        {
            if (session?.Root == null || session.Root.IsDestroyed || user?.Root == null)
                return false;

            float3 localPoint = session.Root.GlobalPointToLocal(user.Root.HeadPosition);
            string mode = NormalizeViewerCullingMode(Config?.GetValue(ViewerCullingMode));
            float range = Math.Clamp(Config?.GetValue(ViewerDistance) ?? Config?.GetValue(ViewerFrustumDepth) ?? 3f, 1f, 10f);
            float originZ = GetCullingPreviewOriginZ(session);

            if (mode == "distance")
                return MathX.Distance(localPoint, new float3(0f, 0f, originZ)) <= range;

            int panelPixelsW = session.LastKnownW > 0 ? session.LastKnownW : MathX.RoundToInt(session.Canvas?.Size.Value.x ?? 0f);
            int panelPixelsH = session.LastKnownH > 0 ? session.LastKnownH : MathX.RoundToInt(session.Canvas?.Size.Value.y ?? 0f);
            float scale = session.PanelCanvasScale > 0f ? session.PanelCanvasScale : 0.0005f;
            if (panelPixelsW <= 0 || panelPixelsH <= 0)
                return false;

            float nearHalfW = panelPixelsW * scale * 0.5f;
            float nearHalfH = panelPixelsH * scale * 0.5f;
            float distanceFromNear = originZ - localPoint.z;
            if (distanceFromNear < 0f || distanceFromNear > range)
                return false;

            float horizontalAngle = NormalizeViewerFrustumAngle(Config?.GetValue(ViewerFrustumWidth) ?? 120f);
            float verticalAngle = horizontalAngle * 0.5f;
            float halfW = nearHalfW + MathF.Tan(horizontalAngle * MathF.PI / 360f) * distanceFromNear;
            float halfH = nearHalfH + MathF.Tan(verticalAngle * MathF.PI / 360f) * distanceFromNear;
            return MathF.Abs(localPoint.x) <= halfW && MathF.Abs(localPoint.y) <= halfH;
        }
        catch
        {
            return false;
        }
    }

    private static string GetViewerListSignature(List<User> users, DesktopSession session)
    {
        if (users == null || users.Count == 0)
            return "";

        return string.Join("|", users.Select(user =>
            $"{user.UserID}:{user.UserName}:{user.IsPresentInWorld}:{GetViewerCullingBadgeText(session, user)}"));
    }

    private static void ScheduleViewerListRefresh(SettingsPanelState state, DesktopSession session)
    {
        if (state?.OwnerRoot?.World == null)
            return;

        int generation = ++state.ViewerListRefreshGeneration;
        state.OwnerRoot.World.RunInUpdates(300, () =>
        {
            if (state.SurfaceSlot == null || state.SurfaceSlot.IsDestroyed || !state.SurfaceSlot.ActiveSelf)
                return;
            if (state.ActiveTab != SettingsPanelTab.Viewers || state.ViewerListRefreshGeneration != generation)
                return;

            var users = state.OwnerRoot.World.AllUsers
                .Where(u => u.IsPresentInWorld)
                .OrderBy(u => u.UserName)
                .ToList();
            string signature = GetViewerListSignature(users, session);
            if (!string.Equals(signature, state.ViewerListSignature, StringComparison.Ordinal))
                RebuildSettingsPanel(state, session);
            else
                ScheduleViewerListRefresh(state, session);
        });
    }
}

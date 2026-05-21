using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopBuddy.Shared;
using Renderite.Shared;
using FrooxEngine;
using SkyFrost.Base;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void CreateViewerCullingGate(
        DesktopSession session,
        Slot streamSlot,
        VideoTextureProvider videoTex,
        FrooxEngine.User owner)
    {
        if (session == null || streamSlot == null || videoTex == null)
            return;

        var gateSlot = session.Root.AddSlot("ViewerCullingGate");

        var viewerAllowed = gateSlot.AttachComponent<ValueField<bool>>();
        viewerAllowed.Value.Value = true;
        var viewerOverride = gateSlot.AttachComponent<ValueUserOverride<bool>>();
        viewerOverride.Target.Target = viewerAllowed.Value;
        viewerOverride.Default.Value = true;
        viewerOverride.CreateOverrideOnWrite.Value = false;
        viewerOverride.ClearOnUserLeave.Value = true;

        var previewAllowed = gateSlot.AttachComponent<ValueField<bool>>();
        previewAllowed.Value.Value = false;
        var previewOverride = gateSlot.AttachComponent<ValueUserOverride<bool>>();
        previewOverride.Target.Target = previewAllowed.Value;
        previewOverride.Default.Value = false;
        previewOverride.CreateOverrideOnWrite.Value = false;
        previewOverride.ClearOnUserLeave.Value = true;

        var finalAllowed = gateSlot.AttachComponent<ValueField<bool>>();
        finalAllowed.Value.Value = false;
        var finalOverride = gateSlot.AttachComponent<ValueUserOverride<bool>>();
        finalOverride.Target.Target = finalAllowed.Value;
        finalOverride.Default.Value = false;
        finalOverride.CreateOverrideOnWrite.Value = false;
        finalOverride.ClearOnUserLeave.Value = true;

        var urlDriver = gateSlot.AttachComponent<BooleanValueDriver<Uri>>();
        urlDriver.TargetField.Target = videoTex.URL;
        urlDriver.FalseValue.Value = null;
        urlDriver.TrueValue.Value = null;
        urlDriver.State.DriveFrom(finalAllowed.Value);

        session.ViewerAllowedField = viewerAllowed;
        session.PreviewAllowedField = previewAllowed;
        session.FinalStreamAllowedField = finalAllowed;
        session.ViewerStreamAllowed = viewerOverride;
        session.PreviewStreamAllowed = previewOverride;
        session.FinalStreamAllowedOverride = finalOverride;
        session.StreamUrlDriver = urlDriver;

        viewerOverride.SetOverride(owner, false);
        previewOverride.SetOverride(owner, false);

        foreach (var user in session.Root.World.AllUsers.Where(u => u.IsPresentInWorld))
        {
            if (user == owner)
                viewerOverride.SetOverride(user, false);
            else if (IsViewerStreamEnabled(session, user))
                viewerOverride.SetOverride(user, true);
        }

        StartViewerCullingPlaybackLoop(session, videoTex);
    }

    private static bool IsLocalSessionOwner(DesktopSession session)
    {
        var localUser = session?.Root?.World?.LocalUser;
        if (session == null || localUser == null)
            return false;

        if (!string.IsNullOrWhiteSpace(session.OwnerUserId) &&
            string.Equals(session.OwnerUserId, localUser.UserID, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrWhiteSpace(session.OwnerUserId) &&
            ReferenceEquals(localUser, session.Root.World.LocalUser);
    }

    private static bool IsSessionOwner(DesktopSession session, FrooxEngine.User user)
    {
        if (session == null || user == null)
            return false;

        if (!string.IsNullOrWhiteSpace(session.OwnerUserId) &&
            string.Equals(session.OwnerUserId, user.UserID, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrWhiteSpace(session.OwnerUserId) &&
            ReferenceEquals(user, session.Root?.World?.LocalUser);
    }

    private static string LocalViewerKey(FrooxEngine.User user)
    {
        if (!string.IsNullOrWhiteSpace(user?.UserID))
            return user.UserID;
        return user?.UserName;
    }

    private static bool IsLocalViewerStreamEnabled(DesktopSession session)
    {
        var localUser = session?.Root?.World?.LocalUser;
        if (session == null || localUser == null || IsLocalSessionOwner(session))
            return false;

        string key = LocalViewerKey(localUser);
        return string.IsNullOrWhiteSpace(key) ||
            !session.ViewerStreamEnabledByUserId.TryGetValue(key, out bool enabled) ||
            enabled;
    }

    private static bool ShouldLocalStreamBeAllowedWithoutGrace(DesktopSession session)
    {
        if (session?.ViewerAllowedField == null || session.PreviewAllowedField == null)
            return true;

        bool previewAllowed = session.LocalPreviewingRemoteStream;
        bool viewerAllowed = IsLocalViewerStreamEnabled(session);
        bool rangeAllowed = IsViewerInConfiguredRange(session, session.Root?.World?.LocalUser);
        return rangeAllowed && (previewAllowed || viewerAllowed);
    }

    private static bool IsLocalStreamAllowedByGraceGate(DesktopSession session)
    {
        if (session?.FinalStreamAllowedField == null || session.FinalStreamAllowedField.IsDestroyed)
            return ShouldLocalStreamBeAllowedWithoutGrace(session);

        return session.FinalStreamAllowedField.Value.Value;
    }

    private static void SetLocalStreamAllowedByGraceGate(DesktopSession session, bool allowed)
    {
        if (session?.FinalStreamAllowedField == null || session.FinalStreamAllowedField.IsDestroyed)
            return;

        if (session.FinalStreamAllowedField.Value.Value == allowed)
            return;

        var localUser = session.Root?.World?.LocalUser;
        if (session.FinalStreamAllowedOverride != null && !session.FinalStreamAllowedOverride.IsDestroyed && localUser != null)
            session.FinalStreamAllowedOverride.SetOverride(localUser, allowed);
        else
            session.FinalStreamAllowedField.Value.Value = allowed;
    }

    private static bool GetAppliedStreamAllowed(DesktopSession session, FrooxEngine.User user)
    {
        string key = ViewerKey(user);
        return !string.IsNullOrWhiteSpace(key) &&
            session.CullingAppliedStreamAllowedByUserId.TryGetValue(key, out bool allowed) &&
            allowed;
    }

    private static void SetStreamAllowedForUser(DesktopSession session, FrooxEngine.User user, bool allowed)
    {
        string key = ViewerKey(user);
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (session.CullingAppliedStreamAllowedByUserId.TryGetValue(key, out bool previous) && previous == allowed)
            return;

        session.CullingAppliedStreamAllowedByUserId[key] = allowed;
        if (session.FinalStreamAllowedOverride != null && !session.FinalStreamAllowedOverride.IsDestroyed)
            session.FinalStreamAllowedOverride.SetOverride(user, allowed);
    }

}

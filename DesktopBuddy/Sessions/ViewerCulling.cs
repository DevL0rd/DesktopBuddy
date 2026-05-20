using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        var rangeAllowed = gateSlot.AttachComponent<ValueField<bool>>();
        rangeAllowed.Value.Value = false;

        var triggerSlot = session.Root.AddSlot("ViewerCullingTrigger");
        triggerSlot.LocalPosition = float3.Zero;
        triggerSlot.LocalRotation = floatQ.Identity;
        triggerSlot.LocalScale = float3.One;

        var tracker = triggerSlot.AttachComponent<ColliderUserTracker>();
        tracker.TriggersOnly.Value = true;

        var sphere = triggerSlot.AttachComponent<SphereCollider>();
        sphere.Type.Value = ColliderType.Trigger;
        sphere.IgnoreRaycasts.Value = true;

        var box = triggerSlot.AttachComponent<BoxCollider>();
        box.Type.Value = ColliderType.Trigger;
        box.IgnoreRaycasts.Value = true;

        rangeAllowed.Value.DriveFrom(tracker.IsLocalUserInside);

        var finalAllowed = gateSlot.AttachComponent<ValueField<bool>>();
        finalAllowed.Value.Value = false;

        var userEnabled = gateSlot.AttachComponent<ValueField<bool>>();
        userEnabled.Value.Value = false;
        var userEnabledDriver = gateSlot.AttachComponent<MultiBoolConditionDriver>();
        userEnabledDriver.Target.Target = userEnabled.Value;
        userEnabledDriver.Mode.Value = MultiBoolConditionDriver.ConditionMode.Any;
        int viewerConditionIndex = userEnabledDriver.Conditions.Count;
        userEnabledDriver.Conditions.Add();
        var viewerCondition = userEnabledDriver.Conditions[viewerConditionIndex];
        viewerCondition.Field.Target = viewerAllowed.Value;
        viewerCondition.Invert.Value = false;
        int previewConditionIndex = userEnabledDriver.Conditions.Count;
        userEnabledDriver.Conditions.Add();
        var previewCondition = userEnabledDriver.Conditions[previewConditionIndex];
        previewCondition.Field.Target = previewAllowed.Value;
        previewCondition.Invert.Value = false;

        var finalDriver = gateSlot.AttachComponent<MultiBoolConditionDriver>();
        finalDriver.Target.Target = finalAllowed.Value;
        finalDriver.Mode.Value = MultiBoolConditionDriver.ConditionMode.All;
        int enabledConditionIndex = finalDriver.Conditions.Count;
        finalDriver.Conditions.Add();
        var enabledCondition = finalDriver.Conditions[enabledConditionIndex];
        enabledCondition.Field.Target = userEnabled.Value;
        enabledCondition.Invert.Value = false;
        int rangeConditionIndex = finalDriver.Conditions.Count;
        finalDriver.Conditions.Add();
        var rangeCondition = finalDriver.Conditions[rangeConditionIndex];
        rangeCondition.Field.Target = rangeAllowed.Value;
        rangeCondition.Invert.Value = false;

        var urlDriver = gateSlot.AttachComponent<BooleanValueDriver<Uri>>();
        urlDriver.TargetField.Target = videoTex.URL;
        urlDriver.FalseValue.Value = null;
        urlDriver.TrueValue.Value = null;
        urlDriver.State.DriveFrom(finalAllowed.Value);

        session.ViewerAllowedField = viewerAllowed;
        session.PreviewAllowedField = previewAllowed;
        session.RangeAllowedField = rangeAllowed;
        session.FinalStreamAllowedField = finalAllowed;
        session.ViewerStreamAllowed = viewerOverride;
        session.PreviewStreamAllowed = previewOverride;
        session.FinalStreamAllowedOverride = null;
        session.StreamUrlDriver = urlDriver;
        session.CullingTracker = tracker;
        session.CullingTriggerSlot = triggerSlot;
        session.CullingSphereCollider = sphere;
        session.CullingFrustumCollider = box;

        viewerOverride.SetOverride(owner, false);
        previewOverride.SetOverride(owner, false);

        foreach (var user in session.Root.World.AllUsers.Where(u => u.IsPresentInWorld))
        {
            if (user == owner)
                viewerOverride.SetOverride(user, false);
            else if (IsViewerStreamEnabled(session, user))
                viewerOverride.SetOverride(user, true);
        }

        UpdateViewerCullingTrigger(session);
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
        if (session?.ViewerAllowedField == null || session.PreviewAllowedField == null || session.RangeAllowedField == null)
            return true;

        bool previewAllowed = session.LocalPreviewingRemoteStream;
        bool viewerAllowed = IsLocalViewerStreamEnabled(session);
        bool rangeAllowed = session.RangeAllowedField.Value.Value;
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

    private static HashSet<string> GetUsersInsideCullingTrigger(DesktopSession session)
    {
        var inside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracker = session?.CullingTracker;
        if (tracker == null || tracker.IsDestroyed)
            return inside;

        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = typeof(ColliderUserTracker).GetField("_usersInside", flags);
            if (field?.GetValue(tracker) is not IEnumerable usersInside)
                return inside;

            foreach (object entry in usersInside)
            {
                object userRef = entry?.GetType().GetProperty("Value")?.GetValue(entry);
                if (userRef is not UserRef userReference)
                    continue;

                string key = ViewerKey(userReference.Target);
                if (!string.IsNullOrWhiteSpace(key))
                    inside.Add(key);
            }
        }
        catch (Exception ex)
        {
            Msg($"[Culling] Failed to read ColliderUserTracker users: {ex.Message}");
        }

        return inside;
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

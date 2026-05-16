using System;
using System.Collections.Generic;
using System.Linq;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void StartViewerCullingPlaybackLoop(DesktopSession session, VideoTextureProvider videoTex)
    {
        if (session?.Root?.World == null || videoTex == null)
            return;

        int generation = ++session.CullingGateGeneration;
        session.CullingOutOfRangeSince = -1.0;
        session.CullingAppliedStreamAllowedByUserId.Clear();
        session.CullingOutOfRangeSinceByUserId.Clear();

        void Tick()
        {
            if (session.Root == null || session.Root.IsDestroyed || videoTex.IsDestroyed || generation != session.CullingGateGeneration)
                return;

            double now = session.Root.World.Time.WorldTime;
            var inside = GetUsersInsideCullingTrigger(session);
            var presentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyViewerShouldPlay = false;

            foreach (var user in session.Root.World.AllUsers.Where(u => u.IsPresentInWorld))
            {
                string key = ViewerKey(user);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                presentKeys.Add(key);

                bool streamEnabledForUser = IsSessionOwner(session, user)
                    ? session.LocalPreviewingRemoteStream
                    : IsViewerStreamEnabled(session, user);
                bool rangeAllowed = inside.Contains(key);
                bool wasAllowed = GetAppliedStreamAllowed(session, user);

                bool shouldPlay;
                if (streamEnabledForUser && rangeAllowed)
                {
                    session.CullingOutOfRangeSinceByUserId.Remove(key);
                    shouldPlay = true;
                }
                else if (streamEnabledForUser && wasAllowed)
                {
                    if (!session.CullingOutOfRangeSinceByUserId.TryGetValue(key, out double outOfRangeSince))
                    {
                        outOfRangeSince = now;
                        session.CullingOutOfRangeSinceByUserId[key] = outOfRangeSince;
                    }

                    shouldPlay = now - outOfRangeSince < 5.0;
                }
                else
                {
                    session.CullingOutOfRangeSinceByUserId.Remove(key);
                    shouldPlay = false;
                }

                SetStreamAllowedForUser(session, user, shouldPlay);
                anyViewerShouldPlay |= shouldPlay;
            }

            foreach (string key in session.CullingAppliedStreamAllowedByUserId.Keys.ToArray())
            {
                if (presentKeys.Contains(key))
                    continue;

                session.CullingAppliedStreamAllowedByUserId.Remove(key);
                session.CullingOutOfRangeSinceByUserId.Remove(key);
            }

            if (anyViewerShouldPlay)
            {
                if (!videoTex.IsPlaying)
                    videoTex.Play();
            }
            else if (videoTex.IsPlaying)
            {
                videoTex.Stop();
            }

            session.Root.World.RunInUpdates(2, Tick);
        }

        session.Root.World.RunInUpdates(1, Tick);
    }

    private static bool IsLocalStreamPlaybackAllowedNow(DesktopSession session)
    {
        return IsLocalStreamAllowedByGraceGate(session);
    }

    private static void SetRemoteStreamUrl(DesktopSession session, Uri url, string reason)
    {
        var videoTex = session?.VideoTexture;
        if (videoTex == null || videoTex.IsDestroyed)
            return;

        session.StreamUrl = url;
        bool playbackAllowed = IsLocalStreamPlaybackAllowedNow(session);
        if (!playbackAllowed && videoTex.IsPlaying)
            videoTex.Stop();

        if (session.StreamUrlDriver != null && !session.StreamUrlDriver.IsDestroyed)
            session.StreamUrlDriver.TrueValue.Value = url;
        else
            videoTex.URL.Value = playbackAllowed ? url : null;

        if (!playbackAllowed && videoTex.IsPlaying)
            videoTex.Stop();

        if (!string.IsNullOrWhiteSpace(reason))
            Msg($"[RemoteStream] URL set ({reason}): {url}");
    }

    private static void ClearRemoteStreamUrl(DesktopSession session, string reason)
    {
        var videoTex = session?.VideoTexture;
        if (videoTex == null || videoTex.IsDestroyed)
            return;

        if (videoTex.IsPlaying)
            videoTex.Stop();

        if (session.StreamUrlDriver != null && !session.StreamUrlDriver.IsDestroyed)
            session.StreamUrlDriver.TrueValue.Value = null;
        videoTex.URL.Value = null;

        if (!string.IsNullOrWhiteSpace(reason))
            Msg($"[RemoteStream] URL cleared ({reason})");
    }
}

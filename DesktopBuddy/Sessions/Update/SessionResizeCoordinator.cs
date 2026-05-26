using System;
using System.Linq;
using System.Threading;
using DesktopBuddy.Shared;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static bool TryPublishCurrentSharedTexture(DesktopSession session)
    {
        if (session == null || !session.UseTextureBridge || !session.BridgeRegistrationPending)
            return true;

        var streamer = session.Streamer;
        if (streamer == null || TextureBridgeChannel == null || !TextureBridgeChannel.IsOpen)
            return false;

        if (DesktopBuddyPlatform.IsLinuxProton)
        {
            if (session.SharedTextureSlot < 0)
            {
                int slot = TextureBridgeChannel.RegisterLinuxCapture(
                    streamer.Width > 0 ? streamer.Width : session.LastKnownW,
                    streamer.Height > 0 ? streamer.Height : session.LastKnownH,
                    streamer.LinuxPipeWireNodeId);
                if (slot < 0)
                    return false;

                session.SharedTextureSlot = slot;
                Msg($"[UpdateLoop] Linux capture bridge registered: slot={slot} {streamer.Width}x{streamer.Height}");
            }

            session.Texture.DisplayIndex.Value = int.MinValue;
            session.PendingBridgeDisplayIndex = SharedTextureBridgeProtocol.MagicIndexBase + session.SharedTextureSlot;
            session.BridgeDisplayIndexApplied = false;
            session.BridgeRegistrationPending = false;
            return true;
        }

        if (!streamer.HasCurrentSharedFrame ||
            streamer.SharedTextureHandle == IntPtr.Zero ||
            streamer.SharedTextureWidth <= 0 ||
            streamer.SharedTextureHeight <= 0)
        {
            if (_updateCount % 30 == 0)
                Msg($"[UpdateLoop] Waiting for current shared frame before bridge publish hwnd={session.Hwnd} pendingRecreate={streamer.IsResizeRecreatePending} size={streamer.Width}x{streamer.Height} shared=0x{streamer.SharedTextureHandle.ToInt64():X} sharedSize={streamer.SharedTextureWidth}x{streamer.SharedTextureHeight}");
            return false;
        }

        if (session.SharedTextureSlot < 0)
        {
            int slot = TextureBridgeChannel.RegisterTexture(
                streamer.SharedTextureHandle,
                null,
                streamer.SharedTextureWidth,
                streamer.SharedTextureHeight);
            if (slot < 0)
            {
                if (_updateCount % 60 == 0)
                    Msg($"[UpdateLoop] No free shared texture slots for hwnd={session.Hwnd}");
                return false;
            }

            session.SharedTextureSlot = slot;
            Msg($"[UpdateLoop] Shared texture bridge registered: slot={slot} shared=0x{streamer.SharedTextureHandle.ToInt64():X} {streamer.SharedTextureWidth}x{streamer.SharedTextureHeight}");
        }
        else
        {
            TextureBridgeChannel.UpdateTexture(
                session.SharedTextureSlot,
                streamer.SharedTextureHandle,
                null,
                streamer.SharedTextureWidth,
                streamer.SharedTextureHeight);
            Msg($"[UpdateLoop] Shared texture bridge updated: slot={session.SharedTextureSlot} shared=0x{streamer.SharedTextureHandle.ToInt64():X} {streamer.SharedTextureWidth}x{streamer.SharedTextureHeight}");
        }

        session.Texture.DisplayIndex.Value = int.MinValue;
        session.PendingBridgeDisplayIndex = SharedTextureBridgeProtocol.MagicIndexBase + session.SharedTextureSlot;
        session.BridgeDisplayIndexApplied = false;
        session.BridgeRegistrationPending = false;
        return true;
    }

    private static bool ProcessSessionResizeAndEncoding(World world, DesktopSession session)
    {
        var streamerForResize = session.Streamer;
        if (streamerForResize != null)
        {
            streamerForResize.RecreatePoolIfNeeded();
            int sw = streamerForResize.Width;
            int sh = streamerForResize.Height;
            if (DesktopBuddyPlatform.IsLinuxProton && session.UseTextureBridge)
            {
                if (session.BridgeRegistrationPending && !TryPublishCurrentSharedTexture(session))
                    return true;

                if (session.SharedTextureSlot >= 0 &&
                    TextureBridgeChannel != null &&
                    TextureBridgeChannel.TryGetTextureSize(session.SharedTextureSlot, out int bridgeW, out int bridgeH) &&
                    bridgeW > 0 && bridgeH > 0 &&
                    (session.LastKnownW != bridgeW || session.LastKnownH != bridgeH))
                {
                    Msg($"[UpdateLoop] Linux renderer resize {session.LastKnownW}x{session.LastKnownH} -> {bridgeW}x{bridgeH}");
                    session.LastKnownW = bridgeW;
                    session.LastKnownH = bridgeH;
                    ApplySessionVisualResize(session, bridgeW, bridgeH);
                }

                return false;
            }

            if (streamerForResize.IsResizeRecreatePending && _updateCount % 30 == 0)
                Msg($"[UpdateLoop] Resize recreate still pending hwnd={session.Hwnd} size={sw}x{sh} sharedSize={streamerForResize.SharedTextureWidth}x{streamerForResize.SharedTextureHeight}");

            if (sw > 0 && sh > 0 && (session.LastKnownW != sw || session.LastKnownH != sh))
            {
                Msg($"[UpdateLoop] Window resize {session.LastKnownW}x{session.LastKnownH} -> {sw}x{sh}");
                session.LastKnownW = sw;
                session.LastKnownH = sh;

                if (session.UseTextureBridge && TextureBridgeChannel != null)
                {
                    session.BridgeRegistrationPending = true;
                }
                else
                {
                    ApplySessionVisualResize(session, sw, sh);
                }

                session.PendingResizeW = sw;
                session.PendingResizeH = sh;
                session.PendingVisualResizeW = sw;
                session.PendingVisualResizeH = sh;
                session.ResizeDebounceUntil = world.Time.WorldTime + 0.15;
                Msg($"[UpdateLoop] Resize pending: visual={sw}x{sh} encoder debounce=150ms");
                return true;
            }

            if (session.BridgeRegistrationPending && !TryPublishCurrentSharedTexture(session))
                return true;
        }

        if (session.PendingVisualResizeW > 0 && session.PendingVisualResizeH > 0)
        {
            int visualW = session.PendingVisualResizeW;
            int visualH = session.PendingVisualResizeH;
            bool bridgeReady = session.SharedTextureSlot < 0 ||
                TextureBridgeChannel == null ||
                TextureBridgeChannel.IsTextureRunning(session.SharedTextureSlot, visualW, visualH);

            if (bridgeReady)
            {
                ApplySessionVisualResize(session, visualW, visualH);
                session.PendingVisualResizeW = 0;
                session.PendingVisualResizeH = 0;
            }
            else
            {
                if (session.BridgeDisplayIndexApplied && _updateCount % 5 == 0)
                    RetriggerDesktopTexture(session.Texture);
                if (_updateCount % 30 == 0)
                    Msg($"[UpdateLoop] Waiting for shared texture bind before visual resize {visualW}x{visualH}");
            }
        }

        if (session.ResizeDebounceUntil > 0 && world.Time.WorldTime >= session.ResizeDebounceUntil)
        {
            session.ResizeDebounceUntil = 0;
            int rw = session.PendingResizeW;
            int rh = session.PendingResizeH;

            if (session.StreamId <= 0)
            {
                Msg($"[UpdateLoop] Resize debounce expired for local-only panel {rw}x{rh}, no encoder reinit");
                session.PendingResizeW = 0;
                session.PendingResizeH = 0;
                return true;
            }

            if (session.EncoderInitialStreamId == session.StreamId &&
                session.EncoderInitialSourceW == rw &&
                session.EncoderInitialSourceH == rh)
            {
                Msg($"[UpdateLoop] Resize debounce expired, encoder already initialized for {rw}x{rh}; skipping reinit");
                session.PendingResizeW = 0;
                session.PendingResizeH = 0;
                return true;
            }

            Msg($"[UpdateLoop] Resize debounce expired, reiniting encoder for {rw}x{rh}");

            if (session.Streamer != null) session.Streamer.OnGpuFrame = null;

            var oldStreamId = session.StreamId;
            bool oldUsesMediaMtx = session.StreamUsesMediaMtx;
            int newStreamId = NextStreamId();
            bool useMediaMtx = IsMediaMtxEnabled;
            FfmpegEncoder newEncoder;
            Uri newUrl;

            if (useMediaMtx)
            {
                var rtspUrl = GetMediaMtxRtspUrl(newStreamId);
                newEncoder = new FfmpegEncoder(newStreamId, rtspUrl);
                newUrl = new Uri(rtspUrl);
            }
            else
            {
                newEncoder = StreamServer?.CreateEncoder(newStreamId);
                newUrl = StreamServer != null ? GetBuiltInStreamUrl(newStreamId) : null;
            }

            FfmpegEncoder oldEncoder = session.Encoder;
            if (oldEncoder == null)
                oldEncoder = GetSharedStreamEncoder(session.Hwnd);

            var oldStreamer = session.Streamer;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    oldEncoder?.Stop();
                    if (oldUsesMediaMtx)
                    {
                        oldEncoder?.Dispose();
                    }
                    else if (StreamServer != null)
                    {
                        StreamServer?.StopEncoder(oldStreamId);
                    }
                    else
                    {
                        oldEncoder?.Dispose();
                    }
                    oldStreamer?.FlushD3dContext();
                }
                catch (Exception ex) { Msg($"[Resize:BG] Old encoder cleanup error: {ex.Message}"); }
            });

            UpdateSharedStreamAfterResize(session.Hwnd, newStreamId, newEncoder, newUrl, useMediaMtx);

            var sharingSessions = ActiveSessions
                .Where(s => s != null && !s.Cleaned && s.StreamId == oldStreamId)
                .ToList();
            if (!sharingSessions.Contains(session))
                sharingSessions.Add(session);

            foreach (var sharingSession in sharingSessions)
            {
                sharingSession.StreamId = newStreamId;
                sharingSession.Encoder = newEncoder;
                sharingSession.EncoderInitialStreamId = 0;
                sharingSession.EncoderInitialSourceW = 0;
                sharingSession.EncoderInitialSourceH = 0;
                sharingSession.StreamUsesMediaMtx = useMediaMtx;
            }

            ConnectEncoder(session, newEncoder);

            foreach (var sharingSession in sharingSessions)
            {
                if (sharingSession.VideoTexture != null && !sharingSession.VideoTexture.IsDestroyed && newUrl != null)
                {
                    Msg($"[UpdateLoop] Updating VTP URL: {sharingSession.VideoTexture.URL.Value} -> {newUrl}");
                    SetRemoteStreamUrl(sharingSession, newUrl, $"encoder resize streamId={newStreamId}");
                }
            }

            Msg($"[UpdateLoop] New encoder {newStreamId} created and connected for {rw}x{rh}");
            session.PendingResizeW = 0;
            session.PendingResizeH = 0;
        }

        return false;
    }
}

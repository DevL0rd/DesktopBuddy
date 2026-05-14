using System;
using System.Threading;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static bool ProcessSessionResizeAndEncoding(World world, DesktopSession session)
    {
        var streamerForResize = session.Streamer;
        if (streamerForResize != null)
        {
            streamerForResize.RecreatePoolIfNeeded();
            int sw = streamerForResize.Width;
            int sh = streamerForResize.Height;

            if (sw > 0 && sh > 0 && (session.LastKnownW != sw || session.LastKnownH != sh))
            {
                Msg($"[UpdateLoop] Window resize {session.LastKnownW}x{session.LastKnownH} -> {sw}x{sh}");
                session.LastKnownW = sw;
                session.LastKnownH = sh;

                if (session.SharedTextureSlot >= 0 && TextureBridgeChannel != null)
                {
                    TextureBridgeChannel.UpdateTexture(
                        session.SharedTextureSlot,
                        streamerForResize.SharedTextureHandle,
                        streamerForResize.SharedTextureWidth,
                        streamerForResize.SharedTextureHeight);
                    RetriggerDesktopTexture(session.Texture);
                    world.RunInUpdates(2, () =>
                    {
                        if (!session.Cleaned && session.Texture != null && !session.Texture.IsDestroyed)
                            RetriggerDesktopTexture(session.Texture);
                    });
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
                if (_updateCount % 5 == 0)
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
                return true;
            }

            Msg($"[UpdateLoop] Resize debounce expired, reiniting encoder for {rw}x{rh}");

            if (session.Streamer != null) session.Streamer.OnGpuFrame = null;

            var oldStreamId = session.StreamId;
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
            session.StreamId = newStreamId;

            FfmpegEncoder oldEncoder = session.Encoder;
            if (oldEncoder == null)
                oldEncoder = GetSharedStreamEncoder(session.Hwnd);

            var oldStreamer = session.Streamer;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    oldEncoder?.Stop();
                    oldStreamer?.FlushD3dContext();
                    if (!useMediaMtx)
                        StreamServer?.StopEncoder(oldStreamId);
                    oldEncoder?.Dispose();
                }
                catch (Exception ex) { Msg($"[Resize:BG] Old encoder cleanup error: {ex.Message}"); }
            });

            UpdateSharedStreamAfterResize(session.Hwnd, newStreamId, newEncoder, newUrl);

            session.Encoder = newEncoder;
            ConnectEncoder(session, newEncoder);

            if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && newUrl != null)
            {
                Msg($"[UpdateLoop] Updating VTP URL: {session.VideoTexture.URL.Value} -> {newUrl}");
                SetRemoteStreamUrl(session, newUrl, $"encoder resize streamId={newStreamId}");
            }

            Msg($"[UpdateLoop] New encoder {newStreamId} created and connected for {rw}x{rh}");
        }

        return false;
    }
}

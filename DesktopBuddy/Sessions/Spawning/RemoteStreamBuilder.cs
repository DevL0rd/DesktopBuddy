using System;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

internal sealed class RemoteStreamBuildContext
{
    public Slot Root;
    public IntPtr Hwnd;
    public int Width;
    public int Height;
    public float CanvasScale;
    public DesktopSession Session;
    public bool UseMediaMtx;
    public Slider<float> VolumeSlider;
    public float StreamOutputVolume;
    public Func<bool> IsPrivate;
    public Func<float> CurrentPanelCurvature;
}

internal sealed class RemoteStreamVisual
{
    public VideoTextureProvider VideoTexture;
    public CurvedPlaneMesh StreamPlane;
    public Canvas StreamCanvas;
}

public partial class DesktopBuddyMod
{
    private static RemoteStreamVisual BuildRemoteStream(RemoteStreamBuildContext context)
    {
        var root = context.Root;
        var session = context.Session;
        bool allowRemoteStream = context.UseMediaMtx || StreamServer != null;
        if (!allowRemoteStream)
        {
            Msg($"[RemoteStream] Skipped: MediaMtx={IsMediaMtxEnabled} StreamServer={StreamServer != null} StreamBase={GetBuiltInStreamBaseUrl() ?? "null"}");
            return null;
        }

        try
        {
            SharedStream shared = AcquireSharedStream(context.Hwnd, context.UseMediaMtx);
            session.StreamId = shared.StreamId;
            session.Encoder = shared.Encoder;
            session.StreamAudioCapture = shared.Audio;
            session.StartStreamAudioCapture = shared.StartAudio;
            var nvEncoder = shared.Encoder;

            if (session.SpatialAudioSource != null && shared.Audio != null)
                session.SpatialAudioSource.SetAudioCapture(shared.Audio);

            bool shouldDriveEncoder = TryClaimSharedStreamDriver(context.Hwnd, shared.StreamId, session);

            if (shouldDriveEncoder)
            {
                ConnectEncoder(session, nvEncoder, shared.Audio, shared.StartAudio);
                Msg($"[RemoteStream] This panel drives the encoder for stream {shared.StreamId}");
            }
            else
            {
                Msg($"[RemoteStream] This panel shares encoder from stream {shared.StreamId}, no encoding hook");
            }

            var videoSlot = root.AddSlot("StreamProvider");
            var videoTex = TextureProviderSettings.ClampWrap(videoSlot.AttachComponent<VideoTextureProvider>());
            videoTex.Stream.Value = true;
            videoTex.URL.Value = null;
            videoTex.Volume.Value = 1f;
            session.VideoTexture = videoTex;
            var streamUrl = shared.StreamUrl;

            var audioOutput = videoSlot.AttachComponent<AudioOutput>();
            audioOutput.Source.Target = videoTex;
            audioOutput.Volume.Value = context.StreamOutputVolume;
            audioOutput.Global.Value = true;
            audioOutput.Spatialize.Value = false;
            audioOutput.SpatialBlend.Value = 0f;
            audioOutput.DistanceSpace.Value = AudioDistanceSpace.Global;
            audioOutput.DopplerLevel.Value = 0f;
            audioOutput.Pitch.Value = 1f;
            audioOutput.IgnoreAudioEffects.Value = true;
            audioOutput.AudioTypeGroup.Value = AudioTypeGroup.Multimedia;
            session.StreamAudioOutput = audioOutput;
            ApplyStreamAudioSettings(session);

            var volDriver = videoSlot.AttachComponent<ValueDriver<float>>();
            volDriver.DriveTarget.Target = audioOutput.Volume;
            volDriver.ValueSource.Target = context.VolumeSlider.Value;

            if (session.SpatialAudioOutput != null)
            {
                var spatialOut = session.SpatialAudioOutput;
                context.VolumeSlider.Value.OnValueChange += (SyncField<float> field) =>
                {
                    if (spatialOut != null && !spatialOut.IsDestroyed)
                        spatialOut.Volume.Value = field.Value;
                };
            }

            var streamSlot = root.AddSlot("RemoteStreamVisual");
            streamSlot.LocalScale = float3.One * context.CanvasScale;
            var ownerStreamVisual = streamSlot.AttachComponent<ValueUserOverride<bool>>();
            ownerStreamVisual.Target.Target = streamSlot.ActiveSelf_Field;
            ownerStreamVisual.Default.Value = true;
            ownerStreamVisual.CreateOverrideOnWrite.Value = false;
            ownerStreamVisual.ClearOnUserLeave.Value = true;
            ownerStreamVisual.SetOverride(root.World.LocalUser, false);
            session.StreamVisualAllowed = ownerStreamVisual;

            Msg("[RemoteStream] Per-user culling gate controls stream visibility and playback");

            CreateViewerCullingGate(session, streamSlot, videoTex, root.World.LocalUser);

            var streamPlane = AddCurvedTexturePlane(
                streamSlot,
                "VideoTextureCurvedPlane",
                context.Width,
                context.Height,
                1f,
                videoTex,
                0f,
                flipY: false,
                offsetUnits: -100f);
            streamPlane.Curvature.Value = MathX.Clamp(context.CurrentPanelCurvature(), 0f, 1f);

            var streamCanvas = streamSlot.AttachComponent<Canvas>();
            streamCanvas.Collider.RawTarget.Enabled = false;
            streamCanvas.Size.Value = new float2(context.Width, context.Height);
            var streamUi = new UIBuilder(streamCanvas);

            var streamBg = streamUi.Image(new colorX(0f, 0f, 0f, 1f));
            streamBg.Tint.Value = colorX.Clear;
            streamBg.InteractionTarget.Value = false;
            streamUi.NestInto(streamBg.RectTransform);

            var streamImg = streamUi.RawImage(videoTex);
            streamImg.Tint.Value = new colorX(1f, 1f, 1f, 0f);
            var streamMat = streamSlot.AttachComponent<UI_UnlitMaterial>();
            streamMat.BlendMode.Value = BlendMode.Alpha;
            streamMat.ZWrite.Value = ZWrite.On;
            streamMat.OffsetUnits.Value = -100f;
            streamImg.Material.Target = streamMat;

            Msg($"[RemoteStream] Created, URL={streamUrl}, streamId={shared.StreamId}, refs={shared.RefCount}");

            ScheduleRemoteStreamBinding(root, session, videoTex, nvEncoder, streamUrl, shared.StreamId, context.IsPrivate);
            ScheduleRemoteStreamPlaybackWatch(root, session, videoTex, shared.StreamId);

            return new RemoteStreamVisual
            {
                VideoTexture = videoTex,
                StreamPlane = streamPlane,
                StreamCanvas = streamCanvas
            };
        }
        catch (Exception ex)
        {
            Msg($"[RemoteStream] ERROR: {ex}");
            return null;
        }
    }

    private static void ScheduleRemoteStreamBinding(
        Slot root,
        DesktopSession session,
        VideoTextureProvider videoTex,
        FfmpegEncoder encoder,
        Uri streamUrl,
        int streamId,
        Func<bool> isPrivate)
    {
        const int StreamBindRetryUpdates = 6;
        const int StreamBindMaxAttempts = 300;
        root.World.RunInUpdates(1, () => BindStreamUrlWhenReady(0));

        void BindStreamUrlWhenReady(int attempt)
        {
            if (videoTex == null || videoTex.IsDestroyed || root.IsDestroyed) return;

            bool ready = encoder.IsRunning;
            bool privateNow = isPrivate();
            Uri effectiveUrl = streamUrl ?? GetBuiltInStreamUrl(streamId);

            if (ready && !privateNow && effectiveUrl != null)
            {
                SetRemoteStreamUrl(session, effectiveUrl, $"initial bind streamId={streamId}");
                Msg($"[RemoteStream] URL bound after encoder readiness: attempt={attempt} streamId={streamId} {encoder.ReadableStreamState}");
                return;
            }

            if (attempt >= StreamBindMaxAttempts)
            {
                Msg($"[RemoteStream] URL not bound: ready={ready}, urlReady={effectiveUrl != null}, private={privateNow}, streamId={streamId}, {encoder.ReadableStreamState}");
                return;
            }

            if (attempt == 0 || attempt % 30 == 0)
                Msg($"[RemoteStream] Waiting before URL bind: attempt={attempt}, ready={ready}, urlReady={effectiveUrl != null}, private={privateNow}, {encoder.ReadableStreamState}");

            root.World.RunInUpdates(StreamBindRetryUpdates, () => BindStreamUrlWhenReady(attempt + 1));
        }
    }

    private static void ScheduleRemoteStreamPlaybackWatch(
        Slot root,
        DesktopSession session,
        VideoTextureProvider videoTex,
        int streamId)
    {
        int checkCount = 0;
        bool loggedPlaybackReady = false;
        bool streamUrlBound = false;
        root.World.RunInUpdates(30, () => CheckVideoState());

        void CheckVideoState()
        {
            if (videoTex == null || videoTex.IsDestroyed || root.IsDestroyed) return;
            checkCount++;
            bool assetAvail = videoTex.IsAssetAvailable;
            string playbackEngine = videoTex.CurrentPlaybackEngine?.Value ?? "null";
            bool isPlaying = videoTex.IsPlaying;
            float clockErr = videoTex.CurrentClockError?.Value ?? -1f;
            streamUrlBound = session.StreamUrl != null || streamUrlBound;
            if (assetAvail && !loggedPlaybackReady)
            {
                loggedPlaybackReady = true;
                Msg($"[RemoteStream] Playback ready: streamId={streamId} engine={playbackEngine}");
            }

            if (streamUrlBound && assetAvail && !isPlaying && IsLocalStreamPlaybackAllowedNow(session))
            {
                videoTex.Play();
                Msg($"[RemoteStream] Restarted VideoTextureProvider playback streamId={streamId} clockErr={clockErr:F2}");
            }

            if (checkCount < 10)
                root.World.RunInUpdates(60, () => CheckVideoState());
            else if (checkCount < 30)
                root.World.RunInUpdates(60 * 30, () => CheckVideoState());
        }
    }
}

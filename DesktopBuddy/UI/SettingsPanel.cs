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
    private const float SettingsPanelZOffset = -0.018f;
    private const int SettingsPanelRenderQueue = SettingsUiRenderQueue;
    private const float SettingsStickScrollDeadzone = 0.16f;
    private const float SettingsStickScrollPixelsPerTick = 36f;
    private static readonly colorX SettingsBg = new(0.055f, 0.06f, 0.072f, 0.84f);
    private static readonly colorX SettingsPanel = new(0.085f, 0.095f, 0.115f, 0.94f);
    private static readonly colorX SettingsPanelSoft = new(0.115f, 0.125f, 0.15f, 0.94f);
    private static readonly colorX SettingsAccent = new(0.16f, 0.42f, 0.48f, 0.98f);
    private static readonly colorX SettingsAccentSoft = new(0.13f, 0.24f, 0.28f, 0.96f);
    private static readonly colorX SettingsGradientPurple = new(0.68f, 0.08f, 1f, 0.98f);
    private static readonly colorX SettingsGradientBlue = new(0.05f, 0.5f, 1f, 0.98f);
    private static readonly colorX SettingsGradientMid = new(0.35f, 0.22f, 0.95f, 0.98f);
    private static readonly colorX SettingsExperimentalOrange = new(1f, 0.48f, 0.08f, 0.98f);
    private static readonly colorX SettingsStatusGood = new(0.12f, 0.58f, 0.28f, 0.98f);
    private static readonly colorX SettingsStatusWarn = new(1f, 0.5f, 0.08f, 0.98f);
    private static readonly colorX SettingsStatusBad = new(0.72f, 0.1f, 0.16f, 0.98f);
    private static readonly colorX SettingsStatusNeutral = new(0.26f, 0.3f, 0.38f, 0.98f);
    private static readonly colorX SettingsText = new(0.93f, 0.94f, 0.97f, 1f);
    private static readonly colorX SettingsSubtext = new(0.68f, 0.72f, 0.78f, 1f);
    private static readonly Uri DefaultViewerAvatar = new("resdb:///bb7d7f1414e0c0a44b4684ecd2a5dc2086c18b3f70c9ed53d467fe96af94e9a9.png");

    private static readonly (SettingsPanelTab Tab, string Label, string Glyph)[] SettingsTabs =
    {
        (SettingsPanelTab.Viewers, "Viewers", "\U0001F465"),
        (SettingsPanelTab.Stream, "Stream", "\U0001F4E1"),
        (SettingsPanelTab.Network, "Network", "\u2601"),
        (SettingsPanelTab.Devices, "Devices", "\U0001F3A5"),
        (SettingsPanelTab.Audio, "Audio", "\U0001F50A"),
        (SettingsPanelTab.Debug, "Debug", "\U0001F9F0"),
        (SettingsPanelTab.UpdateInfo, "Info", "\u2139"),
    };

    private static readonly (int Value, string Label)[] StreamResolutionOptions =
    {
        (1280, "720p"),
        (1920, "1080p"),
        (2560, "1440p"),
        (3840, "4K"),
    };

    private static readonly (int Value, string Label)[] StreamFpsOptions =
    {
        (30, "30"),
        (60, "60"),
        (90, "90"),
        (120, "120"),
    };

    private static void ToggleSettingsPanel(Slot root, DesktopSession session, int width, int height, float canvasScale, float curvature)
    {
        OpenSettingsPanel(root, session, width, height, canvasScale, curvature, null, toggle: true);
    }

    private static void OpenSettingsPanel(Slot root, DesktopSession session, int width, int height, float canvasScale, float curvature, SettingsPanelTab? tab = null, bool toggle = false)
    {
        if (root == null || root.IsDestroyed || session == null)
            return;

        if (session.SettingsPanel == null || session.SettingsPanel.SurfaceSlot == null || session.SettingsPanel.SurfaceSlot.IsDestroyed)
            CreateSettingsPanel(root, session, width, height, canvasScale, curvature);

        var state = session.SettingsPanel;
        if (state?.SurfaceSlot == null || state.SurfaceSlot.IsDestroyed)
            return;

        if (tab.HasValue)
            state.ActiveTab = tab.Value;

        bool active = toggle ? !state.SurfaceSlot.ActiveSelf : true;
        state.SurfaceSlot.ActiveSelf = active;
        if (state.RenderHost != null && !state.RenderHost.IsDestroyed)
            state.RenderHost.ActiveSelf = active;

        if (active)
        {
            RebuildSettingsPanel(state, session);
            StartSettingsStickScrollLoop(state);
        }
        else
        {
            state.StickScrollGeneration++;
            StopVirtualCameraPreview(session);
            FlushSettingsConfig();
        }
    }

    private static void CreateSettingsPanel(Slot root, DesktopSession session, int width, int height, float canvasScale, float curvature)
    {
        (int modalW, int modalH) = GetSettingsModalSize(width, height);
        int renderW = modalW;
        int renderH = modalH;

        var host = root.AddSlot("DesktopBuddySettingsRenderHost", false);
        host.PersistentSelf = false;
        host.AttachComponent<HiddenLayer>();
        host.ActiveSelf = false;
        root.Destroyed += _ =>
        {
            if (host != null && !host.IsDestroyed)
                host.Destroy();
        };

        var renderRoot = host.AddSlot("SettingsRender");
        renderRoot.AttachComponent<HiddenLayer>();

        var cameraSlot = host.AddSlot("SettingsCamera");
        cameraSlot.LocalPosition = new float3(0f, 0f, -1f);
        var renderTexture = cameraSlot.AttachComponent<RenderTextureProvider>();
        renderTexture.Size.Value = new int2(renderW, renderH);
        renderTexture.WrapModeU.Value = TextureWrapMode.Clamp;
        renderTexture.WrapModeV.Value = TextureWrapMode.Clamp;

        var camera = cameraSlot.AttachComponent<Camera>();
        camera.Projection.Value = CameraProjection.Orthographic;
        camera.OrthographicSize.Value = renderH * 0.5f;
        camera.UseTransformScale.Value = true;
        camera.Clear.Value = CameraClearMode.Color;
        camera.ClearColor.Value = colorX.Clear;
        camera.NearClipping.Value = 0.01f;
        camera.FarClipping.Value = 4f;
        camera.Postprocessing.Value = false;
        camera.RenderShadows.Value = false;
        camera.ForwardOnly.Value = true;
        camera.RenderTexture.Target = renderTexture;
        camera.SelectiveRender.Add(renderRoot);

        var canvasSlot = renderRoot.AddSlot("SettingsCanvas");
        var canvas = canvasSlot.AttachComponent<Canvas>();
        canvas.Size.Value = new float2(renderW, renderH);
        canvas.Collider.Target.SetTrigger();
        DesktopCanvasIds.Add(canvas.ReferenceID);
        canvas.Destroyed += _ => DesktopCanvasIds.Remove(canvas.ReferenceID);
        Msg($"[Settings] Registered canvas {canvas.ReferenceID} for locomotion suppression");

        var state = new SettingsPanelState
        {
            RenderHost = host,
            RenderRoot = renderRoot,
            Canvas = canvas,
            RenderTexture = renderTexture,
            Camera = camera,
            OwnerRoot = root,
            Session = session,
            RenderWidth = renderW,
            RenderHeight = renderH,
            ModalWidth = modalW,
            ModalHeight = modalH,
            CanvasScale = canvasScale,
            ActiveTab = SettingsPanelTab.Viewers
        };
        session.SettingsPanel = state;

        var mesh = AddCurvedRenderPlane(
            root,
            "SettingsCurvedMesh",
            modalW,
            modalH,
            canvasScale,
            0f,
            SettingsPanelZOffset,
            renderTexture,
            camera,
            addCollider: true,
            sidedness: Sidedness.Front,
            zWrite: ZWrite.Off,
            offsetUnits: 80f,
            blendMode: BlendMode.Alpha,
            renderQueue: SettingsPanelRenderQueue,
            alphaCutoff: 0.01f);
        mesh.Curvature.Value = curvature;
        mesh.Slot.ActiveSelf = false;
        state.Mesh = mesh;
        state.SurfaceSlot = mesh.Slot;
        state.BackgroundBlur = AddCurvedMeshBackdropBlur(mesh.Slot, mesh, 64, 0.012f, SettingsBackdropBlurRenderQueue);
        state.BackgroundBlurMask = TextureProviderSettings.ClampWrap(mesh.Slot.AttachComponent<StaticTexture2D>());
        UpdateSettingsBlurMask(state);
        RegisterTopBarRaycastPortal(mesh.Slot, renderRoot);

        BuildSettingsPanelShell(state, session);
        Msg("[Settings] Created shared curved settings panel");
    }

    private static void BuildSettingsPanelShell(SettingsPanelState state, DesktopSession session)
    {
        state.Canvas.Slot.DestroyChildren();

        var ui = new UIBuilder(state.Canvas);
        var rounded = state.Canvas.Slot.GetComponent<SpriteProvider>();
        if (rounded == null)
            rounded = state.Canvas.Slot.AttachComponent<SpriteProvider>();
        rounded = TextureProviderSettings.ClampWrap(rounded);
        rounded.Texture.Target = UIBuilder.GetCircleTexture(state.Canvas.World);
        rounded.Borders.Value = float4.One * 0.49f;
        rounded.FixedSize.Value = 28f;
        ui.Style.ButtonSprite = rounded;
        ui.Style.NineSliceSizing = NineSliceSizing.FixedSize;
        ui.Style.MinHeight = 44f;
        ui.Style.PreferredHeight = 44f;

        var bg = ui.Image(new colorX(0.055f, 0.06f, 0.072f, 0.8f));
        state.ModalRect = bg.RectTransform;
        SetSettingsModalRect(state);
        bg.Sprite.Target = rounded;
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.VerticalLayout(14f, paddingTop: 22f, paddingRight: 22f, paddingBottom: 22f, paddingLeft: 22f);

        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleHeight = -1f;
        var header = ui.Empty("Header");
        var headerUi = new UIBuilder(header);
        headerUi.LayoutTarget = header;
        var headerLayout = headerUi.HorizontalLayout(12f, 0f, 0f, 0f, 0f, Alignment.MiddleCenter);
        headerLayout.ForceExpandWidth.Value = true;
        headerLayout.ForceExpandHeight.Value = true;

        headerUi.Style.FlexibleWidth = 1f;
        headerUi.Style.MinHeight = 56f;
        headerUi.Style.PreferredHeight = 56f;
        var title = headerUi.Text("DesktopBuddy Settings", bestFit: true, alignment: Alignment.MiddleLeft);
        title.Size.Value = 28f;
        title.Color.Value = SettingsText;

        headerUi.Style.FlexibleWidth = -1f;
        headerUi.Style.MinWidth = 42f;
        headerUi.Style.PreferredWidth = 42f;
        headerUi.Style.MinHeight = 38f;
        headerUi.Style.PreferredHeight = 38f;
        var close = headerUi.Button(OfficialAssets.Common.Icons.Cross, SettingsPanelSoft, SettingsText);
        StyleSettingsButton(close, false);
        close.LocalPressed += (_, data) =>
        {
            if (state.SurfaceSlot != null && !state.SurfaceSlot.IsDestroyed)
                state.SurfaceSlot.ActiveSelf = false;
            if (state.RenderHost != null && !state.RenderHost.IsDestroyed)
                state.RenderHost.ActiveSelf = false;
            StopVirtualCameraPreview(session);
            FlushSettingsConfig();
        };

        ui.Style.MinHeight = 62f;
        ui.Style.PreferredHeight = 62f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.FlexibleWidth = 1f;
        state.TabRoot = ui.Empty("Tabs");

        ui.Style.FlexibleHeight = 1f;
        ui.Style.MinHeight = 0f;
        ui.Style.FlexibleWidth = 1f;
        state.ContentRoot = ui.Empty("Content");

        RebuildSettingsPanel(state, session);
    }

    private static void RebuildSettingsPanel(SettingsPanelState state, DesktopSession session)
    {
        session ??= state.Session;
        SyncLiveCullingStateFromConfig(state);
        RebuildSettingsTabs(state, session);
        RebuildSettingsContent(state, session);
        UpdateCullingPreview(session, state);
    }

    private static void RebuildSettingsTabs(SettingsPanelState state, DesktopSession session)
    {
        state.TabRoot.DestroyChildren();
        DestroyLayoutControllers(state.TabRoot);
        var ui = new UIBuilder(state.TabRoot);
        ui.LayoutTarget = state.TabRoot;
        ui.HorizontalLayout(8f, 0f, 2f, 0f, 2f, Alignment.MiddleCenter);
        foreach (var tab in SettingsTabs)
        {
            ui.Style.MinWidth = 86f;
            ui.Style.PreferredWidth = 86f;
            ui.Style.MinHeight = 54f;
            ui.Style.PreferredHeight = 54f;
            ui.Style.FlexibleWidth = -1f;
            ui.Style.FlexibleHeight = -1f;
            bool active = tab.Tab == state.ActiveTab;
            var tint = active ? SettingsAccent : SettingsPanelSoft;
            var btn = ui.Button(tab.Glyph, tint);
            StyleSettingsButton(btn, active);
            btn.Slot.Name = "Tab " + tab.Label;
            if (btn.Label != null)
            {
                btn.Label.Size.Value = 25f;
                btn.Label.Color.Value = SettingsText;
                btn.Label.Align = Alignment.MiddleCenter;
            }
            var captured = tab.Tab;
            btn.LocalPressed += (_, data) =>
            {
                state.ActiveTab = captured;
                RebuildSettingsPanel(state, session);
            };
        }
    }

    private static void RebuildSettingsContent(SettingsPanelState state, DesktopSession session)
    {
        session ??= state.Session;
        StopVirtualCameraPreview(session);
        state.ContentRoot.DestroyChildren();
        DestroyLayoutControllers(state.ContentRoot);
        var ui = new UIBuilder(state.ContentRoot);
        state.DebugLogText = null;
        state.DebugLogScroll = null;
        state.ContentScroll = null;
        state.DebugLogContent = null;
        state.Scrollbars.Clear();
        ui.LayoutTarget = state.ContentRoot;
        ui.VerticalLayout(0f, 0f, 0f, 0f, 0f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: true);
        var contentUi = BeginRoundedScroll(ui, state, "SettingsContentScroll", 0f, Alignment.TopLeft, out var contentScroll);
        state.ContentScroll = contentScroll;

        switch (state.ActiveTab)
        {
            case SettingsPanelTab.Viewers:
                BuildViewersTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Stream:
                BuildStreamTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Audio:
                BuildAudioTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Network:
                BuildNetworkTab(contentUi, state);
                break;
            case SettingsPanelTab.Devices:
                BuildDevicesTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Debug:
                BuildDebugTab(contentUi, state, session);
                break;
            case SettingsPanelTab.UpdateInfo:
                BuildUpdateInfoTab(contentUi, state, session);
                break;
        }
        ScheduleScrollbarGeometryUpdate(state);
    }

    private static void StartSettingsStickScrollLoop(SettingsPanelState state)
    {
        if (state?.OwnerRoot?.World == null)
            return;

        int generation = ++state.StickScrollGeneration;

        void Tick()
        {
            if (state.OwnerRoot == null || state.OwnerRoot.IsDestroyed ||
                state.SurfaceSlot == null || state.SurfaceSlot.IsDestroyed ||
                !state.SurfaceSlot.ActiveSelf || generation != state.StickScrollGeneration)
                return;

            ProcessSettingsStickScroll(state);
            state.OwnerRoot.World.RunInUpdates(1, Tick);
        }

        state.OwnerRoot.World.RunInUpdates(1, Tick);
    }

    private static void ProcessSettingsStickScroll(SettingsPanelState state)
    {
        var world = state?.OwnerRoot?.World;
        var localUserRoot = world?.LocalUser?.Root;
        if (world == null || localUserRoot == null || state.RenderRoot == null || state.RenderRoot.IsDestroyed)
            return;

        var handler = localUserRoot.GetRegisteredComponent((InteractionHandler h) => h.Side.Value == Chirality.Right);
        var currentTouchable = handler?.Laser?.CurrentTouchable;
        if (currentTouchable == null || !(currentTouchable is IAxisActionReceiver receiver))
            return;

        var touchableSlot = currentTouchable.Slot;
        if (touchableSlot == null || !touchableSlot.IsChildOf(state.RenderRoot, includeSelf: true))
            return;

        var controller = world.InputInterface.GetControllerNode(Chirality.Right);
        if (controller == null)
            return;

        float axisY = controller.Axis.Value.y;
        if (Math.Abs(axisY) <= SettingsStickScrollDeadzone)
            return;

        receiver.ProcessAxis(handler.Laser.TouchSource, new float2(0f, axisY * SettingsStickScrollPixelsPerTick));
    }

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
                UpdateViewerCullingTrigger(session);
                RebuildSettingsPanel(state, session);
            });
        AddCheckbox(ui, state, "Preview culling guide", Config?.GetValue(ViewerCullingPreview) ?? false, value =>
        {
            state.ViewerCullingPreviewEnabled = value;
            SaveConfigValue(ViewerCullingPreview, value);
            UpdateViewerCullingTrigger(session);
            UpdateCullingPreview(session, state);
            session?.Root?.World?.RunInUpdates(1, () => UpdateCullingPreview(session, state));
        });

        AddFloatSlider(ui, state, "Range", state.ViewerDistance, 1f, 10f, value =>
        {
            state.ViewerDistance = value;
            state.ViewerFrustumDepth = value;
            SaveConfigValue(ViewerDistance, value);
            SaveConfigValue(ViewerFrustumDepth, value);
            UpdateViewerCullingTrigger(session);
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
                UpdateViewerCullingTrigger(session);
                UpdateCullingPreview(session, state);
                session?.Root?.World?.RunInUpdates(1, () => UpdateCullingPreview(session, state));
            });
        }
    }

    private static void RequestStreamEncoderRestart(DesktopSession session, string reason)
    {
        try
        {
            var targets = ActiveSessions
                .Where(s => s != null && !s.Cleaned && s.StreamId > 0 &&
                    (session == null || s == session || s.Root?.World == session.Root?.World))
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

    private static void BuildStreamTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Stream");
        int currentResolution = NormalizeStreamResolution(RuntimeMaxStreamResolution);
        int currentFps = NormalizeStreamFps(RuntimeStreamFps);
        AddOptionRow(ui, state, "Resolution", currentResolution.ToString(CultureInfo.InvariantCulture),
            StreamResolutionOptions.Select(option => (option.Value.ToString(CultureInfo.InvariantCulture), option.Label)).ToArray(),
            value =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected))
                    return;
                selected = NormalizeStreamResolution(selected);
                SaveConfigValue(MaxStreamResolution, selected);
                SaveConfigValue(Bitrate, RecommendedBitrateMbps(selected, currentFps));
                RequestStreamEncoderRestart(session, "stream resolution");
            }, preferredColumns: 4, cellWidth: 108f);
        AddOptionRow(ui, state, "FPS", currentFps.ToString(CultureInfo.InvariantCulture),
            StreamFpsOptions.Select(option => (option.Value.ToString(CultureInfo.InvariantCulture), option.Label)).ToArray(),
            value =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected))
                    return;
                selected = NormalizeStreamFps(selected);
                SaveConfigValue(StreamFps, selected);
                SaveConfigValue(Bitrate, RecommendedBitrateMbps(currentResolution, selected));
                RequestStreamEncoderRestart(session, "stream FPS");
            }, preferredColumns: 4, cellWidth: 108f);

        int currentBitrate = Math.Clamp(RuntimeBitrateMbps, 1, 200);
        AddFloatSlider(ui, state, "Bitrate Mbps", currentBitrate, 4f, 80f,
            value =>
            {
                SaveConfigValue(Bitrate, Math.Clamp((int)MathF.Round(value), 1, 200));
                RequestStreamEncoderRestart(session, "stream bitrate");
            }, commitOnReleaseOnly: true, wholeNumbers: true);

        AddSectionHeader(ui, "Encoder");
        AddOptionRow(ui, state, "Preference", RuntimeEncoderPreference,
            new[]
            {
                ("auto", "Auto"), ("hevc_nvenc", "HEVC NVENC"), ("h264_nvenc", "H264 NVENC"),
                ("hevc_amf", "HEVC AMF"), ("h264_amf", "H264 AMF"), ("hevc_qsv", "HEVC QSV"),
                ("h264_qsv", "H264 QSV"), ("libx264", "libx264"), ("libx265", "libx265")
            },
            value =>
            {
                SaveConfigValue(EncoderPreference, value);
                RequestStreamEncoderRestart(session, "encoder preference");
            });

        string currentLuid = Config?.GetValue(PreferredGpuLuid)?.Trim() ?? "";
        var gpus = WgcCapture.EnumerateAdapters()
            .Where(g => !g.IsBasicRenderDriver && !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => NormalizeGpuDisplayName(g.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var gpuOptions = new List<(string Value, string Label)> { ("", "Auto") };
        gpuOptions.AddRange(gpus.Select(gpu => ("0x" + gpu.Luid.ToString("X16", CultureInfo.InvariantCulture), NormalizeGpuDisplayName(gpu.Name))));
        AddOptionRow(ui, state, "Preferred GPU", currentLuid, gpuOptions.ToArray(),
            value =>
            {
                SaveConfigValue(PreferredGpuLuid, value ?? "");
                RequestStreamEncoderRestart(session, "preferred GPU");
            }, cellWidth: 220f);
    }

    private static string NormalizeGpuDisplayName(string name)
    {
        return string.Join(" ", (name ?? "Unnamed GPU").Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void BuildAudioTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Stream Audio");
        AddFloatSlider(ui, state, "Volume", Config?.GetValue(StreamAudioOutputVolume) ?? 1f, 0f, 1f, value =>
        {
            SaveConfigValue(StreamAudioOutputVolume, NormalizeStreamAudioOutputVolume(value));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddOptionRow(ui, state, "Mode", NormalizeStreamAudioGlobalMode(Config?.GetValue(StreamAudioGlobalMode)),
            new[] { ("global", "Global"), ("auto", "Auto"), ("positional", "Positional") },
            value =>
            {
                SaveConfigValue(StreamAudioGlobalMode, NormalizeStreamAudioGlobalMode(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 3, cellWidth: 126f);

        AddSectionHeader(ui, "Spatial Output");
        AddCheckbox(ui, state, "Spatialize", Config?.GetValue(StreamAudioSpatialize) ?? true, value =>
        {
            SaveConfigValue(StreamAudioSpatialize, value);
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Spatial blend", Config?.GetValue(StreamAudioSpatialBlend) ?? 1f, 0f, 1f, value =>
        {
            SaveConfigValue(StreamAudioSpatialBlend, Math.Clamp(value, 0f, 1f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddOptionRow(ui, state, "Distance space", NormalizeStreamAudioDistanceSpace(Config?.GetValue(StreamAudioDistanceSpace)),
            new[] { ("global", "Global"), ("local", "Local") },
            value =>
            {
                SaveConfigValue(StreamAudioDistanceSpace, NormalizeStreamAudioDistanceSpace(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 2, cellWidth: 126f);
        AddOptionRow(ui, state, "Rolloff", NormalizeStreamAudioRolloffMode(Config?.GetValue(StreamAudioRolloffMode)),
            new[] { ("logarithmic_fade_off", "Log fade"), ("linear", "Linear") },
            value =>
            {
                SaveConfigValue(StreamAudioRolloffMode, NormalizeStreamAudioRolloffMode(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 2, cellWidth: 126f);
        AddFloatSlider(ui, state, "Min distance", Config?.GetValue(StreamAudioMinDistance) ?? 1f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioMinDistance, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Max distance", Config?.GetValue(StreamAudioMaxDistance) ?? 30f, 1f, 50f, value =>
        {
            SaveConfigValue(StreamAudioMaxDistance, Math.Clamp(value, 1f, 50f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Spatial start", Config?.GetValue(StreamAudioSpatializationStartDistance) ?? 0.01f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioSpatializationStartDistance, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Transition range", Config?.GetValue(StreamAudioSpatializationTransitionRange) ?? 0.01f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioSpatializationTransitionRange, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Min scale", Config?.GetValue(StreamAudioMinScale) ?? 0f, 0f, 1000f, value =>
        {
            SaveConfigValue(StreamAudioMinScale, Math.Clamp(value, 0f, 1000f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Max scale", Config?.GetValue(StreamAudioMaxScale) ?? 1000f, 0f, 1000f, value =>
        {
            SaveConfigValue(StreamAudioMaxScale, Math.Clamp(value, 0f, 1000f));
            ApplyStreamAudioSettingsToAllSessions();
        });

        AddSectionHeader(ui, "Playback");
        AddOptionRow(ui, state, "Type group", NormalizeStreamAudioTypeGroup(Config?.GetValue(StreamAudioTypeGroup)),
            new[] { ("multimedia", "Multimedia"), ("sound_effect", "Sound"), ("voice", "Voice"), ("ui", "UI") },
            value =>
            {
                SaveConfigValue(StreamAudioTypeGroup, NormalizeStreamAudioTypeGroup(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 4, cellWidth: 108f);
        AddCheckbox(ui, state, "Ignore audio effects", Config?.GetValue(StreamAudioIgnoreAudioEffects) ?? true, value =>
        {
            SaveConfigValue(StreamAudioIgnoreAudioEffects, value);
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Pitch", Config?.GetValue(StreamAudioPitch) ?? 1f, 0.5f, 2f, value =>
        {
            SaveConfigValue(StreamAudioPitch, Math.Clamp(value, 0.5f, 2f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Doppler", Config?.GetValue(StreamAudioDopplerLevel) ?? 0f, 0f, 1f, value =>
        {
            SaveConfigValue(StreamAudioDopplerLevel, Math.Clamp(value, 0f, 1f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddIntField(ui, state, "Priority", Config?.GetValue(StreamAudioPriority) ?? 128, 0, 256, value =>
        {
            SaveConfigValue(StreamAudioPriority, Math.Clamp(value, 0, 256));
            ApplyStreamAudioSettingsToAllSessions();
        });
    }

    private static int NormalizeStreamResolution(int value)
    {
        return StreamResolutionOptions
            .OrderBy(option => Math.Abs(option.Value - value))
            .First().Value;
    }

    private static int NormalizeStreamFps(int value)
    {
        return StreamFpsOptions
            .OrderBy(option => Math.Abs(option.Value - value))
            .First().Value;
    }

    private static int RecommendedBitrateMbps(int longEdge, int fps)
    {
        longEdge = NormalizeStreamResolution(longEdge);
        fps = NormalizeStreamFps(fps);
        float width = longEdge;
        float height = longEdge * 9f / 16f;
        float mbps = width * height * fps * 0.11f / 1_000_000f;
        return Math.Clamp((int)MathF.Round(mbps), 4, 80);
    }

    private static void BuildNetworkTab(UIBuilder ui, SettingsPanelState state)
    {
        AddSectionHeader(ui, "Stream");
        AddStatusRow(ui, state, "Stream Server", StreamServer == null ? "Stopped" : "Running", StreamServer == null ? SettingsStatusBad : SettingsStatusGood);
        bool cloudflareMode = NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "cloudflare";
        AddStatusRow(ui, state, "Cloudflare", cloudflareMode ? (TunnelUrl == null ? "Waiting" : "Connected") : "Off",
            cloudflareMode ? (TunnelUrl == null ? SettingsStatusWarn : SettingsStatusGood) : SettingsStatusNeutral);
        AddStatusRow(ui, state, "Port", STREAM_PORT.ToString(CultureInfo.InvariantCulture), SettingsStatusNeutral);
        AddOptionRow(ui, state, "Access", NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)),
            new[] { ("cloudflare", "Cloudflare"), ("port_forward", "Port forward") },
            value =>
            {
                SaveConfigValue(StreamNetworkMode, NormalizeStreamNetworkMode(value));
                ApplyStreamNetworkMode();
                RequestStreamEncoderRestart(state.Session, "network mode");
            });

        if (NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "port_forward")
        {
            AddCheckbox(ui, state, "Auto NAT / UPnP", Config?.GetValue(PortForwardUseNat) ?? false, value =>
            {
                SaveConfigValue(PortForwardUseNat, value);
                ApplyStreamNetworkMode();
            });
            AddOptionRow(ui, state, "Host", NormalizePortForwardHostMode(Config?.GetValue(PortForwardHostMode)),
                new[] { ("auto", "Auto public IP"), ("manual", "Manual") },
                value =>
                {
                    SaveConfigValue(PortForwardHostMode, NormalizePortForwardHostMode(value));
                    ApplyStreamNetworkMode();
                });
            if (NormalizePortForwardHostMode(Config?.GetValue(PortForwardHostMode)) == "manual")
            {
                AddStringField(ui, state, "Manual IP / host", Config?.GetValue(PortForwardHost) ?? "", value =>
                {
                    SaveConfigValue(PortForwardHost, value.Trim());
                    ApplyStreamNetworkMode();
                });
            }
            else
            {
                AddInfoRow(ui, state, "Auto public IP", ResolvePortForwardHost() ?? "");
            }
        }
        AddSectionHeader(ui, "MediaMTX");
        AddCheckbox(ui, state, "Use MediaMTX", Config?.GetValue(UseMediaMtx) ?? false, value =>
        {
            SaveConfigValue(UseMediaMtx, value);
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX mode");
        });
        AddStringField(ui, state, "Host", Config?.GetValue(MediaMtxHost) ?? "", value =>
        {
            SaveConfigValue(MediaMtxHost, value.Trim());
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX host");
        });
        AddIntField(ui, state, "Port", Config?.GetValue(MediaMtxPort) ?? 8554, 1, 65535, value =>
        {
            SaveConfigValue(MediaMtxPort, value);
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX port");
        });
        AddStringField(ui, state, "Stream name", Config?.GetValue(MediaMtxStreamName) ?? "", value =>
        {
            SaveConfigValue(MediaMtxStreamName, value.Trim());
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX stream name");
        });
    }

    private static void BuildDevicesTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Virtual Devices");
        bool softCamRegistered = SoftCamSetup.IsRegistered();
        bool vbCableInstalled = VBCableSetup.IsInstalled();
        AddStatusRow(ui, state, "SoftCam", softCamRegistered ? "Installed" : "Missing", softCamRegistered ? SettingsStatusGood : SettingsStatusBad);
        AddStatusRow(ui, state, "VB-Cable", vbCableInstalled ? "Installed" : "Missing", vbCableInstalled ? SettingsStatusGood : SettingsStatusBad);
        AddCheckbox(ui, state, "Virtual camera enabled", VCam != null && !VCam.ManuallyDisabled, value =>
        {
            if (VCam != null) VCam.ManuallyDisabled = !value;
        });
        AddVirtualCameraPreview(ui, state, session);
        AddCheckbox(ui, state, "Virtual mic muted", session?.VMicMuted ?? false, value =>
        {
            if (session != null) session.VMicMuted = value;
            if (VMic != null) VMic.Muted = value;
        });
        AddSectionHeader(ui, "Audio");
        AddCheckboxWithBadge(ui, state, "Spatial audio", "(Experimental)", SettingsExperimentalOrange, Config?.GetValue(SpatialAudioEnabled) ?? false,
            value => SaveConfigValue(SpatialAudioEnabled, value));
    }

    private static void AddVirtualCameraPreview(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Camera Preview");

        float previewHeight = Math.Clamp((state.ModalWidth - 120f) * 9f / 16f, 150f, 340f);
        ui.Style.MinHeight = previewHeight;
        ui.Style.PreferredHeight = previewHeight;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var frame = ui.Image(new colorX(0.065f, 0.072f, 0.09f, 0.78f));
        frame.Sprite.Target = CreateRoundedSprite(frame.Slot, state.Canvas.World, 18f);
        frame.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(frame.RectTransform);
        ui.LayoutTarget = frame.Slot;

        var previewTexture = StartVirtualCameraPreview(state, session);
        if (previewTexture == null)
        {
            ui.VerticalLayout(0f, paddingTop: 10f, paddingRight: 10f, paddingBottom: 10f, paddingLeft: 10f, childAlignment: Alignment.MiddleCenter);
            ui.Style.MinHeight = previewHeight - 20f;
            ui.Style.PreferredHeight = previewHeight - 20f;
            ui.Style.FlexibleWidth = 1f;
            var unavailable = ui.Text("Virtual camera preview unavailable", bestFit: true, alignment: Alignment.MiddleCenter);
            unavailable.Size.Value = 18f;
            unavailable.Color.Value = SettingsSubtext;
            ui.NestOut();
            return;
        }

        ui.PushStyle();
        ui.Style.SupressLayoutElement = true;
        var maskSprite = CreateRoundedSprite(frame.Slot, state.Canvas.World, 16f);
        ui.SpriteMask(maskSprite, false, out var maskImage);
        maskImage.Tint.Value = colorX.White;
        maskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        maskImage.RectTransform.AnchorMin.Value = float2.Zero;
        maskImage.RectTransform.AnchorMax.Value = float2.One;
        maskImage.RectTransform.OffsetMin.Value = new float2(10f, 10f);
        maskImage.RectTransform.OffsetMax.Value = new float2(-10f, -10f);
        ui.Nest();
        var preview = ui.RawImage(previewTexture, preserveAspect: true);
        preview.Tint.Value = colorX.White;
        preview.InteractionTarget.Value = false;
        preview.RectTransform.AnchorMin.Value = float2.Zero;
        preview.RectTransform.AnchorMax.Value = float2.One;
        preview.RectTransform.OffsetMin.Value = float2.Zero;
        preview.RectTransform.OffsetMax.Value = float2.Zero;
        ui.NestOut();
        ui.PopStyle();

        ui.NestOut();
    }

    private static RenderTextureProvider StartVirtualCameraPreview(SettingsPanelState state, DesktopSession session)
    {
        if (state?.ContentRoot == null || state.ContentRoot.IsDestroyed ||
            session?.VCamCamera == null || session.VCamCamera.IsDestroyed)
            return null;

        StopVirtualCameraPreview(session);

        var textureSlot = state.ContentRoot.AddSlot("VirtualCameraPreviewTexture", false);
        textureSlot.PersistentSelf = false;
        var texture = textureSlot.AttachComponent<RenderTextureProvider>();
        texture.Size.Value = new int2(640, 360);
        texture.WrapModeU.Value = TextureWrapMode.Clamp;
        texture.WrapModeV.Value = TextureWrapMode.Clamp;

        session.VCamPreviewTexture = texture;
        session.VCamCamera.RenderTexture.Target = texture;
        return texture;
    }

    private static void StopVirtualCameraPreview(DesktopSession session)
    {
        if (session == null)
            return;

        try
        {
            var texture = session.VCamPreviewTexture;
            if (session.VCamCamera != null && !session.VCamCamera.IsDestroyed &&
                texture != null && !texture.IsDestroyed &&
                session.VCamCamera.RenderTexture.Target == texture)
            {
                session.VCamCamera.RenderTexture.Target = null;
            }

            if (texture != null && !texture.IsDestroyed)
                texture.Slot.Destroy();

            session.VCamPreviewTexture = null;
        }
        catch (Exception ex)
        {
            Msg($"[Settings] Virtual camera preview cleanup failed: {ex.Message}");
            session.VCamPreviewTexture = null;
        }
    }

    private static void BuildDebugTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Debug");
        AddButtonRow(ui, state, "Export combined log", () =>
        {
            try { Log.ExportCombinedLog(); }
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
        AddLinkButtonRow(ui, state, "Repository", "https://github.com/DevL0rd/DesktopBuddy", buttonLabel: "GitHub");

        AddSectionHeader(ui, "Settings");
        AddButtonRow(ui, state, "Reset settings to defaults", () => ResetSettingsToDefaults(state, session), buttonLabel: "Reset");

        AddSectionHeader(ui, "Changelog");
        float changelogHeight = Math.Clamp((state.ModalHeight - 540f) * 3f, 360f, 720f);
        var changelogUi = BeginRoundedScroll(ui, state, "UpdateChangelogScroll", changelogHeight, Alignment.TopLeft, out _);
        string changelog = string.IsNullOrWhiteSpace(_remoteChangelog)
            ? (_updateCheckInProgress ? "Checking CHANGELOG.txt..." : "No CHANGELOG.txt release asset found.")
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

        string content = string.Join("\n", Log.GetRecentLines(100));
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

    private static UIBuilder BeginRoundedScroll(UIBuilder ui, SettingsPanelState state, string name, float minHeight, Alignment alignment, out ScrollRect scroll, colorX? frameTint = null)
    {
        ui.Style.MinHeight = minHeight;
        ui.Style.PreferredHeight = minHeight;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = minHeight <= 0f ? 1f : -1f;
        var frame = ui.Image(frameTint ?? new colorX(0.07f, 0.078f, 0.095f, 0.72f));
        frame.Sprite.Target = CreateRoundedSprite(frame.Slot, state.Canvas.World, 16f);
        frame.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(frame.RectTransform);
        ui.LayoutTarget = frame.Slot;
        ui.HorizontalLayout(10f, paddingTop: 12f, paddingRight: 10f, paddingBottom: 12f, paddingLeft: 12f, childAlignment: Alignment.MiddleCenter);

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 0f;
        ui.Style.PreferredHeight = 0f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = 1f;
        var viewport = ui.Empty(name + "Viewport");
        scroll = ScrollRect.CreateScrollRect<Image>(viewport, out var content, out var mask, out var viewportGraphic);
        scroll.Alignment = alignment;
        mask.ShowMaskGraphic.Value = false;
        viewportGraphic.Tint.Value = colorX.White;
        viewportGraphic.Sprite.Target = CreateRoundedSprite(viewport, state.Canvas.World, 14f);
        viewportGraphic.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        var scrollUi = new UIBuilder(content);
        scrollUi.LayoutTarget = content;
        scrollUi.VerticalLayout(10f, paddingTop: 4f, paddingRight: 4f, paddingBottom: 4f, paddingLeft: 4f, childAlignment: Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        scrollUi.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);

        AddScrollbarSlider(ui, state, name + "Scrollbar", scroll);
        ui.NestOut();
        return scrollUi;
    }

    private static void AddScrollbarSlider(UIBuilder ui, SettingsPanelState state, string name, ScrollRect scroll)
    {
        ui.Style.MinWidth = 18f;
        ui.Style.PreferredWidth = 18f;
        ui.Style.MinHeight = 0f;
        ui.Style.PreferredHeight = 0f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = 1f;
        var root = ui.Empty(name);
        var hit = root.AttachComponent<Image>();
        hit.Tint.Value = colorX.Clear;

        var slider = root.AttachComponent<Slider<float>>();
        slider.RequireLockInToInteract.Value = true;
        slider.RequireInitialPress.Value = true;
        slider.SlideDirection.Value = Slider<float>.Direction.Vertical;
        slider.AnchorOffset.Value = new float2(0.5f, 0f);
        slider.Min.Value = 0f;
        slider.Max.Value = 1f;
        slider.Value.Value = 1f - Math.Clamp(scroll?.NormalizedPosition.Value.y ?? 0f, 0f, 1f);

        var railSlot = root.AddSlot("Rail");
        var railRect = railSlot.GetComponentOrAttach<RectTransform>();
        railRect.AnchorMin.Value = new float2(0.5f, 0f);
        railRect.AnchorMax.Value = new float2(0.5f, 1f);
        railRect.OffsetMin.Value = new float2(-5f, 8f);
        railRect.OffsetMax.Value = new float2(5f, -8f);
        var rail = railSlot.AttachComponent<Image>();
        rail.Tint.Value = new colorX(0.02f, 0.024f, 0.032f, 0.55f);
        rail.Sprite.Target = CreateRoundedSprite(railSlot, state.Canvas.World, 8f);
        rail.NineSliceSizing.Value = NineSliceSizing.FixedSize;

        var thumbSlot = root.AddSlot("Thumb");
        var thumbRect = thumbSlot.GetComponentOrAttach<RectTransform>();
        thumbRect.SetFixedRect(new Rect(-7f, -36f, 14f, 72f), new float2(0.5f, slider.Value.Value));
        var thumb = thumbSlot.AttachComponent<Image>();
        thumb.Tint.Value = SettingsAccentSoft;
        thumb.Sprite.Target = CreateRoundedSprite(thumbSlot, state.Canvas.World, 8f);
        thumb.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ApplyPurpleBlueGradient(thumb, 8f, 1f, interactionTarget: false);

        float lastApplied = slider.Value.Value;
        var barState = new SettingsScrollbarState
        {
            Scroll = scroll,
            Root = root,
            ThumbRect = thumbRect,
            Slider = slider
        };
        slider.Value.LocalFilter = (candidate, field) =>
        {
            float clamped = Math.Clamp(candidate, 0f, 1f);
            return clamped;
        };
        slider.Value.OnValueChange += (SyncField<float> field) =>
        {
            float clamped = Math.Clamp(field.Value, 0f, 1f);
            lastApplied = clamped;
            SetScrollbarThumbValue(barState, clamped);
            if (scroll != null && !scroll.IsDestroyed)
            {
                var pos = scroll.NormalizedPosition.Value;
                scroll.NormalizedPosition.Value = new float2(pos.x, 1f - clamped);
            }
        };
        if (scroll != null)
        {
            scroll.NormalizedPosition.OnValueChange += (SyncField<float2> field) =>
            {
                if (slider.IsDestroyed)
                    return;
                float y = 1f - Math.Clamp(field.Value.y, 0f, 1f);
                lastApplied = y;
                SetScrollbarThumbValue(barState, y);
            };
        }

        state.Scrollbars.Add(barState);
        ScheduleScrollbarGeometryUpdate(state);
    }

    private static void ScheduleScrollbarGeometryUpdate(SettingsPanelState state)
    {
        var world = state?.OwnerRoot?.World;
        if (world == null)
            return;

        world.RunInUpdates(2, () => UpdateScrollbarGeometry(state));
    }

    private static void UpdateScrollbarGeometry(SettingsPanelState state)
    {
        if (state?.Scrollbars == null)
            return;

        foreach (var bar in state.Scrollbars)
        {
            if (bar?.Root == null || bar.Root.IsDestroyed || bar.Scroll == null || bar.Scroll.IsDestroyed || bar.ThumbRect == null || bar.ThumbRect.IsDestroyed)
                continue;

            var contentRect = bar.Scroll.Slot.GetComponent<RectTransform>()?.LocalComputeRect ?? default;
            var viewportRect = bar.Scroll.Slot.Parent?.GetComponent<RectTransform>()?.LocalComputeRect ?? default;
            float contentHeight = Math.Abs(contentRect.height);
            float viewportHeight = Math.Abs(viewportRect.height);
            bool needsScroll = contentHeight > viewportHeight + 2f && viewportHeight > 1f;
            bar.Root.ActiveSelf = needsScroll;
            if (!needsScroll)
                continue;

            const float padding = 8f;
            float trackHeight = Math.Max(1f, viewportHeight - padding * 2f);
            float thumbHeight = Math.Clamp(trackHeight * viewportHeight / Math.Max(contentHeight, viewportHeight), 34f, trackHeight);
            float sliderValue = bar.Slider == null || bar.Slider.IsDestroyed ? 1f - Math.Clamp(bar.Scroll.NormalizedPosition.Value.y, 0f, 1f) : bar.Slider.Value.Value;
            bar.TrackPadding = padding;
            bar.TrackHeight = trackHeight;
            bar.ThumbHeight = thumbHeight;
            SetScrollbarThumbValue(bar, sliderValue);
        }
    }

    private static void SetScrollbarThumbValue(SettingsScrollbarState bar, float value)
    {
        if (bar?.ThumbRect == null || bar.ThumbRect.IsDestroyed)
            return;

        float clamped = Math.Clamp(value, 0f, 1f);
        float trackHeight = Math.Max(1f, bar.TrackHeight);
        float thumbHeight = Math.Clamp(bar.ThumbHeight <= 0f ? 34f : bar.ThumbHeight, 1f, trackHeight);
        float travel = Math.Max(0f, trackHeight - thumbHeight);
        float thumbBottom = bar.TrackPadding + travel * clamped;
        bar.ThumbRect.SetFixedRect(new Rect(-7f, thumbBottom, 14f, thumbHeight), new float2(0.5f, 0f));
    }

    private static void DestroyLayoutControllers(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return;

        foreach (var layout in slot.GetComponents<HorizontalLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<VerticalLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<GridLayout>())
            layout.Destroy();
        foreach (var layout in slot.GetComponents<OverlappingLayout>())
            layout.Destroy();
    }

    private static void AddSectionHeader(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var bg = ui.Image(new colorX(0.035f, 0.04f, 0.052f, 0.58f));
        bg.Sprite.Target = CreateRoundedSprite(bg.Slot, ui.Root.World, 16f);
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 14f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleLeft);

        ui.Style.MinWidth = 5f;
        ui.Style.PreferredWidth = 5f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = -1f;
        var accent = ui.Image(SettingsAccent);
        accent.Sprite.Target = CreateRoundedSprite(accent.Slot, ui.Root.World, 5f);
        accent.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ApplyPurpleBlueGradient(accent, 5f, 1f, interactionTarget: false);

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        ui.Style.FlexibleWidth = 1f;
        var label = ui.Text(text, bestFit: true, alignment: Alignment.MiddleLeft);
        label.Size.Value = 23f;
        label.Color.Value = SettingsText;
        ui.NestOut();
    }

    private static void AddBodyText(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        ui.Style.FlexibleWidth = 1f;
        var label = ui.Text(text ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        label.Size.Value = 16f;
        label.Color.Value = SettingsSubtext;
    }

    private static void AddInfoRow(UIBuilder ui, SettingsPanelState state, string label, string value)
    {
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.Style.FlexibleWidth = 1f;
        var bg = ui.Image(SettingsPanel);
        bg.Sprite.Target = CreateRoundedSprite(bg.Slot, state.Canvas.World, 13f);
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(12f, paddingTop: 7f, paddingRight: 12f, paddingBottom: 7f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 32f;
        ui.Style.PreferredHeight = 32f;
        var name = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        name.Size.Value = 16f;
        name.Color.Value = SettingsSubtext;

        ui.Style.FlexibleWidth = 1f;
        var val = ui.Text(value ?? "", bestFit: true, alignment: Alignment.MiddleRight);
        val.Size.Value = 16f;
        val.Color.Value = SettingsText;
        ui.NestOut();
    }

    private static void AddStatusRow(UIBuilder ui, SettingsPanelState state, string label, string status, colorX badgeColor)
    {
        const float badgeWidth = 76f;
        const float badgeHeight = 26f;

        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(10f, paddingTop: 7f, paddingRight: 10f, paddingBottom: 7f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 16f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = badgeWidth;
        ui.Style.PreferredWidth = badgeWidth;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        var badgePill = ui.Image(badgeColor);
        var badgeElement = badgePill.Slot.GetComponent<LayoutElement>() ?? badgePill.Slot.AttachComponent<LayoutElement>();
        badgeElement.MinWidth.Value = badgeWidth;
        badgeElement.PreferredWidth.Value = badgeWidth;
        badgeElement.FlexibleWidth.Value = -1f;
        badgeElement.MinHeight.Value = badgeHeight;
        badgeElement.PreferredHeight.Value = badgeHeight;
        badgeElement.FlexibleHeight.Value = -1f;
        StyleBadgePill(badgePill, badgeColor);
        ui.NestInto(badgePill.RectTransform);
        ui.LayoutTarget = badgePill.Slot;
        var badgeLayout = ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        badgeLayout.ForceExpandWidth.Value = true;
        badgeLayout.ForceExpandHeight.Value = true;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        ui.Style.FlexibleWidth = 1f;
        var badgeText = ui.Text(status ?? "", bestFit: true, alignment: Alignment.MiddleCenter);
        badgeText.Size.Value = 13f;
        badgeText.Color.Value = SettingsText;
        ui.NestOut();
        ui.NestOut();
    }

    private static void AddViewerRow(UIBuilder ui, SettingsPanelState state, DesktopSession session, User user)
    {
        EnsureViewerStreamOverride(session, user);

        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var bg = ui.Image(new colorX(0.105f, 0.112f, 0.13f, 0.92f));
        var rounded = CreateRoundedSprite(bg.Slot, state.Canvas.World, 14f);
        bg.Sprite.Target = rounded;
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(10f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 10f, childAlignment: Alignment.MiddleLeft);

        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 42f;
        ui.Style.PreferredHeight = 42f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = -1f;
        var avatarRoot = ui.Empty("Avatar");
        var avatarUi = new UIBuilder(avatarRoot);
        avatarUi.Style.MinWidth = 42f;
        avatarUi.Style.PreferredWidth = 42f;
        avatarUi.Style.MinHeight = 42f;
        avatarUi.Style.PreferredHeight = 42f;
        var avatarSprite = CreateRoundedSprite(avatarRoot, state.Canvas.World, 10f);
        avatarUi.SpriteMask(avatarSprite, true, out var maskImage);
        maskImage.Tint.Value = new colorX(0.16f, 0.17f, 0.2f, 1f);
        maskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        avatarUi.Nest();
        var avatarTex = TextureProviderSettings.ClampWrap(avatarRoot.AttachComponent<StaticTexture2D>());
        avatarTex.URL.Value = DefaultViewerAvatar;
        if (!string.IsNullOrWhiteSpace(user?.UserID))
        {
            LoadViewerAvatarIcon(avatarRoot, avatarTex, user.UserID);
        }
        var avatar = avatarUi.RawImage(avatarTex, preserveAspect: true);
        avatar.Tint.Value = colorX.White;
        avatarUi.NestOut();

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 42f;
        ui.Style.PreferredHeight = 42f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var name = ui.Text(user?.UserName ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        name.Size.Value = 19f;
        name.Color.Value = new colorX(0.91f, 0.92f, 0.95f, 1f);

        ui.Style.MinWidth = 86f;
        ui.Style.PreferredWidth = 86f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        ui.Style.FlexibleWidth = -1f;
        string statusText = GetViewerCullingBadgeText(session, user);
        colorX statusColor = GetViewerCullingBadgeColor(session, user);
        var statusBadge = ui.Image(statusColor);
        StyleBadgePill(statusBadge, statusColor);
        ui.NestInto(statusBadge.RectTransform);
        ui.LayoutTarget = statusBadge.Slot;
        ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        var statusLabel = ui.Text(statusText, bestFit: true, alignment: Alignment.MiddleCenter);
        statusLabel.Size.Value = 12f;
        statusLabel.Color.Value = SettingsText;
        ui.NestOut();

        ui.Style.MinWidth = 58f;
        ui.Style.PreferredWidth = 58f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        ui.Style.FlexibleWidth = -1f;
        bool isOwner = IsOwnerViewer(session, user);
        bool viewerEnabled = !isOwner && IsViewerStreamEnabled(session, user);
        var toggle = ui.Button("On", SettingsAccentSoft);
        StyleSettingsButton(toggle, true);
        UpdateToggleButton(toggle, viewerEnabled);
        toggle.LocalPressed += (_, _) =>
        {
            if (isOwner)
            {
                UpdateToggleButton(toggle, false);
                return;
            }
            viewerEnabled = !viewerEnabled;
            SetViewerStreamEnabled(session, user, viewerEnabled);
            UpdateToggleButton(toggle, viewerEnabled);
            statusLabel.Content.Value = GetViewerCullingBadgeText(session, user);
            StyleBadgePill(statusBadge, GetViewerCullingBadgeColor(session, user));
        };
        ui.NestOut();
    }

    private static void LoadViewerAvatarIcon(Slot avatarRoot, StaticTexture2D avatarTex, string userId)
    {
        if (avatarRoot == null || avatarRoot.IsDestroyed ||
            avatarTex == null || avatarTex.IsDestroyed ||
            string.IsNullOrWhiteSpace(userId))
            return;

        var world = avatarRoot.World;
        var engine = world?.Engine;
        if (world == null || engine?.Cloud?.Users == null)
            return;

        Task.Run(async () =>
        {
            try
            {
                var cloudResult = await engine.Cloud.Users.GetUserCached(userId).ConfigureAwait(false);
                if (cloudResult == null || !cloudResult.IsOK)
                    return;

                string iconUrl = cloudResult.Entity?.Profile?.IconUrl;
                if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri))
                    return;

                world.RunInUpdates(0, () =>
                {
                    if (avatarRoot == null || avatarRoot.IsDestroyed ||
                        avatarTex == null || avatarTex.IsDestroyed)
                        return;

                    avatarTex.URL.Value = iconUri;
                });
            }
            catch
            {
                // Keep the default avatar if the cloud profile icon cannot be loaded.
            }
        });
    }

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

    private static SpriteProvider CreateRoundedSprite(Slot slot, World world, float fixedSize)
    {
        var sprite = TextureProviderSettings.ClampWrap(slot.GetComponent<SpriteProvider>() ?? slot.AttachComponent<SpriteProvider>());
        sprite.Texture.Target = UIBuilder.GetCircleTexture(world);
        sprite.Borders.Value = float4.One * 0.49f;
        sprite.FixedSize.Value = fixedSize;
        return sprite;
    }

    private static GradientImage ApplyPurpleBlueGradient(Image image, float fixedSize, float alpha, bool interactionTarget)
    {
        if (image == null || image.Slot == null || image.Slot.IsDestroyed)
            return null;

        image.Enabled = false;
        image.InteractionTarget.Value = false;
        var gradient = image.Slot.GetComponent<GradientImage>() ?? image.Slot.AttachComponent<GradientImage>();
        gradient.Sprite.Target = CreateRoundedSprite(image.Slot, image.Slot.World, fixedSize);
        gradient.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        gradient.PreserveAspect.Value = false;
        gradient.InteractionTarget.Value = interactionTarget;

        colorX purple = SettingsGradientPurple.SetA(alpha);
        colorX blue = SettingsGradientBlue.SetA(alpha);
        colorX mid = SettingsGradientMid.SetA(alpha);
        gradient.TintTopLeft.Value = purple;
        gradient.TintBottomLeft.Value = mid;
        gradient.TintTopRight.Value = mid;
        gradient.TintBottomRight.Value = blue;
        return gradient;
    }

    private static void ApplyButtonGradient(Button button, bool selected)
    {
        var bg = button?.Slot?.GetComponent<Image>();
        if (bg == null)
            return;

        if (!selected)
        {
            var existing = button.Slot.GetComponent<GradientImage>();
            if (existing != null)
                existing.Enabled = false;
            bg.Enabled = true;
            bg.InteractionTarget.Value = true;
            bg.Tint.Value = SettingsPanelSoft;
            if (button.ColorDrivers.Count > 0)
            {
                button.ColorDrivers[0].ColorDrive.Target = bg.Tint;
                button.ColorDrivers[0].SetColors(SettingsPanelSoft);
            }
            return;
        }

        var gradient = ApplyPurpleBlueGradient(bg, selected ? 14f : 12f, selected ? 0.98f : 0.54f, interactionTarget: true);
        if (gradient == null)
            return;

        gradient.Enabled = true;

        SetupGradientButtonDriver(button, 1, gradient.TintTopLeft, SettingsGradientPurple.SetA(selected ? 0.98f : 0.54f));
        SetupGradientButtonDriver(button, 2, gradient.TintTopRight, SettingsGradientMid.SetA(selected ? 0.98f : 0.54f));
        SetupGradientButtonDriver(button, 3, gradient.TintBottomLeft, SettingsGradientMid.SetA(selected ? 0.98f : 0.54f));
        SetupGradientButtonDriver(button, 4, gradient.TintBottomRight, SettingsGradientBlue.SetA(selected ? 0.98f : 0.54f));
    }

    private static void SetupGradientButtonDriver(Button button, int index, Sync<colorX> target, colorX color)
    {
        if (button == null || target == null)
            return;

        while (button.ColorDrivers.Count <= index)
            button.ColorDrivers.Add();

        var driver = button.ColorDrivers[index];
        driver.ColorDrive.Target = target;
        driver.SetColors(color);
    }

    private static void StyleBadgePill(Image image, colorX color)
    {
        if (image == null)
            return;

        image.Sprite.Target = CreateRoundedSprite(image.Slot, image.Slot.World, 12f);
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Tint.Value = color;
        image.InteractionTarget.Value = false;
    }

    private static void StyleSettingsButton(Button button, bool selected)
    {
        if (button == null) return;

        var bg = button.Slot.GetComponent<Image>();
        if (bg != null)
        {
            bg.Sprite.Target = CreateRoundedSprite(button.Slot, button.Slot.World, selected ? 14f : 12f);
            bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
            bg.Tint.Value = selected ? SettingsAccent : SettingsPanelSoft;
        }
        ApplyButtonGradient(button, selected);

        if (button.ColorDrivers.Count > 0 && !selected)
            button.ColorDrivers[0].SetColors(selected ? SettingsAccent : SettingsPanelSoft);

        if (button.Label != null)
        {
            button.Label.Align = Alignment.MiddleCenter;
            button.Label.Color.Value = SettingsText;
            button.Label.Size.Value = 17f;
        }
    }

    private static void UpdateToggleButton(Button button, bool enabled)
    {
        if (button == null) return;

        var bg = button.Slot.GetComponent<Image>();
        var color = enabled ? SettingsAccent : SettingsPanelSoft;
        if (bg != null)
        {
            bg.Tint.Value = color;
            ApplyButtonGradient(button, enabled);
        }
        if (button.ColorDrivers.Count > 0)
            button.ColorDrivers[0].SetColors(color);
        if (button.Label != null)
        {
            button.Label.Content.Value = enabled ? "On" : "Off";
            button.Label.Color.Value = enabled ? SettingsText : SettingsSubtext;
            button.Label.Align = Alignment.MiddleCenter;
        }
    }

    private static void StyleTextFieldSlot(Slot slot, SettingsPanelState state)
    {
        if (slot == null || state == null) return;
        var bg = slot.GetComponent<Image>();
        if (bg != null)
        {
            bg.Tint.Value = new colorX(0.18f, 0.19f, 0.23f, 0.96f);
            bg.Sprite.Target = CreateRoundedSprite(slot, state.Canvas.World, 12f);
            bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        }
        var text = slot.GetComponentInChildren<Text>();
        if (text != null)
        {
            text.Color.Value = SettingsText;
            text.Size.Value = 16f;
            text.Align = Alignment.MiddleLeft;
            text.RectTransform.AddFixedPadding(18f, 0f, 10f, 0f);
        }
    }

    private static void AddCheckbox(UIBuilder ui, SettingsPanelState state, string label, bool initial, Action<bool> changed)
    {
        ui.Style.MinHeight = 54f;
        ui.Style.PreferredHeight = 54f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 17f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 74f;
        ui.Style.PreferredWidth = 74f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var toggle = ui.Button(initial ? "On" : "Off", initial ? SettingsAccentSoft : SettingsPanelSoft);
        StyleSettingsButton(toggle, initial);
        bool lastApplied = initial;
        UpdateToggleButton(toggle, lastApplied);
        toggle.LocalPressed += (_, _) =>
        {
            lastApplied = !lastApplied;
            UpdateToggleButton(toggle, lastApplied);
            changed?.Invoke(lastApplied);
        };
        ui.NestOut();
    }

    private static void AddFloatSlider(UIBuilder ui, SettingsPanelState state, string label, float value, float min, float max, Action<float> changed, bool commitOnReleaseOnly = false, bool wholeNumbers = false)
    {
        value = wholeNumbers ? MathF.Round(value) : value;
        ui.Style.MinHeight = 92f;
        ui.Style.PreferredHeight = 92f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.VerticalLayout(8f, paddingTop: 10f, paddingRight: 14f, paddingBottom: 12f, paddingLeft: 14f, childAlignment: Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);

        ui.Style.MinHeight = 24f;
        ui.Style.PreferredHeight = 24f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        string FormatSliderValue(float v) => wholeNumbers
            ? MathF.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
        var valueLabel = ui.Text($"{label}: {FormatSliderValue(value)}", bestFit: true, alignment: Alignment.MiddleLeft);
        valueLabel.Size.Value = 16f;
        valueLabel.Color.Value = new colorX(0.72f, 0.74f, 0.78f, 1f);

        ui.Style.MinHeight = 36f;
        ui.Style.PreferredHeight = 36f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var slider = ui.Slider<float>(22f, value, min, max, false, out var line, out var fillLine, out var handle);
        line.Tint.Value = SettingsPanelSoft;
        fillLine.Tint.Value = SettingsAccent;
        handle.Tint.Value = SettingsText;
        ApplyPurpleBlueGradient(fillLine, 10f, 0.98f, interactionTarget: false);
        var handleGradient = ApplyPurpleBlueGradient(handle, 18f, 0.98f, interactionTarget: false);
        if (handleGradient != null && slider.ColorDrivers.Count > 0)
            slider.ColorDrivers[0].ColorDrive.Target = handleGradient.TintBottomRight;
        float lastApplied = Math.Clamp(value, min, max);
        float lastCommitted = lastApplied;
        slider.Value.LocalFilter = (candidate, field) =>
        {
            float clamped = Math.Clamp(candidate, min, max);
            if (wholeNumbers)
                clamped = MathF.Round(clamped);
            valueLabel.Content.Value = $"{label}: {FormatSliderValue(clamped)}";
            if (Math.Abs(clamped - lastApplied) > 0.0001f)
            {
                lastApplied = clamped;
                if (!commitOnReleaseOnly)
                    changed?.Invoke(clamped);
            }

            return clamped;
        };
        if (commitOnReleaseOnly)
        {
            slider.IsPressed.OnValueChange += field =>
            {
                if (field.Value)
                    return;
                float valueOnRelease = Math.Clamp(slider.Value.Value, min, max);
                if (wholeNumbers)
                    valueOnRelease = MathF.Round(valueOnRelease);
                if (Math.Abs(valueOnRelease - lastCommitted) <= 0.0001f)
                    return;
                lastCommitted = valueOnRelease;
                changed?.Invoke(valueOnRelease);
            };
        }
        ui.NestOut();
    }

    private static void AddCheckboxWithBadge(UIBuilder ui, SettingsPanelState state, string label, string badge, colorX badgeColor, bool initial, Action<bool> changed)
    {
        ui.Style.MinHeight = 54f;
        ui.Style.PreferredHeight = 54f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        var rowLayout = ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);
        rowLayout.ForceExpandHeight.Value = true;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var text = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        text.Size.Value = 17f;
        text.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 146f;
        ui.Style.PreferredWidth = 146f;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        var badgePill = ui.Image(badgeColor);
        StyleBadgePill(badgePill, badgeColor);
        ui.NestInto(badgePill.RectTransform);
        ui.LayoutTarget = badgePill.Slot;
        var badgeLayout = ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        badgeLayout.ForceExpandWidth.Value = true;
        badgeLayout.ForceExpandHeight.Value = true;
        ui.Style.MinHeight = 30f;
        ui.Style.PreferredHeight = 30f;
        ui.Style.FlexibleWidth = 1f;
        var badgeText = ui.Text(badge, bestFit: true, alignment: Alignment.MiddleCenter);
        badgeText.Size.Value = 14f;
        badgeText.Color.Value = SettingsText;
        ui.NestOut();

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 74f;
        ui.Style.PreferredWidth = 74f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        var toggle = ui.Button(initial ? "On" : "Off", initial ? SettingsAccentSoft : SettingsPanelSoft);
        StyleSettingsButton(toggle, initial);
        bool lastApplied = initial;
        UpdateToggleButton(toggle, lastApplied);
        toggle.LocalPressed += (_, _) =>
        {
            lastApplied = !lastApplied;
            UpdateToggleButton(toggle, lastApplied);
            changed?.Invoke(lastApplied);
        };
        ui.NestOut();
    }

    private static void AddIntField(UIBuilder ui, SettingsPanelState state, string label, int value, int min, int max, Action<int> changed)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var parser = ui.IntegerField(min, max, 1, parseContinuously: false);
        StyleTextFieldSlot(parser.TextEditor?.Slot, state);
        parser.ParsedValue.Value = value;
        parser.TextEditor.LocalEditingFinished += editor =>
        {
            if (int.TryParse(editor.TargetString, out int parsed))
                changed?.Invoke(Math.Clamp(parsed, min, max));
        };
        AddCopyButton(ui, state, (parser.TextEditor?.Text.Target as Text)?.Content);
        AddPasteButton(ui, state, parser.TextEditor, pasted =>
        {
            if (!int.TryParse(pasted, out int parsed))
                return;
            parsed = Math.Clamp(parsed, min, max);
            parser.ParsedValue.Value = parsed;
            parser.TextEditor.TargetString = parsed.ToString(CultureInfo.InvariantCulture);
            changed?.Invoke(parsed);
        });
        ui.NestOut();
    }

    private static void AddStringField(UIBuilder ui, SettingsPanelState state, string label, string value, Action<string> changed)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var field = ui.TextField(value ?? "", undo: false, parseRTF: false);
        StyleTextFieldSlot(field.Slot, state);
        field.Editor.Target.LocalEditingFinished += editor =>
        {
            changed?.Invoke(field.TargetString ?? "");
        };
        AddCopyButton(ui, state, field.Text?.Content);
        AddPasteButton(ui, state, field.Editor.Target, pasted =>
        {
            field.TargetString = pasted ?? "";
            changed?.Invoke(field.TargetString ?? "");
        });
        ui.NestOut();
    }

    private static void AddCopyButton(UIBuilder ui, SettingsPanelState state, IField<string> source)
    {
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleHeight = -1f;
        var copy = ui.Button("\u29C9");
        StyleSettingsButton(copy, false);
        if (copy.Label != null)
        {
            copy.Label.Size.Value = 18f;
            copy.Label.Color.Value = SettingsText;
        }
        var copier = copy.Slot.AttachComponent<ButtonClipboardCopyText>();
        copier.Source.Target = source;
    }

    private static void AddPasteButton(UIBuilder ui, SettingsPanelState state, TextEditor editor, Action<string> pasted)
    {
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleHeight = -1f;
        var paste = ui.Button("📋");
        StyleSettingsButton(paste, false);
        if (paste.Label != null)
        {
            paste.Label.Size.Value = 16f;
            paste.Label.Color.Value = SettingsText;
        }
        paste.LocalPressed += (_, _) =>
        {
            try
            {
                var clipboard = state?.OwnerRoot?.World?.InputInterface?.Clipboard;
                if (clipboard == null || !clipboard.ContainsText)
                    return;
                string text = clipboard.GetText().Result ?? "";
                if (editor != null && !editor.IsDestroyed)
                    editor.TargetString = text;
                pasted?.Invoke(text);
            }
            catch (Exception ex)
            {
                Msg($"[Settings] Paste failed: {ex.Message}");
            }
        };
    }

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

    private static void AddButtonRow(UIBuilder ui, SettingsPanelState state, string label, Action pressed, bool selected = false, string buttonLabel = null)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 116f;
        ui.Style.PreferredWidth = 116f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var btn = ui.Button(buttonLabel ?? label, selected ? SettingsAccent : SettingsPanelSoft);
        StyleSettingsButton(btn, selected);
        btn.LocalPressed += (_, _) =>
        {
            pressed?.Invoke();
            RebuildSettingsContent(state, null);
        };
        ui.NestOut();
    }

    private static void AddLinkButtonRow(UIBuilder ui, SettingsPanelState state, string label, string url, string buttonLabel = null)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, state.Canvas.World, 13f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var rowLabel = ui.Text(label, bestFit: true, alignment: Alignment.MiddleLeft);
        rowLabel.Size.Value = 16f;
        rowLabel.Color.Value = SettingsText;

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 116f;
        ui.Style.PreferredWidth = 116f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var btn = ui.Button(buttonLabel ?? label, SettingsPanelSoft);
        StyleSettingsButton(btn, false);

        var link = btn.Slot.AttachComponent<Hyperlink>();
        link.URL.Value = new Uri(url);
        link.OpenOnce.Value = false;
        link.Reason.Value = "DesktopBuddy";
        ui.NestOut();
    }

    private static int EstimateOptionColumns(SettingsPanelState state, int optionCount, float cellWidth, int? preferredColumns)
    {
        float available = Math.Max(cellWidth, (state?.ModalWidth ?? 820) - 300f);
        int maxColumns = (int)Math.Floor((available + 8f) / (cellWidth + 8f));
        int columns = preferredColumns.HasValue && preferredColumns.Value <= maxColumns ? preferredColumns.Value : maxColumns;
        return Math.Clamp(columns, 1, Math.Max(1, optionCount));
    }

    private static void ResizeSettingsPanel(DesktopSession session, int width, int height, float canvasScale, float curvature)
    {
        var state = session?.SettingsPanel;
        if (state == null) return;

        (state.ModalWidth, state.ModalHeight) = GetSettingsModalSize(width, height);
        state.RenderWidth = state.ModalWidth;
        state.RenderHeight = state.ModalHeight;
        state.CanvasScale = canvasScale;

        if (state.RenderTexture != null && !state.RenderTexture.IsDestroyed)
            state.RenderTexture.Size.Value = new int2(state.RenderWidth, state.RenderHeight);
        if (state.Camera != null && !state.Camera.IsDestroyed)
            state.Camera.OrthographicSize.Value = state.RenderHeight * 0.5f;
        if (state.Canvas != null && !state.Canvas.IsDestroyed)
            state.Canvas.Size.Value = new float2(state.RenderWidth, state.RenderHeight);
        if (state.Mesh != null && !state.Mesh.IsDestroyed)
        {
            state.Mesh.Size.Value = new float2(state.ModalWidth, state.ModalHeight);
            state.Mesh.Curvature.Value = curvature;
            state.Mesh.Slot.LocalScale = float3.One * canvasScale;
            state.Mesh.Slot.LocalPosition = new float3(0f, 0f, SettingsPanelZOffset);
        }
        UpdateSettingsBlurMask(state);
        SetSettingsModalRect(state);
        UpdateViewerCullingTrigger(session);
        UpdateCullingPreview(session, state);
    }

    private static void SyncLiveCullingStateFromConfig(SettingsPanelState state)
    {
        if (state == null) return;
        state.ViewerCullingPreviewEnabled = Config?.GetValue(ViewerCullingPreview) ?? false;
        state.ViewerCullingMode = NormalizeViewerCullingMode(Config?.GetValue(ViewerCullingMode));
        state.ViewerFrustumAngle = NormalizeViewerFrustumAngle(Config?.GetValue(ViewerFrustumWidth) ?? 120f);
        float range = Math.Clamp(Config?.GetValue(ViewerDistance) ?? Config?.GetValue(ViewerFrustumDepth) ?? 3f, 1f, 10f);
        state.ViewerFrustumDepth = range;
        state.ViewerDistance = range;
    }

    private static void SetSettingsModalRect(SettingsPanelState state)
    {
        if (state?.ModalRect == null || state.ModalRect.IsDestroyed)
            return;

        state.ModalRect.SetFixedRect(
            new Rect(state.ModalWidth * -0.5f, state.ModalHeight * -0.5f, state.ModalWidth, state.ModalHeight),
            new float2(0.5f, 0.5f));
    }

    private static void UpdateSettingsBlurMask(SettingsPanelState state)
    {
        if (state?.BackgroundBlur == null || state.BackgroundBlur.IsDestroyed ||
            state.BackgroundBlurMask == null || state.BackgroundBlurMask.IsDestroyed ||
            state.OwnerRoot == null || state.OwnerRoot.IsDestroyed)
            return;

        int modalW = Math.Max(1, state.ModalWidth);
        int modalH = Math.Max(1, state.ModalHeight);
        if (state.BackgroundBlurMaskWidth == modalW && state.BackgroundBlurMaskHeight == modalH)
            return;

        state.BackgroundBlurMaskWidth = modalW;
        state.BackgroundBlurMaskHeight = modalH;

        var tex = state.BackgroundBlurMask;
        var blur = state.BackgroundBlur;
        var engine = state.OwnerRoot.Engine;
        byte[] data = CreateRoundedMaskPixels(modalW, modalH, 28f, out int texW, out int texH);

        Task.Run(async () =>
        {
            try
            {
                var bitmap = new Bitmap2D(data, texW, texH, Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.Linear, false);
                var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                if (uri == null)
                    return;

                tex.World.RunInUpdates(0, () =>
                {
                    if (tex.IsDestroyed || blur.IsDestroyed)
                        return;

                    tex.URL.Value = uri;
                    blur.SpreadMagnitudeTexture.Target = tex;
                    blur.SpreadTextureScale.Value = float2.One;
                    blur.SpreadTextureOffset.Value = float2.Zero;
                });
            }
            catch (Exception ex)
            {
                Msg($"[Settings] Blur mask generation failed: {ex.Message}");
            }
        });
    }

    private static byte[] CreateRoundedMaskPixels(int modalW, int modalH, float radiusPixels, out int texW, out int texH)
    {
        float aspect = modalW / (float)Math.Max(1, modalH);
        if (aspect >= 1f)
        {
            texW = 512;
            texH = Math.Clamp((int)MathF.Round(texW / aspect), 128, 512);
        }
        else
        {
            texH = 512;
            texW = Math.Clamp((int)MathF.Round(texH * aspect), 128, 512);
        }

        byte[] data = new byte[texW * texH * 4];
        float radius = Math.Clamp(radiusPixels, 1f, Math.Min(modalW, modalH) * 0.5f);
        const float edge = 2f;

        for (int y = 0; y < texH; y++)
        {
            float py = (y + 0.5f) / texH * modalH;
            for (int x = 0; x < texW; x++)
            {
                float px = (x + 0.5f) / texW * modalW;
                float cx = Math.Clamp(px, radius, modalW - radius);
                float cy = Math.Clamp(py, radius, modalH - radius);
                float dist = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                float mask = 1f - Math.Clamp((dist - (radius - edge)) / (edge * 2f), 0f, 1f);
                byte v = (byte)Math.Clamp((int)MathF.Round(mask * 255f), 0, 255);
                int i = (y * texW + x) * 4;
                data[i] = v;
                data[i + 1] = v;
                data[i + 2] = v;
                data[i + 3] = 255;
            }
        }

        return data;
    }

    private static byte[] CreateCenteredRoundedMaskPixels(int canvasW, int canvasH, int pillW, int pillH, float radiusPixels, out int texW, out int texH)
    {
        float aspect = canvasW / (float)Math.Max(1, canvasH);
        if (aspect >= 1f)
        {
            texW = 512;
            texH = Math.Clamp((int)MathF.Round(texW / aspect), 64, 512);
        }
        else
        {
            texH = 512;
            texW = Math.Clamp((int)MathF.Round(texH * aspect), 64, 512);
        }

        byte[] data = new byte[texW * texH * 4];
        float pillLeft = (canvasW - pillW) * 0.5f;
        float pillRight = pillLeft + pillW;
        float pillTop = (canvasH - pillH) * 0.5f;
        float pillBottom = pillTop + pillH;
        float radius = Math.Clamp(radiusPixels, 1f, Math.Min(pillW, pillH) * 0.5f);
        const float edge = 2f;

        for (int y = 0; y < texH; y++)
        {
            float py = (y + 0.5f) / texH * canvasH;
            for (int x = 0; x < texW; x++)
            {
                float px = (x + 0.5f) / texW * canvasW;
                float cx = Math.Clamp(px, pillLeft + radius, pillRight - radius);
                float cy = Math.Clamp(py, pillTop + radius, pillBottom - radius);
                float dist = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                float mask = 1f - Math.Clamp((dist - (radius - edge)) / (edge * 2f), 0f, 1f);

                if (px < pillLeft || px > pillRight || py < pillTop || py > pillBottom)
                    mask = 0f;

                byte v = (byte)Math.Clamp((int)MathF.Round(mask * 255f), 0, 255);
                int i = (y * texW + x) * 4;
                data[i] = v;
                data[i + 1] = v;
                data[i + 2] = v;
                data[i + 3] = 255;
            }
        }

        return data;
    }

    private static (int Width, int Height) GetSettingsModalSize(int panelWidth, int panelHeight)
    {
        int maxW = Math.Max(360, Math.Min(1120, panelWidth - 120));
        int maxH = Math.Max(300, Math.Min(760, panelHeight - 120));
        int minW = Math.Min(720, maxW);
        int minH = Math.Min(480, maxH);
        int width = (int)Math.Clamp(panelWidth * 0.62f, minW, maxW);
        int height = (int)Math.Clamp(panelHeight * 0.68f, minH, maxH);
        return (width, height);
    }

    private static void ApplyPanelCurve(DesktopSession session, float curvature)
    {
        if (session == null) return;
        if (session.Root != null && !session.Root.IsDestroyed)
        {
            foreach (var mesh in session.Root.GetComponentsInChildren<CurvedPlaneMesh>())
            {
                if (mesh != null && !mesh.IsDestroyed)
                    mesh.Curvature.Value = curvature;
            }
        }
    }

    private static void UpdateCullingPreview(DesktopSession session, SettingsPanelState state = null)
    {
        if (session?.Root == null || session.Root.IsDestroyed)
            return;

        state ??= session.SettingsPanel;

        if (session.CullingPreviewSlot != null && !session.CullingPreviewSlot.IsDestroyed)
        {
            session.CullingPreviewSlot.Destroy();
            session.CullingPreviewSlot = null;
        }

        if (!(state?.ViewerCullingPreviewEnabled ?? (Config?.GetValue(ViewerCullingPreview) ?? false)))
            return;

        string mode = NormalizeViewerCullingMode(state?.ViewerCullingMode ?? Config.GetValue(ViewerCullingMode));
        var guide = session.Root.AddSlot("ViewerCullingPreviewGuide");
        guide.LocalPosition = float3.Zero;
        guide.LocalRotation = floatQ.Identity;

        if (mode == "distance")
        {
            float distance = Math.Clamp(state?.ViewerDistance ?? Config.GetValue(ViewerDistance), 1f, 10f);
            AddPreviewSphere(guide, session, distance, new colorX(0.25f, 0.55f, 1f, 0.16f));
        }
        else
        {
            float depth = Math.Clamp(state?.ViewerFrustumDepth ?? Config.GetValue(ViewerFrustumDepth), 1f, 10f);
            float angle = NormalizeViewerFrustumAngle(state?.ViewerFrustumAngle ?? Config.GetValue(ViewerFrustumWidth));
            AddPreviewFrustum(guide, session, angle, depth, new colorX(0.25f, 1f, 0.7f, 0.16f));
        }

        session.CullingPreviewSlot = guide;
    }

    private static float NormalizeViewerFrustumAngle(float value)
    {
        if (value < 5f)
            return 120f;
        return Math.Clamp(value, 30f, 170f);
    }

    private static UnlitMaterial CreatePreviewMaterial(Slot slot, colorX tint)
    {
        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = tint;
        material.BlendMode.Value = BlendMode.Alpha;
        material.ZWrite.Value = ZWrite.Off;
        material.Sidedness.Value = Sidedness.Double;
        material.RenderQueue.Value = 3004;
        return material;
    }

    private static float GetCullingPreviewOriginZ(DesktopSession session)
    {
        if (session == null)
            return 0.001f;

        var mesh = session.PanelMesh;
        float scale = session.PanelCanvasScale;
        if ((mesh == null || mesh.IsDestroyed) && session.SettingsPanel != null)
        {
            mesh = session.SettingsPanel.Mesh;
            scale = session.SettingsPanel.CanvasScale;
        }

        if (scale <= 0f)
            scale = 0.0005f;

        return GetCurvedPanelDepth(mesh, scale) + 0.001f;
    }

    private static void AddPreviewSphere(Slot parent, DesktopSession session, float radius, colorX tint)
    {
        var slot = parent.AddSlot("DistanceSphere");
        slot.LocalPosition = new float3(0f, 0f, GetCullingPreviewOriginZ(session));
        slot.LocalRotation = floatQ.Identity;
        var renderer = slot.AttachComponent<MeshRenderer>();
        var sphere = slot.AttachComponent<SphereMesh>();
        sphere.Radius.Value = radius;
        sphere.Segments.Value = 48;
        sphere.Rings.Value = 24;
        renderer.Mesh.Target = sphere;
        renderer.Materials.Add(CreatePreviewMaterial(slot, tint));
    }

    private static void AddPreviewFrustum(Slot parent, DesktopSession session, float angleDegrees, float depth, colorX tint)
    {
        if (session == null || session.SettingsPanel == null)
            return;

        int panelPixelsW = session.LastKnownW;
        int panelPixelsH = session.LastKnownH;
        if ((panelPixelsW <= 0 || panelPixelsH <= 0) && session.Canvas != null && !session.Canvas.IsDestroyed)
        {
            panelPixelsW = MathX.RoundToInt(session.Canvas.Size.Value.x);
            panelPixelsH = MathX.RoundToInt(session.Canvas.Size.Value.y);
        }
        if (panelPixelsW <= 0 || panelPixelsH <= 0 || session.SettingsPanel.CanvasScale <= 0f)
            return;

        float panelW = panelPixelsW * session.SettingsPanel.CanvasScale;
        float panelH = panelPixelsH * session.SettingsPanel.CanvasScale;
        float nearZ = GetCullingPreviewOriginZ(session);
        float farZ = nearZ - depth;
        float nearHalfW = panelW * 0.5f;
        float nearHalfH = panelH * 0.5f;
        float farHalfW = nearHalfW + (float)Math.Tan(angleDegrees * Math.PI / 360.0) * depth;
        float verticalAngleDegrees = angleDegrees * 0.5f;
        float farHalfH = nearHalfH + (float)Math.Tan(verticalAngleDegrees * Math.PI / 360.0) * depth;

        var near = new[]
        {
            new float3(-nearHalfW, -nearHalfH, nearZ),
            new float3( nearHalfW, -nearHalfH, nearZ),
            new float3( nearHalfW,  nearHalfH, nearZ),
            new float3(-nearHalfW,  nearHalfH, nearZ),
        };
        var far = new[]
        {
            new float3(-farHalfW, -farHalfH, farZ),
            new float3( farHalfW, -farHalfH, farZ),
            new float3( farHalfW,  farHalfH, farZ),
            new float3(-farHalfW,  farHalfH, farZ),
        };

        AddPreviewQuad(parent, "NearPlane", near[0], near[1], near[2], near[3], tint);
        AddPreviewQuad(parent, "FarPlane", far[1], far[0], far[3], far[2], tint);
        AddPreviewQuad(parent, "LeftPlane", far[0], near[0], near[3], far[3], tint);
        AddPreviewQuad(parent, "RightPlane", near[1], far[1], far[2], near[2], tint);
        AddPreviewQuad(parent, "BottomPlane", far[0], far[1], near[1], near[0], tint);
        AddPreviewQuad(parent, "TopPlane", near[3], near[2], far[2], far[3], tint);
    }

    private static void AddPreviewQuad(Slot parent, string name, float3 a, float3 b, float3 c, float3 d, colorX tint)
    {
        AddPreviewTriangle(parent, name + " A", a, b, c, tint);
        AddPreviewTriangle(parent, name + " B", a, c, d, tint);
    }

    private static void AddPreviewTriangle(Slot parent, string name, float3 a, float3 b, float3 c, colorX tint)
    {
        var slot = parent.AddSlot(name);
        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<TriangleMesh>();
        mesh.Vertex0.Position.Value = a;
        mesh.Vertex1.Position.Value = b;
        mesh.Vertex2.Position.Value = c;
        mesh.AutoNormals.Value = true;
        mesh.AutoTangents.Value = true;
        mesh.DualSided.Value = true;
        renderer.Mesh.Target = mesh;
        renderer.Materials.Add(CreatePreviewMaterial(slot, tint));
    }

}

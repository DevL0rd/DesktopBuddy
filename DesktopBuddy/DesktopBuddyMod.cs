using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;
using SkyFrost.Base;
using Key = Renderite.Shared.Key;

namespace DesktopBuddy;

public class DesktopBuddyMod : ResoniteMod
{
    public override string Name => "DesktopBuddy";
    public override string Author => "DevL0rd";
    public override string Version => "1.0.0";
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

    internal static ModConfiguration? Config;

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> FrameRate =
        new("frameRate", "Target capture frame rate", () => 30);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ImmediateGC =
        new("immediate_gc", "Force garbage collection on dispose", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> SpatialAudioEnabled =
        new("spatialAudio", "Enable spatial in-game audio (redirects window audio to VB-Cable). When off, use Windows volume slider instead.", () => false);



    internal static readonly List<DesktopSession> ActiveSessions = new();
    private static int _nextStreamId;

    internal static readonly HashSet<RefID> DesktopCanvasIds = new();

    private static readonly Dictionary<IntPtr, SharedStream> _sharedStreams = new();

    private class SharedStream
    {
        public int StreamId;
        public FfmpegEncoder Encoder;
        public AudioCapture Audio;
        public Uri StreamUrl;
        public int RefCount;
    }

    internal static MjpegServer? StreamServer;
    internal static VirtualCamera VCam;
    internal static VirtualMic VMic;
    private const int STREAM_PORT = 48080;
    internal static string? TunnelUrl;
    private static Process _tunnelProcess;
    private static string _cfPath;
    private static volatile bool _tunnelRestarting;
    internal static readonly PerfTimer Perf = new();

    // Renderer-side capture side-channel
    internal static CaptureSessionChannel? CaptureChannel;
    private static bool _captureChannelOpened;

    // Track our DesktopTextureProvider instances so the patch can bypass the Userspace world check
    internal static readonly System.Collections.Generic.HashSet<DesktopTextureProvider> OurProviders = new();

    private static Thread _windowPollerThread;
    private static volatile bool _windowPollerRunning;
    private static readonly ConcurrentQueue<WindowEvent> _windowEvents = new();

    private struct WindowEvent
    {
        public DesktopSession Session;
        public IntPtr ChildHwnd;
        public string Title;
        public WindowEventType EventType;
    }
    private enum WindowEventType { NewChild, ChildClosed, TitleChanged }

    private static string _latestVersion;
    private static bool _updateShown;


    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Config!.Save(true);

        Log.StartSession();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Msg($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}):\n{e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Msg($"UNOBSERVED TASK EXCEPTION:\n{e.Exception}");
        };

        InstallNativeCrashHandler();

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();

        AudioCapture.LogHandler = Msg;

        try
        {
            StreamServer = new MjpegServer(STREAM_PORT);
            StreamServer.Start();
            Msg($"Stream server started on port {STREAM_PORT}");
        }
        catch (Exception ex)
        {
            Msg($"Stream server failed to start: {ex.Message}");
            StreamServer = null;
        }

        if (StreamServer != null)
        {
            System.Threading.Tasks.Task.Run(() => StartTunnel());
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            var resetPids = new HashSet<uint>();
            foreach (var session in ActiveSessions)
            {
                if (session.OwnsAudioRedirect && session.ProcessId != 0 && resetPids.Add(session.ProcessId))
                    AudioRouter.ResetProcessToDefault(session.ProcessId);
            }
            KillTunnel();
        };

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (SoftCamSetup.IsRegistered())
                {
                    VCam = new VirtualCamera();
                    VCam.StartIdle();
                }
                else
                {
                    Msg("[VirtualCamera] DirectShow filter not registered, virtual camera unavailable");
                }
            }
            catch (Exception ex) { Msg($"[VirtualCamera] Setup error: {ex.Message}"); }

            try
            {
                if (!VBCableSetup.IsInstalled())
                    Msg("[VirtualMic] VB-Cable not installed, virtual mic unavailable");
            }
            catch (Exception ex) { Msg($"[VirtualMic] Setup error: {ex.Message}"); }
        });

        _windowPollerRunning = true;
        _windowPollerThread = new Thread(WindowPollerLoop)
        { Name = "DesktopBuddy:WindowPoller", IsBackground = true };
        _windowPollerThread.Start();

        Msg("DesktopBuddy initialized!");

        // Open the renderer capture side-channel after a delay so RenderSystem is initialized
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Give the render system a moment to start
                Thread.Sleep(5000);
                OpenCaptureChannel();
            }
            catch (Exception ex)
            {
                Msg($"[CaptureChannel] Failed to open: {ex.Message}");
            }
        });
    }

    private static void OpenCaptureChannel()
    {
        if (_captureChannelOpened) return;

        try
        {
            var engine = FrooxEngine.Engine.Current;
            if (engine == null) { Msg("[CaptureChannel] Engine.Current is null"); return; }

            var renderSystem = engine.RenderSystem;
            if (renderSystem == null) { Msg("[CaptureChannel] RenderSystem is null"); return; }

            // Access private _messagingHost field via reflection
            var hostField = renderSystem.GetType().GetField("_messagingHost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (hostField == null) { Msg("[CaptureChannel] _messagingHost field not found"); return; }

            var host = hostField.GetValue(renderSystem);
            if (host == null) { Msg("[CaptureChannel] _messagingHost is null (renderer not started?)"); return; }

            var queueNameProp = host.GetType().GetProperty("QueueName");
            if (queueNameProp == null) { Msg("[CaptureChannel] QueueName property not found"); return; }

            var queueName = (string)queueNameProp.GetValue(host);
            if (string.IsNullOrEmpty(queueName)) { Msg("[CaptureChannel] QueueName is empty"); return; }

            CaptureChannel = new CaptureSessionChannel();
            CaptureChannel.Open(queueName);
            _captureChannelOpened = true;
            Msg($"[CaptureChannel] Opened successfully (queueName={queueName})");
        }
        catch (Exception ex)
        {
            Msg($"[CaptureChannel] Error: {ex}");
        }
    }

    private static void CheckForUpdate()
    {
        try
        {
            var buildSha = BuildInfo.GitSha;
            Msg($"[Update] Current build: {buildSha}");

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "DesktopBuddy");
            var json = http.GetStringAsync("https://api.github.com/repos/DevL0rd/DesktopBuddy/releases/latest").Result;
            var match = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (match.Success)
            {
                var tag = match.Groups[1].Value;
                var remoteSha = tag.StartsWith("build-") ? tag.Substring(6) : tag;
                Msg($"[Update] Latest release: {tag} (sha: {remoteSha})");
                if (buildSha != "unknown" && remoteSha != buildSha)
                    _latestVersion = tag;
            }
        }
        catch (Exception ex)
        {
            Msg($"[Update] Check failed: {ex.Message}");
        }
    }

    private static void ShowUpdatePopup(Slot root, float w, float canvasScale)
    {
        Msg($"[Update] Showing update popup: {_latestVersion}");

        var updateSlot = root.AddSlot("UpdateNotice");
        updateSlot.LocalPosition = new float3(0f, 0f, -0.002f);
        updateSlot.LocalScale = float3.One * canvasScale;

        var updateCanvas = updateSlot.AttachComponent<Canvas>();
        float popupW = Math.Min(w * 0.6f, 400f);
        updateCanvas.Size.Value = new float2(popupW, 160f);
        var updateUi = new UIBuilder(updateCanvas);

        var bg = updateUi.Image(new colorX(0.12f, 0.12f, 0.15f, 0.95f));
        updateUi.NestInto(bg.RectTransform);
        updateUi.VerticalLayout(8f, childAlignment: Alignment.MiddleCenter);
        updateUi.Style.FlexibleWidth = 1f;

        updateUi.Style.MinHeight = 32f;
        var msg = updateUi.Text("Update available!", bestFit: false, alignment: Alignment.MiddleCenter);
        msg.Size.Value = 22f;
        msg.Color.Value = new colorX(0.95f, 0.85f, 0.3f, 1f);

        updateUi.Style.MinHeight = 36f;
        var dlBtn = updateUi.Button("Download");
        var dlTxt = dlBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (dlTxt != null) { dlTxt.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f); dlTxt.Size.Value = 18f; }
        if (dlBtn.ColorDrivers.Count > 0)
        {
            var cd = dlBtn.ColorDrivers[0];
            cd.NormalColor.Value = new colorX(0.2f, 0.4f, 0.6f, 1f);
            cd.HighlightColor.Value = new colorX(0.25f, 0.5f, 0.75f, 1f);
            cd.PressColor.Value = new colorX(0.15f, 0.3f, 0.45f, 1f);
        }
        dlBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Update] Opening releases page");
            try { Process.Start(new ProcessStartInfo("https://github.com/DevL0rd/DesktopBuddy/releases") { UseShellExecute = true }); }
            catch (Exception ex) { Msg($"[Update] Failed: {ex.Message}"); }
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        };

        updateUi.Style.MinHeight = 30f;
        var dismissBtn = updateUi.Button("Dismiss");
        var dismissTxt = dismissBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (dismissTxt != null) { dismissTxt.Color.Value = new colorX(0.7f, 0.7f, 0.7f, 1f); dismissTxt.Size.Value = 14f; }
        if (dismissBtn.ColorDrivers.Count > 0)
        {
            var cd = dismissBtn.ColorDrivers[0];
            cd.NormalColor.Value = new colorX(0.2f, 0.2f, 0.25f, 1f);
            cd.HighlightColor.Value = new colorX(0.3f, 0.3f, 0.35f, 1f);
            cd.PressColor.Value = new colorX(0.15f, 0.15f, 0.18f, 1f);
        }
        dismissBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        };

        root.World.RunInUpdates(15 * 60, () =>
        {
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        });
    }

    internal static void SpawnStreaming(World world, IntPtr hwnd, string title, IntPtr monitorHandle = default, int monitorIndex = -1)
    {
        try
        {
            Msg($"[SpawnStreaming] Starting for '{title}' hwnd={hwnd} monitorIndex={monitorIndex}");
            var localUser = world.LocalUser;
            if (localUser == null) { Msg("[SpawnStreaming] LocalUser is null, aborting"); return; }
            var userRoot = localUser.Root;
            if (userRoot == null) { Msg("[SpawnStreaming] UserRoot is null, aborting"); return; }

            var root = (localUser.Root.Slot.Parent ?? world.RootSlot).AddSlot("Desktop Buddy");

            var headPos = userRoot.HeadPosition;
            var headRot = userRoot.HeadRotation;
            var forward = headRot * float3.Forward;
            root.GlobalPosition = headPos + forward * 0.8f;
            root.GlobalRotation = floatQ.LookRotation(forward, float3.Up);
            root.Tag = "Desktop Buddy";
            var destroyer = root.AttachComponent<DestroyOnUserLeave>();

            destroyer.TargetUser.Target = localUser;

            Msg($"[SpawnStreaming] Slot created at pos={root.GlobalPosition}");

            StartStreaming(root, hwnd, title, monitorHandle: monitorHandle, monitorIndex: monitorIndex);
        }
        catch (Exception ex)
        {
            Msg($"ERROR in SpawnStreaming: {ex}");
        }
    }

    private static void StartStreaming(Slot root, IntPtr hwnd, string title, bool isChild = false, IntPtr monitorHandle = default, DesktopSession parentSession = null, int monitorIndex = -1)
    {
        Msg($"[StartStreaming] Window: {title} (hwnd={hwnd} monitorIndex={monitorIndex})");

        WindowInput.RestoreIfMinimized(hwnd);

        var streamer = new DesktopStreamer(hwnd, monitorHandle);
        var world = root.World;

        System.Threading.Tasks.Task.Run(() =>
        {
            if (!streamer.TryInitialCapture())
            {
                Msg($"[StartStreaming] Failed initial capture for: {title}");
                streamer.Dispose();
                return;
            }
            world.RunInUpdates(0, () => FinishStartStreaming(root, hwnd, title, isChild, streamer, parentSession, monitorIndex));
        });
    }

    private static void FinishStartStreaming(Slot root, IntPtr hwnd, string title, bool isChild, DesktopStreamer streamer, DesktopSession parentSession = null, int monitorIndex = -1)
    {
        if (root == null || root.IsDestroyed)
        {
            Msg($"[StartStreaming] Root slot destroyed before capture completed: {title}");
            streamer.Dispose();
            return;
        }

        int fps = Config!.GetValue(FrameRate);
        int w = streamer.Width;
        int h = streamer.Height;
        Grabbable grabbable = null;

        Msg($"[StartStreaming] Window size: {w}x{h}, target {fps}fps");

        float canvasScale = 0.0005f;
        float worldHalfH = h / 2f * canvasScale;
        float worldHalfW = w / 2f * canvasScale;
        var collider = root.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(w * canvasScale, h * canvasScale, 0.001f);
        collider.Offset.Value = float3.Zero;
        Msg("[StartStreaming] Collider added to root");

        var displaySlot = root.AddLocalSlot("Display", false);
        Msg("[StartStreaming] Display slot (local) created");

        var texSlot = displaySlot.AddSlot("Texture");
        var procTex = texSlot.AttachComponent<DesktopTextureProvider>();
        OurProviders.Add(procTex);
        int captureSlot = -1;
        if (hwnd != IntPtr.Zero && CaptureChannel != null && CaptureChannel.IsOpen)
        {
            // Window capture — use magic index + renderer UWC path
            captureSlot = CaptureChannel.RegisterSession(hwnd, streamer.MonitorHandle);
            if (captureSlot < 0)
            {
                Msg($"[StartStreaming] No free capture slots for: {title}");
                streamer.Dispose();
                root.Destroy();
                return;
            }
            int magicIdx = CaptureSessionProtocol.MagicIndexBase + captureSlot;
            procTex.DisplayIndex.Value = magicIdx;
            Msg($"[StartStreaming] Window capture: slot {captureSlot}, DisplayIndex={magicIdx}");
            // NOTE: The renderer may not have the UWC source ready yet for this magic index.
            // The UpdateLoop handles retries by re-sending desktopTex.Update() periodically.
        }
        else if (hwnd == IntPtr.Zero && monitorIndex >= 0)
        {
            // Monitor capture — use real display index, Renderite's DuplicableDisplay handles it
            procTex.DisplayIndex.Value = monitorIndex;
            Msg($"[StartStreaming] Monitor capture: DisplayIndex={monitorIndex}");
        }
        else
        {
            Msg($"[StartStreaming] WARNING: Cannot set up texture (hwnd={hwnd}, monitorIndex={monitorIndex}, channel={(CaptureChannel?.IsOpen ?? false)})");
        }
        Msg("[StartStreaming] Texture component created");

        var ui = new UIBuilder(displaySlot, w, h, canvasScale);

        var displayBg = ui.Image(new colorX(0f, 0f, 0f, 1f));
        ui.NestInto(displayBg.RectTransform);

        var rawImage = ui.RawImage(procTex);
        // Flip Y to match Renderite's texture coordinate convention (same as native Userspace display)
        rawImage.UVRect.Value = new Rect(float2.Zero, new float2(1f, -1f));
        Msg("[StartStreaming] Canvas + RawImage created");

        var mat = displaySlot.AttachComponent<UI_UnlitMaterial>();
        mat.BlendMode.Value = BlendMode.Alpha;
        mat.ZWrite.Value = ZWrite.On;
        mat.OffsetUnits.Value = 100f;
        rawImage.Material.Target = mat;

        var btn = rawImage.Slot.AttachComponent<Button>();
        btn.PassThroughHorizontalMovement.Value = false;
        btn.PassThroughVerticalMovement.Value = false;
        Msg("[StartStreaming] Button attached");

        WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
        Msg($"[StartStreaming] Process ID: {processId}");

        var session = new DesktopSession
        {
            Streamer = streamer,
            Texture = procTex,
            TextureImage = rawImage,
            Canvas = ui.Canvas,
            Root = root,
            TargetInterval = 1.0 / fps,
            Hwnd = hwnd,
            ProcessId = processId,
            Collider = collider,
            CaptureSlot = captureSlot,
            LastKnownW = w,
            LastKnownH = h,
        };
        ActiveSessions.Add(session);
        if (parentSession != null)
        {
            session.ParentSession = parentSession;
            parentSession.ChildSessions.Add(session);
            Msg($"[ChildWindow] Linked to parent, parent now tracking {parentSession.ChildSessions.Count} children");
        }
        DesktopCanvasIds.Add(ui.Canvas.ReferenceID);
        Msg($"[StartStreaming] Registered canvas {ui.Canvas.ReferenceID} for locomotion suppression");

        if (!isChild && processId != 0)
        {
            foreach (var existing in WindowEnumerator.GetProcessWindows(processId))
            {
                if (existing.Handle != hwnd)
                    session.TrackedChildHwnds.Add(existing.Handle);
            }
            if (session.TrackedChildHwnds.Count > 0)
                Msg($"[StartStreaming] Pre-existing child windows ignored: {session.TrackedChildHwnds.Count}");
        }


        bool IsActiveSource(Component source)
        {
            if (session.LastActiveSource == null || session.LastActiveSource.IsDestroyed)
                return true;
            return source == session.LastActiveSource;
        }

        void ClaimSource(Component source)
        {
            if (source != session.LastActiveSource)
            {
                session.LastActiveSource = source;
            }
        }

        var _handlerField = typeof(InteractionLaser)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);

        InteractionHandler FindHandler(Component source)
        {
            if (source == null) return null;
            var laser = source.Slot?.GetComponent<InteractionLaser>();
            if (laser != null && _handlerField != null)
            {
                var handlerRef = _handlerField.GetValue(laser) as SyncRef<InteractionHandler>;
                return handlerRef?.Target;
            }
            return source.Slot?.GetComponentInParents<InteractionHandler>();
        }

        uint GetTouchId(Component source)
        {
            var handler = FindHandler(source);
            if (handler != null && handler.Side.Value == Renderite.Shared.Chirality.Right)
                return 1;
            return 0;
        }

        btn.LocalPressed += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            ClaimSource(data.source);
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.FocusWindow(hwnd);
            WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source));
        };

        btn.LocalPressing += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source));
        };

        btn.LocalReleased += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source));
        };

        btn.LocalHoverStay += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            float hu = data.normalizedPressPoint.x;
            float hv = 1f - data.normalizedPressPoint.y;

            if (IsActiveSource(data.source))
            {
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height);
            }

            var mouse = root.World.InputInterface.Mouse;
            if (mouse != null)
            {
                float scrollY = mouse.ScrollWheelDelta.Value.y;
                if (scrollY != 0)
                {
                    ClaimSource(data.source);
                    WindowInput.FocusWindow(hwnd);
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta);
                }
            }

            try
            {
                var handler = FindHandler(data.source);
                var controller = handler != null
                    ? root.World.InputInterface.GetControllerNode(handler.Side.Value)
                    : null;
                if (controller != null)
                {
                    float axisY = controller.Axis.Value.y;
                    if (Math.Abs(axisY) > 0.15f)
                    {
                        double tick = root.World.Time.WorldTime;
                        bool sameDir = session.LastScrollSign == 0 || Math.Sign(axisY) == session.LastScrollSign;
                        if (tick != session.LastScrollTick && sameDir)
                        {
                            session.LastScrollTick = tick;
                            session.LastScrollSign = Math.Sign(axisY);
                            ClaimSource(data.source);
                            WindowInput.FocusWindow(hwnd);
                            int wheelDelta = (int)(axisY * 120f);
                            WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta);
                        }
                    }
                    else
                    {
                        session.LastScrollSign = 0;
                    }
                }
            }
            catch { }
        };

        float barH = 64f;
        float barMarginTop = 10f * canvasScale;
        float barPad = 8f;
        float barGap = 8f;
        float avatarW = 48f;
        float toggleW = 36f;

        var barSlot = root.AddSlot("TopBar");
        barSlot.LocalScale = float3.One * canvasScale;

        var barCanvas = barSlot.AttachComponent<Canvas>();

        var barMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barMat.BlendMode.Value = BlendMode.Alpha;
        barMat.ZWrite.Value = ZWrite.On;
        barMat.OffsetUnits.Value = 100f;

        var barUi = new UIBuilder(barCanvas);
        var barBg = barUi.Image(new colorX(0.1f, 0.1f, 0.12f, 1f));
        barBg.Material.Target = barMat;
        var roundedSprite = barSlot.AttachComponent<SpriteProvider>();
        roundedSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        roundedSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        roundedSprite.FixedSize.Value = 16f;
        barBg.Sprite.Target = roundedSprite;
        barBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBg.Tint.Value = new colorX(0.1f, 0.1f, 0.12f, 1f);

        var barMask = barBg.Slot.AttachComponent<Mask>();
        barMask.ShowMaskGraphic.Value = true;
        barUi.NestInto(barBg.RectTransform);
        var barLayout = barUi.HorizontalLayout(8f, padding: 8f, childAlignment: Alignment.MiddleLeft);
        barLayout.ForceExpandWidth.Value = false;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = 1f;

        var localUser = root.World.LocalUser;

        barUi.Style.MinWidth = 48f;
        barUi.Style.PreferredWidth = 48f;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var imageSpaceSlot = barUi.Empty("Image Space");
        imageSpaceSlot.AttachComponent<Mask>();
        var imgMaskImage = imageSpaceSlot.GetComponent<Image>();
        var avatarMaskSprite = imageSpaceSlot.AttachComponent<SpriteProvider>();
        avatarMaskSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        avatarMaskSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        avatarMaskSprite.FixedSize.Value = 18f;
        imgMaskImage.Sprite.Target = avatarMaskSprite;
        imgMaskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;

        barUi.NestInto(imageSpaceSlot);
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var cloudUserInfo = barSlot.AttachComponent<CloudUserInfo>();
        var defaultImg = new Uri("resdb:///bb7d7f1414e0c0a44b4684ecd2a5dc2086c18b3f70c9ed53d467fe96af94e9a9.png");
        var avatarTex = barSlot.AttachComponent<StaticTexture2D>();
        var imgMux = barSlot.AttachComponent<ValueMultiplexer<Uri>>();
        cloudUserInfo.UserId.ForceSet(localUser.UserID);
        imgMux.Target.Target = avatarTex.URL;
        imgMux.Values.Add(defaultImg);
        imgMux.Values.Add();
        var urlCopy = barSlot.AttachComponent<ValueCopy<Uri>>();
        try { urlCopy.Source.Target = cloudUserInfo.TryGetField<Uri>("IconURL"); }
        catch (Exception e) { Msg($"[TopBar] IconURL error: {e}"); }
        urlCopy.Target.Target = imgMux.Values.GetField(1);
        if (localUser.UserID != null) imgMux.Index.ForceSet(1);

        barUi.Image(avatarTex);
        barUi.NestOut();

        string userName = localUser?.UserName ?? "Unknown";
        float nameW = MathX.Max(60f, userName.Length * 12f);
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.MinWidth = nameW;
        barUi.Style.PreferredWidth = nameW;
        barUi.Style.FlexibleHeight = 1f;
        barUi.Style.MinHeight = -1f;
        var nameText = barUi.Text(userName, bestFit: false, alignment: Alignment.MiddleLeft);
        nameText.Size.Value = 18f;
        nameText.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f);

        float barCollapsedW = barPad * 2f + avatarW + barGap + nameW + barGap + toggleW;
        float expandContentW = 7f * 30f + 6f * 6f + 13f + 30f + 100f;
        float barExpandedW = barCollapsedW + barGap + expandContentW;

        void StyleButton(Button btn)
        {
            var textComp = btn.Slot.GetComponentInChildren<FrooxEngine.UIX.Text>();
            if (textComp != null)
            {
                textComp.Size.Value = 18f;
                textComp.Color.Value = new colorX(0.85f, 0.85f, 0.88f, 1f);
            }
            var txtRenderer = btn.Slot.GetComponentInChildren<TextRenderer>();
            if (txtRenderer != null)
            {
                txtRenderer.Color.Value = new colorX(0.85f, 0.85f, 0.88f, 1f);
            }
            if (btn.ColorDrivers.Count > 0)
            {
                var cd = btn.ColorDrivers[0];
                cd.NormalColor.Value = colorX.Clear;
                cd.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.15f);
                cd.PressColor.Value = new colorX(1f, 1f, 1f, 0.08f);
            }
        }

        barUi.Style.MinWidth = 36f;
        barUi.Style.PreferredWidth = 36f;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;
        var toggleBtn = barUi.Button("≡");
        StyleButton(toggleBtn);
        if (toggleBtn.ColorDrivers.Count > 0)
        {
            var cd = toggleBtn.ColorDrivers[0];
            cd.PressColor.Value = new colorX(0.15f, 0.15f, 0.18f, 1f);
        }
        var toggleImg = toggleBtn.Slot.GetComponent<Image>();
        if (toggleImg != null && roundedSprite != null)
        {
            toggleImg.Sprite.Target = roundedSprite;
            toggleImg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        }
        var toggleText = toggleBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (toggleText != null) toggleText.Size.Value = 42f;

        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = 1f;
        barUi.Style.MinWidth = -1f;
        barUi.Style.MinHeight = -1f;
        var expandPanel = barUi.Empty("ExpandPanel");
        var ep = new UIBuilder(expandPanel);
        var epLayout = ep.HorizontalLayout(6f, childAlignment: Alignment.MiddleLeft);
        epLayout.ForceExpandWidth.Value = false;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = 1f;

        ep.Style.MinWidth = 30f;
        ep.Style.PreferredWidth = 30f;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;

        var kbBtn = ep.Button("⌨");      StyleButton(kbBtn);
        var pasteBtn = ep.Button("📋");   StyleButton(pasteBtn);
        var testStreamBtn = ep.Button("👁"); StyleButton(testStreamBtn);
        var resyncBtn = ep.Button("🔄");  StyleButton(resyncBtn);
        var anchorBtn = ep.Button("⚓");   StyleButton(anchorBtn);
        var privateBtn = ep.Button("🔒"); StyleButton(privateBtn);
        var githubBtn = ep.Button("🔗");  StyleButton(githubBtn);
        githubBtn.SendSlotEvents.Value = true;
        var hyperlink = githubBtn.Slot.AttachComponent<Hyperlink>();
        hyperlink.URL.Value = new Uri("https://github.com/DevL0rd/DesktopBuddy");
        hyperlink.Reason.Value = "DesktopBuddy GitHub";

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        ep.Image(new colorX(0.4f, 0.4f, 0.45f, 0.4f));

        ep.Style.MinWidth = 24f;
        ep.Style.PreferredWidth = 24f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;
        ep.Style.FlexibleWidth = -1f;
        var volIcon = ep.Text("🔊", bestFit: false, alignment: Alignment.MiddleCenter);
        volIcon.Size.Value = 16f;
        volIcon.Color.Value = new colorX(0.6f, 0.6f, 0.6f, 1f);

        ep.Style.FlexibleWidth = -1f;
        ep.Style.MinWidth = 80f;
        ep.Style.PreferredWidth = 100f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;

        var streamVolRow = ep.Empty("StreamVol");
        var streamVolUi = new UIBuilder(streamVolRow);
        streamVolUi.Style.FlexibleWidth = 1f;
        streamVolUi.Style.FlexibleHeight = 1f;
        var volSlider = streamVolUi.Slider<float>(20f, 1f, 0f, 1f, false);

        var widthField = barSlot.AttachComponent<ValueField<float>>();
        widthField.Value.Value = barCollapsedW;
        var widthSmooth = barSlot.AttachComponent<SmoothValue<float>>();
        widthSmooth.Speed.Value = 10f;
        widthSmooth.TargetValue.Value = barCollapsedW;
        widthSmooth.Value.Target = widthField.Value;
        widthSmooth.WriteBack.Value = false;

        bool barExpanded = true;
        float barYPos = worldHalfH + barH / 2f * canvasScale + barMarginTop;

        float _lastBarW = barExpandedW;
        widthField.Value.Value = barExpandedW;
        widthSmooth.TargetValue.Value = barExpandedW;
        void BarUpdateLoop()
        {
            if (root.IsDestroyed || barSlot.IsDestroyed) return;
            float cw = widthField.Value.Value;
            if (cw != _lastBarW)
            {
                _lastBarW = cw;
                barCanvas.Size.Value = new float2(cw, barH);
                barSlot.LocalPosition = new float3(
                    -worldHalfW + cw / 2f * canvasScale,
                    barYPos, 0f);
            }
            float target = widthSmooth.TargetValue.Value;
            if (Math.Abs(cw - target) > 0.5f)
                root.World.RunInUpdates(1, BarUpdateLoop);
        }
        barCanvas.Size.Value = new float2(barExpandedW, barH);
        barSlot.LocalPosition = new float3(
            -worldHalfW + barExpandedW / 2f * canvasScale,
            barYPos, 0f);
        root.World.RunInUpdates(1, BarUpdateLoop);

        toggleBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            barExpanded = !barExpanded;
            widthSmooth.TargetValue.Value = barExpanded ? barExpandedW : barCollapsedW;
            root.World.RunInUpdates(1, BarUpdateLoop);
        };

        if (isChild)
            barSlot.ActiveSelf = false;

        Msg($"[TopBar] Created, user '{userName}'");

        Slot keyboardSlot = null;
        kbBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Keyboard] Button pressed!");
            if (keyboardSlot != null && !keyboardSlot.IsDestroyed)
            {
                bool show = !keyboardSlot.ActiveSelf;
                Msg($"[Keyboard] Toggling visibility: {keyboardSlot.ActiveSelf} -> {show}");
                keyboardSlot.ActiveSelf = show;
                if (show)
                {
                    keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);
                    keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
                    keyboardSlot.LocalScale = float3.One;
                }
                return;
            }
            Msg("[Keyboard] Spawning virtual keyboard (favorite or fallback)");
            keyboardSlot = root.AddLocalSlot("Virtual Keyboard", false);
            session.KeyboardSource = keyboardSlot.AttachComponent<DesktopKeyboardSource>();
            keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);
            keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
            keyboardSlot.StartTask(async () =>
            {
                try
                {
                    var vk = await keyboardSlot.SpawnEntity<VirtualKeyboard>(
                        FavoriteEntity.Keyboard,
                        (Slot s) =>
                        {
                            Msg("[Keyboard] Using fallback SimpleVirtualKeyboard");
                            s.AttachComponent<SimpleVirtualKeyboard>();
                            return s.GetComponent<VirtualKeyboard>();
                        });
                    Msg($"[Keyboard] Spawned: {vk != null}, slot children: {keyboardSlot.ChildrenCount}, globalScale={keyboardSlot.GlobalScale}");
                }
                catch (Exception ex)
                {
                    Msg($"[Keyboard] ERROR spawning: {ex}");
                }
            });
        };

        bool streamTestMode = false;
        ValueUserOverride<bool> streamVisRef = null;
        VideoTextureProvider videoTexRef = null;
        var testActiveColor = new colorX(0.2f, 0.45f, 0.25f, 1f);
        testStreamBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[TestStream] Button pressed");
            if (streamVisRef != null && !streamVisRef.IsDestroyed)
            {
                streamTestMode = !streamTestMode;
                streamVisRef.SetOverride(root.World.LocalUser, streamTestMode);
                displaySlot.ActiveSelf = !streamTestMode;
                var img = testStreamBtn.Slot.GetComponent<Image>();
                if (img != null) img.Tint.Value = streamTestMode ? testActiveColor : colorX.Clear;
                Msg($"[TestStream] Test mode: {streamTestMode} (stream={streamTestMode}, preview={!streamTestMode})");
            }
            else
            {
                Msg("[TestStream] No stream available");
            }
        };

        resyncBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Resync] Button pressed");
            if (videoTexRef != null && !videoTexRef.IsDestroyed)
            {
                var savedUrl = videoTexRef.URL.Value;
                Msg($"[Resync] Forcing full reload: {savedUrl}");
                videoTexRef.URL.Value = null;
                root.World.RunInUpdates(10, () =>
                {
                    if (videoTexRef != null && !videoTexRef.IsDestroyed)
                    {
                        videoTexRef.URL.Value = savedUrl;
                        Msg($"[Resync] URL restored: {savedUrl}");
                    }
                });
            }
            else
            {
                Msg("[Resync] No stream available");
            }
        };

        bool isAnchored = false;
        var anchorActiveColor = new colorX(0.2f, 0.45f, 0.25f, 1f);
        anchorBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Anchor] Button pressed");
            var localUser = root.World.LocalUser;
            if (localUser?.Root == null) return;
            if (!isAnchored)
            {
                root.SetParent(localUser.Root.Slot, keepGlobalTransform: true);
                Msg($"[Anchor] Anchored to user");
                isAnchored = true;
            }
            else
            {
                root.SetParent(root.World.RootSlot, keepGlobalTransform: true);
                Msg($"[Anchor] Unanchored to world");
                isAnchored = false;
            }
            var img = anchorBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isAnchored ? anchorActiveColor : colorX.Clear;
        };

        if (!isChild)
        {
            var camSlot = root.AddSlot("VirtualCamera");
            camSlot.LocalPosition = new float3(0f, worldHalfH + 0.02f, -0.001f);
            camSlot.LocalRotation = floatQ.Euler(0f, 180f, 0f);
            camSlot.LocalScale = float3.One;

            var camVisual = camSlot.AddSlot("Visual");
            camVisual.LocalScale = new float3(0.04f, 0.02f, 0.001f);
            var meshRenderer = camVisual.AttachComponent<MeshRenderer>();
            meshRenderer.Mesh.Target = camVisual.AttachComponent<BoxMesh>();
            var camMat = camVisual.AttachComponent<UI_UnlitMaterial>();
            camMat.Tint.Value = new colorX(0.05f, 0.05f, 0.05f, 1f);
            meshRenderer.Materials.Add(camMat);

            var camCollider = camVisual.AttachComponent<BoxCollider>();
            camCollider.Size.Value = float3.One;

            var camButton = camVisual.AttachComponent<PhysicalButton>();
            camButton.LocalPressed += (IButton b, ButtonEventData d) =>
            {
                if (VCam == null) { Msg("[VirtualCamera] Not available"); return; }

                VCam.ManuallyDisabled = !VCam.ManuallyDisabled;
                Msg($"[VirtualCamera] {(VCam.ManuallyDisabled ? "Disabled" : "Enabled")}");
            };

            var cam = camSlot.AttachComponent<Camera>();
            cam.FieldOfView.Value = 90f;
            cam.NearClipping.Value = 0.05f;
            cam.FarClipping.Value = 1000f;
            cam.Clear.Value = Renderite.Shared.CameraClearMode.Color;
            cam.ClearColor.Value = new colorX(0.1f, 0.1f, 0.1f, 1f);

            session.VCamSlot = camSlot;
            session.VCamCamera = cam;
            session.VCamIndicator = camMat;

            bool spatialAudio = Config?.GetValue(SpatialAudioEnabled) ?? false;

            {
                var micSlot = root.AddSlot("VirtualMic");
                micSlot.LocalPosition = new float3(0.03f, worldHalfH + 0.02f, -0.001f);
                micSlot.LocalRotation = floatQ.Identity;
                micSlot.LocalScale = float3.One;

                var micVisual = micSlot.AddSlot("Visual");
                micVisual.LocalScale = new float3(0.015f, 0.02f, 0.001f);
                var micMeshRenderer = micVisual.AttachComponent<MeshRenderer>();
                micMeshRenderer.Mesh.Target = micVisual.AttachComponent<BoxMesh>();
                var micMat = micVisual.AttachComponent<UI_UnlitMaterial>();
                micMat.Tint.Value = new colorX(0.1f, 0.8f, 0.1f, 1f);
                micMeshRenderer.Materials.Add(micMat);

                var micCollider = micVisual.AttachComponent<BoxCollider>();
                micCollider.Size.Value = float3.One;
                session.VMicIndicator = micMat;

                var listener = micSlot.AttachComponent<AudioListener>();
                session.VMicListener = listener;

                var micButton = micVisual.AttachComponent<PhysicalButton>();
                micButton.LocalPressed += (IButton b, ButtonEventData d) =>
                {
                    session.VMicMuted = !session.VMicMuted;
                    micMat.Tint.Value = session.VMicMuted
                        ? new colorX(0.3f, 0.05f, 0.05f, 1f)
                        : new colorX(0.1f, 0.8f, 0.1f, 1f);
                    Msg($"[VirtualMic] {(session.VMicMuted ? "Muted" : "Unmuted")}");
                };
            }

            if (spatialAudio)
            {
                var localAudioSlot = root.AddLocalSlot("LocalAudio", false);
                var audioSource = localAudioSlot.AttachComponent<DesktopAudioSource>();
                session.SpatialAudioSource = audioSource;

                var spatialOutput = localAudioSlot.AttachComponent<AudioOutput>();
                spatialOutput.Source.Target = audioSource;
                spatialOutput.Volume.Value = 1f;
                spatialOutput.SpatialBlend.Value = 1f;
                spatialOutput.MinDistance.Value = 0.5f;
                spatialOutput.MaxDistance.Value = 30f;
                spatialOutput.AudioTypeGroup.Value = AudioTypeGroup.Multimedia;
                session.SpatialAudioOutput = spatialOutput;
            }
        }

        pasteBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Paste] Button pressed");
            WindowInput.SendPaste();
        };

        bool isPrivate = false;
        string savedStreamUrl = null;

        var rootVis = root.AttachComponent<ValueUserOverride<bool>>();
        rootVis.Target.Target = root.ActiveSelf_Field;
        rootVis.Default.Value = true;
        rootVis.CreateOverrideOnWrite.Value = false;

        privateBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            isPrivate = !isPrivate;
            Msg($"[Private] Mode: {isPrivate}");

            rootVis.Default.Value = !isPrivate;
            rootVis.SetOverride(root.World.LocalUser, true);

            if (videoTexRef != null && !videoTexRef.IsDestroyed)
            {
                if (isPrivate)
                {
                    savedStreamUrl = videoTexRef.URL.Value?.ToString();
                    videoTexRef.URL.Value = null;
                    videoTexRef.Stop();
                    Msg("[Private] Stream disconnected");
                }
                else if (savedStreamUrl != null)
                {
                    videoTexRef.URL.Value = new Uri(savedStreamUrl);
                    Msg($"[Private] Stream restored: {savedStreamUrl}");
                }
            }

            var img = privateBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isPrivate ? new colorX(0.5f, 0.2f, 0.2f, 1f) : colorX.Clear;
        };

        bool isDesktopCapture = hwnd == IntPtr.Zero;
        uint capturedPid = processId;

        var ownerRef = root.AttachComponent<ReferenceField<FrooxEngine.User>>();
        ownerRef.Reference.Target = root.World.LocalUser;

        if (!(Config?.GetValue(SpatialAudioEnabled) ?? true))
        {
            volSlider.Value.OnValueChange += (SyncField<float> field) =>
            {
                if (ownerRef.Reference.Target == root.World.LocalUser)
                {
                    if (isDesktopCapture)
                        WindowVolume.SetMasterVolume(field.Value);
                    else if (capturedPid != 0)
                        WindowVolume.SetProcessVolume(capturedPid, field.Value);
                }
            };
        }

        Canvas backCanvasRef = null;
        Canvas streamCanvasRef = null;
        TextRenderer titleTextRef = null;

        {
            var backSlot = root.AddSlot("BackPanel");
            backSlot.LocalPosition = new float3(0f, 0f, 0.001f);
            backSlot.LocalRotation = floatQ.Euler(0f, 180f, 0f);
            backSlot.LocalScale = float3.One * canvasScale;

            var backCanvas = backSlot.AttachComponent<Canvas>();
            backCanvasRef = backCanvas;
            backCanvas.Size.Value = new float2(w, h);
            var backUi = new UIBuilder(backCanvas);

            var backMat = backSlot.AttachComponent<UI_UnlitMaterial>();
            backMat.BlendMode.Value = BlendMode.Alpha;
            backMat.Sidedness.Value = Sidedness.Double;
            backMat.ZWrite.Value = ZWrite.On;
            backMat.OffsetUnits.Value = 100f;

            var bg = backUi.Image(new colorX(0.08f, 0.08f, 0.1f, 1f));
            bg.Material.Target = backMat;

            backUi.NestInto(bg.RectTransform);
            backUi.VerticalLayout(16f);
            backUi.Style.FlexibleWidth = 1f;
            backUi.Style.FlexibleHeight = 1f;

            backUi.Spacer(1f);

            float iconSize = Math.Min(w, h) * 0.25f;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    var iconData = WindowIconExtractor.GetLargeIconRGBA(hwnd, out int iw, out int ih, 128);
                    if (iconData != null && iw > 0 && ih > 0)
                    {
                        backUi.Style.MinHeight = iconSize;
                        backUi.Style.PreferredHeight = iconSize;
                        backUi.Style.FlexibleHeight = -1f;

                        var iconTex = backSlot.AttachComponent<StaticTexture2D>();
                        var iconMat = backSlot.AttachComponent<UI_UnlitMaterial>();
                        iconMat.Texture.Target = iconTex;
                        iconMat.OffsetFactor.Value = -1f;
                        var iconImg = backUi.RawImage(iconTex);
                        iconImg.PreserveAspect.Value = true;
                        iconImg.Material.Target = iconMat;

                        var capturedIconData = iconData;
                        var capturedIw = iw;
                        var capturedIh = ih;
                        var capturedTex = iconTex;
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var bitmap = new Bitmap2D(capturedIconData, capturedIw, capturedIh,
                                    Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                                var uri = await root.Engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                                if (uri != null)
                                {
                                    capturedTex.World.RunInUpdates(0, () =>
                                    {
                                        if (!capturedTex.IsDestroyed)
                                            capturedTex.URL.Value = uri;
                                    });
                                }
                            }
                            catch (Exception ex) { Msg($"[BackPanel] Icon save error: {ex.Message}"); }
                        });
                        backUi.Style.FlexibleHeight = 1f;
                        Msg("[BackPanel] Icon added");
                    }
                }
                catch (Exception ex) { Msg($"[BackPanel] Icon error: {ex.Message}"); }
            }

            backUi.Style.MinHeight = 64f;
            backUi.Style.PreferredHeight = 64f;
            backUi.Style.FlexibleHeight = -1f;
            var text = backUi.Text(title, bestFit: true, alignment: Alignment.MiddleCenter);
            titleTextRef = text.Slot.GetComponent<TextRenderer>();
            text.Size.Value = 48f;
            text.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f);

            root.World.RunInUpdates(2, () =>
            {
                try
                {
                    var autoMat = text.Slot.GetComponentInParents<UI_TextUnlitMaterial>();
                    if (autoMat != null)
                    {
                        autoMat.OffsetFactor.Value = -1f;
                        Msg("[BackPanel] Set OffsetFactor=-1 on auto text material");
                    }
                    else
                    {
                        Msg("[BackPanel] Could not find auto UI_TextUnlitMaterial");
                    }
                }
                catch (Exception ex) { Msg($"[BackPanel] Text material fix error: {ex.Message}"); }
            });

            backUi.Style.FlexibleHeight = 1f;
            backUi.Spacer(1f);

            Msg($"[BackPanel] Created with title '{title}'");
        }

        if (!_updateShown && !isChild)
        {
            _updateShown = true;
            var capturedRoot = root;
            var capturedWorld = root.World;
            float capturedW = w;
            float capturedScale = canvasScale;
            System.Threading.Tasks.Task.Run(() =>
            {
                CheckForUpdate();
                if (_latestVersion == null) return;
                capturedWorld.RunInUpdates(0, () =>
                {
                    if (capturedRoot.IsDestroyed) return;
                    ShowUpdatePopup(capturedRoot, capturedW, capturedScale);
                });
            });
        }

        if (StreamServer != null && TunnelUrl != null)
        {
            try
            {
                SharedStream shared;
                lock (_sharedStreams)
                {
                    if (hwnd == IntPtr.Zero || !_sharedStreams.TryGetValue(hwnd, out shared))
                    {
                        int streamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                        var encoder = StreamServer.CreateEncoder(streamId);

                        var audio = new AudioCapture();
                        if (hwnd != IntPtr.Zero)
                            audio.Start(hwnd, AudioCaptureMode.IncludeProcess);
                        else
                            audio.Start(IntPtr.Zero, AudioCaptureMode.ExcludeProcess);

                        var url = new Uri($"{TunnelUrl}/stream/{streamId}");
                        shared = new SharedStream { StreamId = streamId, Encoder = encoder, Audio = audio, StreamUrl = url, RefCount = 0 };
                        if (hwnd != IntPtr.Zero)
                            _sharedStreams[hwnd] = shared;
                        Msg($"[RemoteStream] Created new shared stream {streamId} for hwnd={hwnd}");
                    }
                    else
                    {
                        Msg($"[RemoteStream] Reusing shared stream {shared.StreamId} for hwnd={hwnd} (refs={shared.RefCount})");
                    }
                    shared.RefCount++;
                }
                session.StreamId = shared.StreamId;
                var nvEncoder = shared.Encoder;

                if (session.SpatialAudioSource != null && shared.Audio != null)
                    session.SpatialAudioSource.SetAudioCapture(shared.Audio);

                bool isFirstForHwnd = shared.RefCount == 1;
                if (isFirstForHwnd)
                {
                    ConnectEncoder(session, nvEncoder);
                    Msg($"[RemoteStream] This panel drives the encoder for stream {shared.StreamId}");
                }
                else
                {
                    Msg($"[RemoteStream] This panel shares encoder from stream {shared.StreamId}, no encoding hook");
                }

                var videoSlot = root.AddSlot("StreamProvider");
                var videoTex = videoSlot.AttachComponent<VideoTextureProvider>();
                videoTex.URL.Value = shared.StreamUrl;
                videoTex.Stream.Value = true;
                videoTex.Volume.Value = 0f;
                videoTexRef = videoTex;
                session.VideoTexture = videoTex;

                var audioOutput = videoSlot.AttachComponent<AudioOutput>();
                audioOutput.Source.Target = videoTex;
                audioOutput.Volume.Value = 1f;
                audioOutput.AudioTypeGroup.Value = AudioTypeGroup.Multimedia;
                audioOutput.ExludeUser(root.World.LocalUser);

                var volDriver = videoSlot.AttachComponent<ValueDriver<float>>();
                volDriver.DriveTarget.Target = audioOutput.Volume;
                volDriver.ValueSource.Target = volSlider.Value;

                if (session.SpatialAudioOutput != null)
                {
                    var spatialOut = session.SpatialAudioOutput;
                    volSlider.Value.OnValueChange += (SyncField<float> field) =>
                    {
                        if (spatialOut != null && !spatialOut.IsDestroyed)
                            spatialOut.Volume.Value = field.Value;
                    };
                }

                var streamSlot = root.AddSlot("RemoteStreamVisual");
                streamSlot.LocalScale = float3.One * canvasScale;

                var streamVis = streamSlot.AttachComponent<ValueUserOverride<bool>>();
                streamVis.Target.Target = streamSlot.ActiveSelf_Field;
                streamVis.Default.Value = true;
                streamVis.CreateOverrideOnWrite.Value = false;
                streamVis.SetOverride(root.World.LocalUser, false);
                streamVisRef = streamVis;
                Msg("[RemoteStream] Per-user visibility on visual (local=false, others=true)");

                var streamCanvas = streamSlot.AttachComponent<Canvas>();
                streamCanvasRef = streamCanvas;
                streamCanvas.Size.Value = new float2(w, h);
                var streamUi = new UIBuilder(streamCanvas);

                var streamBg = streamUi.Image(new colorX(0f, 0f, 0f, 1f));
                streamUi.NestInto(streamBg.RectTransform);

                var streamImg = streamUi.RawImage(videoTex);
                var streamMat = streamSlot.AttachComponent<UI_UnlitMaterial>();
                streamMat.BlendMode.Value = BlendMode.Alpha;
                streamMat.ZWrite.Value = ZWrite.On;
                streamMat.OffsetUnits.Value = -100f;
                streamImg.Material.Target = streamMat;

                Msg($"[RemoteStream] Created, URL={shared.StreamUrl}, streamId={shared.StreamId}, refs={shared.RefCount}");

                int checkCount = 0;
                root.World.RunInUpdates(30, () => CheckVideoState());
                void CheckVideoState()
                {
                    if (videoTex == null || videoTex.IsDestroyed || root.IsDestroyed) return;
                    checkCount++;
                    bool assetAvail = videoTex.IsAssetAvailable;
                    string playbackEngine = videoTex.CurrentPlaybackEngine?.Value ?? "null";
                    bool isPlaying = videoTex.IsPlaying;
                    float clockErr = videoTex.CurrentClockError?.Value ?? -1f;
                    Msg($"[RemoteStream] Check #{checkCount}: avail={assetAvail} engine={playbackEngine} playing={isPlaying} clockErr={clockErr:F2}");

                    if (assetAvail && !isPlaying)
                    {
                        videoTex.Play();
                        Msg("[RemoteStream] Called Play() on VideoTextureProvider");
                    }

                    if (checkCount < 10)
                        root.World.RunInUpdates(60, () => CheckVideoState());
                    else if (checkCount < 30)
                        root.World.RunInUpdates(60 * 30, () => CheckVideoState());
                }
            }
            catch (Exception ex)
            {
                Msg($"[RemoteStream] ERROR: {ex}");
            }
        }
        else
        {
            Msg($"[RemoteStream] Skipped: StreamServer={StreamServer != null} TunnelUrl={TunnelUrl ?? "null"}");
        }


        grabbable = root.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
        Msg("[StartStreaming] Grabbable attached");

        {
            const int HISTORY_SIZE = 5;
            float3[] posHistory = new float3[HISTORY_SIZE];
            floatQ[] rotHistory = new floatQ[HISTORY_SIZE];
            double[] timeHistory = new double[HISTORY_SIZE];
            int histIdx = 0;
            bool wasGrabbed = false;
            bool thrown = false;

            void ThrowTrackLoop()
            {
                if (root.IsDestroyed || thrown) return;
                bool isGrabbed = grabbable.IsGrabbed;

                if (isGrabbed)
                {
                    int idx = histIdx % HISTORY_SIZE;
                    posHistory[idx] = root.GlobalPosition;
                    rotHistory[idx] = root.GlobalRotation;
                    timeHistory[idx] = root.World.Time.WorldTime;
                    histIdx++;
                }
                else if (wasGrabbed && histIdx >= 2)
                {
                    int newest = (histIdx - 1) % HISTORY_SIZE;
                    int oldest = (histIdx >= HISTORY_SIZE) ? (histIdx % HISTORY_SIZE) : 0;
                    double dt = timeHistory[newest] - timeHistory[oldest];
                    if (dt > 0.001)
                    {
                        float3 velocity = (posHistory[newest] - posHistory[oldest]) / (float)dt;
                        float speed = velocity.Magnitude;
                        Msg($"[Throw] Release velocity: {speed:F2} m/s");

                        if (speed > 3f)
                        {
                            thrown = true;
                            Msg($"[Throw] Thrown! velocity={speed:F2} m/s");

                            var cc = root.AttachComponent<CharacterController>();
                            cc.SimulatingUser.Target = localUser;
                            cc.Gravity.Value = new float3(0f, -9.81f, 0f);
                            cc.LinearDamping.Value = 0.3f;
                            cc.LinearVelocity = velocity;

                            int prev = (histIdx - 2 + HISTORY_SIZE) % HISTORY_SIZE;
                            double frameDt = timeHistory[newest] - timeHistory[prev];
                            floatQ perFrameRot = floatQ.Identity;
                            if (frameDt > 0.001)
                            {
                                floatQ rotDelta = rotHistory[newest] * rotHistory[prev].Conjugated;
                                float dtRatio = (1f / 60f) / (float)frameDt;
                                var identity = floatQ.Identity;
                                perFrameRot = MathX.Slerp(in identity, rotDelta, dtRatio);
                            }

                            float fadeSeconds = 1f;
                            double startTime = root.World.Time.WorldTime;
                            float3 lastPos = root.GlobalPosition;
                            int frameCount = 0;

                            void FadeAndCollisionLoop()
                            {
                                if (root.IsDestroyed) return;
                                frameCount++;
                                double elapsed = root.World.Time.WorldTime - startTime;
                                float t = MathX.Clamp01((float)(elapsed / fadeSeconds));

                                float scale = MathX.Lerp(1f, 0f, t * t);
                                root.LocalScale = float3.One * MathX.Max(0.01f, scale);

                                root.LocalRotation = root.LocalRotation * perFrameRot;

                                float3 curPos = root.GlobalPosition;
                                if (frameCount > 5)
                                {
                                    float delta = (curPos - lastPos).Magnitude;
                                    if (delta < 0.001f)
                                    {
                                        root.Destroy();
                                        return;
                                    }
                                }
                                lastPos = curPos;

                                if (t >= 1f)
                                {
                                    root.Destroy();
                                    return;
                                }
                                root.World.RunInUpdates(1, FadeAndCollisionLoop);
                            }
                            root.World.RunInUpdates(1, FadeAndCollisionLoop);
                            return;
                        }
                    }
                    histIdx = 0;
                }
                wasGrabbed = isGrabbed;
                root.World.RunInUpdates(isGrabbed ? 1 : 10, ThrowTrackLoop);
            }
            root.World.RunInUpdates(1, ThrowTrackLoop);
        }

        void UpdateLayout(int newW, int newH)
        {
            worldHalfW = newW / 2f * canvasScale;
            worldHalfH = newH / 2f * canvasScale;
            barYPos = worldHalfH + barH / 2f * canvasScale + barMarginTop;

            if (session.Collider != null && !session.Collider.IsDestroyed)
                session.Collider.Size.Value = new float3(newW * canvasScale, newH * canvasScale, 0.001f);

            if (backCanvasRef != null && !backCanvasRef.IsDestroyed)
                backCanvasRef.Size.Value = new float2(newW, newH);

            if (streamCanvasRef != null && !streamCanvasRef.IsDestroyed)
                streamCanvasRef.Size.Value = new float2(newW, newH);

            if (barSlot != null && !barSlot.IsDestroyed)
                barSlot.LocalPosition = new float3(
                    -worldHalfW + _lastBarW / 2f * canvasScale,
                    barYPos, 0f);

            if (keyboardSlot != null && keyboardSlot.ActiveSelf && !keyboardSlot.IsDestroyed)
                keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);

            Msg($"[Resize] UI updated to {newW}x{newH}");
        }
        session.OnResize = UpdateLayout;

        root.PersistentSelf = false;
        root.Name = $"Desktop: {title}";
        session.TitleText = titleTextRef;
        session.LastTitle = title;

        ScheduleUpdate(root.World);

        if (!isChild)
            WindowInput.FocusWindow(hwnd);

        bool useSpatialAudio = Config?.GetValue(SpatialAudioEnabled) ?? true;
        if (useSpatialAudio && !isChild && !isDesktopCapture && processId != 0 && VBCableSetup.IsInstalled())
        {
            string cableId = VBCableSetup.FindCableInputDeviceId();
            if (cableId != null)
            {
                AudioRouter.SetProcessOutputDevice(processId, cableId);
                session.OwnsAudioRedirect = true;
            }
        }

        Msg($"[StartStreaming] Window focused, streaming started for: {title}");
    }

    private static void SpawnChildWindow(DesktopSession parentSession, IntPtr childHwnd, string childTitle = null)
    {
        if (!WindowEnumerator.TryGetWindowRect(parentSession.Hwnd, out int px, out int py, out int pw, out int ph))
        {
            Msg($"[ChildWindow] Failed to get parent window rect");
            return;
        }
        if (!WindowEnumerator.TryGetWindowRect(childHwnd, out int cx, out int cy, out int cw, out int ch))
        {
            Msg($"[ChildWindow] Failed to get child window rect hwnd={childHwnd}");
            return;
        }
        if (cw <= 0 || ch <= 0) return;

        string title = childTitle;
        if (string.IsNullOrEmpty(title)) title = $"Popup ({childHwnd})";

        float canvasScale = 0.0005f;
        float offsetX, offsetY;
        float offsetZ = -0.01f;

        bool isExplorer = false;
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById((int)parentSession.ProcessId);
            isExplorer = proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Msg($"[ChildWindow] Process check error: {ex.Message}"); }

        if (isExplorer)
        {
            offsetX = 0f;
            offsetY = 0f;
            Msg($"[ChildWindow] Explorer detected — centering child on parent");
        }
        else
        {
            offsetX = ((cx - px) + cw / 2f - pw / 2f) * canvasScale;
            offsetY = (-(cy - py) - ch / 2f + ph / 2f) * canvasScale;
        }

        var root = parentSession.Root.AddSlot($"Popup: {title}");
        root.LocalPosition = new float3(offsetX, offsetY, offsetZ);
        Msg($"[ChildWindow] Spawning full DesktopBuddy for hwnd={childHwnd} title='{title}' size={cw}x{ch} offset=({offsetX:F4},{offsetY:F4})");

        parentSession.TrackedChildHwnds.Add(childHwnd);

        try
        {
            StartStreaming(root, childHwnd, title, isChild: true, parentSession: parentSession);
        }
        catch (Exception ex)
        {
            Msg($"[ChildWindow] Failed to spawn: {ex.Message}");
            parentSession.TrackedChildHwnds.Remove(childHwnd);
            if (!root.IsDestroyed) root.Destroy();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private static readonly HashSet<World> _scheduledWorlds = new();

    internal static void ScheduleUpdate(World world)
    {
        if (_scheduledWorlds.Contains(world)) return;
        _scheduledWorlds.Add(world);
        world.RunInUpdates(1, () => UpdateLoop(world));
    }

    private static int _updateCount;



    private static void ConnectEncoder(DesktopSession session, FfmpegEncoder encoder)
    {
        if (encoder == null || session.Streamer == null) return;
        var contextLock = session.Streamer.D3dContextLock;
        AudioCapture audioForEncoder = null;
        lock (_sharedStreams)
        {
            if (_sharedStreams.TryGetValue(session.Hwnd, out var shared))
                audioForEncoder = shared.Audio;
        }
        var enc = encoder;
        session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
        {
            enc.StartInitializeAsync(device, (uint)fw, (uint)fh, contextLock, audioForEncoder);
            enc.QueueFrame(texture, (uint)fw, (uint)fh);
        };
    }

    /// <summary>
    /// Re-sends the DesktopTexture.Update() message to the renderer for a window capture
    /// whose UWC source may not have been registered on the first attempt.
    /// </summary>
    private static void RetriggerDesktopTexture(DesktopTextureProvider provider)
    {
        try
        {
            var type = typeof(DesktopTextureProvider);
            var assetField = type.GetField("_desktopTex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (assetField == null) return;

            var desktopTex = assetField.GetValue(provider) as DesktopTexture;
            if (desktopTex == null) return;

            var onCreatedMethod = type.GetMethod("OnTextureCreated",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (onCreatedMethod == null) return;

            var callback = (Action)Delegate.CreateDelegate(typeof(Action), provider, onCreatedMethod);
            desktopTex.Update(provider.DisplayIndex.Value, callback);
        }
        catch (Exception ex)
        {
            Msg($"[RetriggerDesktopTexture] Error: {ex.Message}");
        }
    }

    private static void CleanupSession(DesktopSession session)
    {
        if (session.Cleaned) { Msg($"[Cleanup] Already cleaned hwnd={session.Hwnd} streamId={session.StreamId}, skipping"); return; }
        session.Cleaned = true;
        Msg($"[Cleanup] === START === hwnd={session.Hwnd} streamId={session.StreamId} isChild={session.IsChildPanel} children={session.ChildSessions.Count}");

        if (VMic != null && session.VMicListener != null)
        {
            Msg("[Cleanup] Disposing VMic (listener destroyed)");
            VMic.Dispose();
            VMic = null;
        }

        if (session.OwnsAudioRedirect && session.ProcessId != 0)
        {
            bool otherSessionUsesSamePid = false;
            foreach (var s in ActiveSessions)
            {
                if (s != session && !s.Cleaned && s.ProcessId == session.ProcessId)
                {
                    otherSessionUsesSamePid = true;
                    break;
                }
            }
            if (!otherSessionUsesSamePid)
            {
                AudioRouter.ResetProcessToDefault(session.ProcessId);
                Msg($"[Cleanup] Reset audio routing for PID {session.ProcessId}");
            }
            else
            {
                Msg($"[Cleanup] Keeping audio routing for PID {session.ProcessId} (other sessions still active)");
            }
        }

        if (session.ChildSessions.Count > 0)
        {
            Msg($"[Cleanup] Destroying {session.ChildSessions.Count} child popup panels");
            foreach (var child in session.ChildSessions)
            {
                child.ParentSession = null;
                Msg($"[Cleanup] Child: disconnecting VTP hwnd={child.Hwnd}");
                {
                    var vtp = child.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed) { vtp.URL.Value = null; vtp.Stop(); }
                    child.VideoTexture = null;
                    var rootToDie = child.Root;
                    if (rootToDie != null && !rootToDie.IsDestroyed)
                    {
                        var childWorld = rootToDie.World;
                        if (childWorld != null && !childWorld.IsDestroyed)
                        {
                            childWorld.RunInUpdates(10, () =>
                            {
                                Msg($"[Cleanup] Child deferred destroy executing hwnd={child.Hwnd}");
                                if (rootToDie != null && !rootToDie.IsDestroyed) rootToDie.Destroy();
                                Msg($"[Cleanup] Child deferred destroy complete hwnd={child.Hwnd}");
                            });
                        }
                        else
                        {
                            Msg($"[Cleanup] Child world dead, destroying now hwnd={child.Hwnd}");
                            rootToDie.Destroy();
                        }
                    }
                }
                Msg($"[Cleanup] Child: calling CleanupSession recursively hwnd={child.Hwnd}");
                CleanupSession(child);
                Msg($"[Cleanup] Child: done hwnd={child.Hwnd}");
            }
            session.ChildSessions.Clear();
            session.TrackedChildHwnds.Clear();
        }

        if (session.ParentSession != null)
        {
            Msg($"[Cleanup] Removing from parent tracking");
            session.ParentSession.TrackedChildHwnds.Remove(session.Hwnd);
            session.ParentSession.ChildSessions.Remove(session);
        }

        Msg($"[Cleanup] Removing canvas ID");
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        if (session.CaptureSlot >= 0 && CaptureChannel != null)
        {
            CaptureChannel.StopSession(session.CaptureSlot);
            Msg($"[Cleanup] Stopped capture slot {session.CaptureSlot}");
        }

        if (session.Texture != null)
            OurProviders.Remove(session.Texture);

        Msg($"[Cleanup] Disconnecting encoder");
        var streamer = session.Streamer;
        if (streamer != null) streamer.OnGpuFrame = null;
        if (session.StreamId > 0)
        {
            lock (_sharedStreams)
            {
                if (_sharedStreams.TryGetValue(session.Hwnd, out var shared) && shared.Encoder != null)
                    shared.Encoder.Stop();
            }
        }
        int streamId = session.StreamId;
        IntPtr hwnd = session.Hwnd;
        session.Streamer = null;

        Msg($"[Cleanup] Queuing background dispose for stream {streamId}");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Msg($"[Cleanup:BG] === START === stream {streamId}");

                streamer?.FlushD3dContext();

                AudioCapture audioToDispose = null;
                bool shouldStopEncoder = false;
                if (streamId > 0)
                {
                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(hwnd, out var shared) && shared.StreamId == streamId)
                        {
                            shared.RefCount--;
                            Msg($"[Cleanup:BG] Stream {shared.StreamId} refs now {shared.RefCount}");
                            if (shared.RefCount <= 0)
                            {
                                _sharedStreams.Remove(hwnd);
                                audioToDispose = shared.Audio;
                                shouldStopEncoder = true;
                            }
                        }
                        else
                        {
                            shouldStopEncoder = true;
                        }
                    }

                    if (shouldStopEncoder)
                    {
                        Msg($"[Cleanup:BG] Stopping encoder {streamId}...");
                        StreamServer?.StopEncoder(streamId);
                        Msg($"[Cleanup:BG] Encoder {streamId} stopped");
                    }

                    bool forceGC = Config?.GetValue(ImmediateGC) ?? false;
                    if (forceGC)
                    {
                        Msg("[Cleanup:BG] Forcing GC after encoder dispose");
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }

                Msg($"[Cleanup:BG] Stopping capture...");
                streamer?.StopCapture();
                Msg($"[Cleanup:BG] Capture stopped");

                Msg($"[Cleanup:BG] Disposing streamer...");
                streamer?.Dispose();
                Msg($"[Cleanup:BG] Streamer disposed");

                if (audioToDispose != null)
                {
                    Msg($"[Cleanup:BG] Disposing audio...");
                    audioToDispose.Dispose();
                    Msg($"[Cleanup:BG] Audio disposed");
                }

                Msg($"[Cleanup:BG] === DONE === stream {streamId}");
            }
            catch (Exception ex)
            {
                Msg($"[Cleanup:BG] ERROR: {ex}");
            }
        });
        Msg($"[Cleanup] === END (bg queued) === stream {streamId}");
    }

    private static void WindowPollerLoop()
    {
        while (_windowPollerRunning)
        {
            Thread.Sleep(100);
            if (!_windowPollerRunning) break;

            DesktopSession[] snapshot;
            try { snapshot = ActiveSessions.ToArray(); }
            catch { continue; }

            var byProcess = new Dictionary<uint, List<DesktopSession>>();
            foreach (var session in snapshot)
            {
                if (session.Cleaned || session.IsChildPanel || session.ProcessId == 0) continue;
                if (!byProcess.TryGetValue(session.ProcessId, out var list))
                    byProcess[session.ProcessId] = list = new List<DesktopSession>();
                list.Add(session);
            }

            foreach (var kvp in byProcess)
            {
                if (!_windowPollerRunning) break;
                var sessions = kvp.Value;

                List<WindowEnumerator.WindowInfo> procWindows;
                HashSet<IntPtr> windowSet;
                try
                {
                    procWindows = WindowEnumerator.GetProcessWindows(kvp.Key);
                    windowSet = new HashSet<IntPtr>(procWindows.Count);
                    for (int pw = 0; pw < procWindows.Count; pw++)
                        windowSet.Add(procWindows[pw].Handle);
                }
                catch (Exception ex)
                {
                    Msg($"[WindowPoller] Error enumerating PID {kvp.Key}: {ex.Message}");
                    continue;
                }

                foreach (var session in sessions)
                {
                    try
                    {
                        for (int pw = 0; pw < procWindows.Count; pw++)
                        {
                            if (procWindows[pw].Handle == session.Hwnd && !string.IsNullOrEmpty(procWindows[pw].Title))
                            {
                                if (procWindows[pw].Title != session.LastTitle)
                                {
                                    _windowEvents.Enqueue(new WindowEvent
                                    {
                                        Session = session,
                                        EventType = WindowEventType.TitleChanged,
                                        Title = procWindows[pw].Title
                                    });
                                }
                                break;
                            }
                        }

                        foreach (var win in procWindows)
                        {
                            if (win.Handle == session.Hwnd) continue;
                            if (session.TrackedChildHwnds.Contains(win.Handle)) continue;
                            if (WindowEnumerator.TryGetWindowRect(win.Handle, out _, out _, out int cw2, out int ch2) && cw2 > 10 && ch2 > 10)
                            {
                                session.TrackedChildHwnds.Add(win.Handle);
                                _windowEvents.Enqueue(new WindowEvent
                                {
                                    Session = session,
                                    EventType = WindowEventType.NewChild,
                                    ChildHwnd = win.Handle,
                                    Title = win.Title
                                });
                            }
                        }

                        for (int c = session.ChildSessions.Count - 1; c >= 0; c--)
                        {
                            var child = session.ChildSessions[c];
                            bool childStillActive = child.Streamer != null && windowSet.Contains(child.Hwnd);
                            if (!childStillActive)
                            {
                                _windowEvents.Enqueue(new WindowEvent
                                {
                                    Session = session,
                                    EventType = WindowEventType.ChildClosed,
                                    ChildHwnd = child.Hwnd
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Msg($"[WindowPoller] Error for session hwnd={session.Hwnd}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static void UpdateLoop(World world)
    {
        _updateCount++;
        double dt = world.Time.Delta;

        if (world.IsDestroyed)
        {
            Msg("[UpdateLoop] World destroyed, cleaning up sessions for this world");
            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];
                if (session.Root == null || session.Root.IsDestroyed || session.Root.World == world)
                {
                    Msg($"[UpdateLoop] Cleaning up session {i} (world destroyed)");
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                }
            }
            _scheduledWorlds.Remove(world);
            return;
        }

        try
        {
            int lastVCamIdx = -1;
            int lastVMicIdx = -1;
            for (int k = 0; k < ActiveSessions.Count; k++)
            {
                var s = ActiveSessions[k];
                if (s.Root?.World != world) continue;
                if (s.VCamCamera != null && !s.VCamCamera.IsDestroyed) lastVCamIdx = k;
                if (s.VMicListener != null && !s.VMicListener.IsDestroyed) lastVMicIdx = k;
            }

            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];

                if (session.Cleaned)
                {
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root == null || session.Root.IsDestroyed ||
                    session.Texture == null || session.Texture.IsDestroyed)
                {
                    Msg($"[UpdateLoop] Session {i} root/texture destroyed, cleaning up (root={session.Root != null} rootDestroyed={session.Root?.IsDestroyed} tex={session.Texture != null} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId})");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed) { vtp.URL.Value = null; vtp.Stop(); }
                    session.VideoTexture = null;
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root.World != world) continue;
                if (session.UpdateInProgress) continue;

                session.TimeSinceValidCheck += dt;
                if (session.TimeSinceValidCheck >= 0.5)
                {
                    session.TimeSinceValidCheck = 0;
                    session.LastValidState = session.Streamer == null || session.Streamer.IsValid;
                }
                if (session.Streamer != null && !session.LastValidState)
                {
                    Msg($"[UpdateLoop] Window closed (IsValid=false), destroying viewer");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg("[UpdateLoop] Disconnecting VideoTextureProvider before cleanup");
                        vtp.URL.Value = null;
                        vtp.Stop();
                    }
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                    var rootToDestroy = session.Root;
                    world.RunInUpdates(10, () =>
                    {
                        Msg("[UpdateLoop] Deferred destroy executing");
                        if (rootToDestroy != null && !rootToDestroy.IsDestroyed)
                        {
                            rootToDestroy.DestroyChildren();
                            rootToDestroy.Destroy();
                        }
                        Msg("[UpdateLoop] Deferred destroy complete");
                    });
                    continue;
                }

                while (_windowEvents.TryDequeue(out var evt))
                {
                    if (evt.Session.Cleaned || evt.Session.Root == null || evt.Session.Root.IsDestroyed) continue;
                    if (evt.Session.Root.World != world) continue;

                    switch (evt.EventType)
                    {
                        case WindowEventType.TitleChanged:
                            evt.Session.LastTitle = evt.Title;
                            if (evt.Session.TitleText != null && !evt.Session.TitleText.IsDestroyed)
                                evt.Session.TitleText.Text.Value = evt.Title;
                            if (evt.Session.Root != null && !evt.Session.Root.IsDestroyed)
                                evt.Session.Root.Name = $"Desktop: {evt.Title}";
                            break;

                        case WindowEventType.NewChild:
                            Msg($"[ChildWindow] Detected new popup: hwnd={evt.ChildHwnd} title='{evt.Title}'");
                            SpawnChildWindow(evt.Session, evt.ChildHwnd, evt.Title);
                            break;

                        case WindowEventType.ChildClosed:
                        {
                            var child = evt.Session.ChildSessions.Find(c => c.Hwnd == evt.ChildHwnd);
                            if (child != null)
                            {
                                Msg($"[ChildWindow] Popup closed: hwnd={child.Hwnd}");
                                evt.Session.TrackedChildHwnds.Remove(child.Hwnd);
                                child.ParentSession = null;
                                evt.Session.ChildSessions.Remove(child);
                                {
                                    var cvtp = child.VideoTexture;
                                    if (cvtp != null && !cvtp.IsDestroyed) { cvtp.URL.Value = null; cvtp.Stop(); }
                                    child.VideoTexture = null;
                                    var cRoot = child.Root;
                                    world.RunInUpdates(10, () =>
                                    {
                                        if (cRoot != null && !cRoot.IsDestroyed) cRoot.Destroy();
                                    });
                                }
                                CleanupSession(child);
                            }
                            break;
                        }
                    }
                }

                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");

                    // For window captures (magic DisplayIndex), the renderer may not have
                    // registered the UWC source yet. Re-send the Update message so the
                    // renderer's TryGetDisplayTexture is retried.
                    if (session.CaptureSlot >= 0 && _updateCount % 5 == 0)
                    {
                        RetriggerDesktopTexture(session.Texture);
                    }
                    continue;
                }



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

                        // Renderer-side UWC handles texture resize automatically.
                        // We only update game-side UI layout.
                        if (session.Canvas != null && !session.Canvas.IsDestroyed)
                            session.Canvas.Size.Value = new float2(sw, sh);

                        session.OnResize?.Invoke(sw, sh);
                        session.PendingResizeW = sw;
                        session.PendingResizeH = sh;
                        session.ResizeDebounceUntil = world.Time.WorldTime + 0.5;
                        Msg($"[UpdateLoop] UI resized to {sw}x{sh}");
                        continue;
                    }
                }

                if (session.ResizeDebounceUntil > 0 && world.Time.WorldTime >= session.ResizeDebounceUntil)
                {
                    session.ResizeDebounceUntil = 0;
                    int rw = session.PendingResizeW;
                    int rh = session.PendingResizeH;
                    Msg($"[UpdateLoop] Resize debounce expired, reiniting encoder for {rw}x{rh}");

                    if (session.Streamer != null) session.Streamer.OnGpuFrame = null;

                    var oldStreamId = session.StreamId;
                    int newStreamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                    var newEncoder = StreamServer?.CreateEncoder(newStreamId);
                    session.StreamId = newStreamId;

                    FfmpegEncoder oldEncoder = null;
                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(session.Hwnd, out var oldShared))
                            oldEncoder = oldShared.Encoder;
                    }

                    var oldStreamer = session.Streamer;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            oldEncoder?.Stop();
                            oldStreamer?.FlushD3dContext();
                            StreamServer?.StopEncoder(oldStreamId);
                        }
                        catch (Exception ex) { Msg($"[Resize:BG] Old encoder cleanup error: {ex.Message}"); }
                    });

                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(session.Hwnd, out var shared))
                        {
                            shared.StreamId = newStreamId;
                            shared.Encoder = newEncoder;
                        }
                    }

                    ConnectEncoder(session, newEncoder);

                    if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && TunnelUrl != null)
                    {
                        var newUrl = new Uri($"{TunnelUrl}/stream/{newStreamId}");
                        Msg($"[UpdateLoop] Updating VTP URL: {session.VideoTexture.URL.Value} -> {newUrl}");
                        session.VideoTexture.URL.Value = newUrl;
                    }

                    Msg($"[UpdateLoop] New encoder {newStreamId} created and connected for {rw}x{rh}");
                }

                if (VCam != null && VCam.ConsumerConnected && !VCam.ManuallyDisabled &&
                    session.VCamCamera != null && !session.VCamCamera.IsDestroyed &&
                    !session.VCamRenderPending)
                {
                    if (i == lastVCamIdx)
                    {
                        session.VCamRenderPending = true;
                        var vcam = session.VCamCamera;
                        var vcamRef = VCam;
                        vcam.RenderToBitmap(new int2(1280, 720)).ContinueWith(task =>
                        {
                            session.VCamRenderPending = false;
                            if (task.IsFaulted || task.Result == null) return;
                            var bmp = task.Result;
                            if (bmp.RawData.Length == 0) return;
                            if (vcamRef._logNextFrame)
                            {
                                vcamRef._logNextFrame = false;
                                Log.Msg($"[VirtualCamera] Bitmap: {bmp.Size.x}x{bmp.Size.y} format={bmp.Format} bpp={bmp.BitsPerPixel} profile={bmp.Profile}");
                            }
                            vcamRef.SendFrame(bmp.RawData, bmp.Size.x, bmp.Size.y, bmp.Format);
                        });

                    }
                }

                if (session.VCamIndicator != null && !session.VCamIndicator.IsDestroyed && VCam != null)
                {
                    bool lit = VCam.ConsumerConnected && !VCam.ManuallyDisabled;
                    if (lit != session.VCamLastLitState)
                    {
                        session.VCamLastLitState = lit;
                        session.VCamIndicator.Tint.Value = lit
                            ? new colorX(0.8f, 0.1f, 0.1f, 1f)
                            : new colorX(0.05f, 0.05f, 0.05f, 1f);
                    }
                }

                if ((VMic == null || !VMic.IsActive) && VBCableSetup.IsInstalled() &&
                    session.VMicListener != null && !session.VMicListener.IsDestroyed)
                {
                    if (i == lastVMicIdx)
                    {
                        VMic = new VirtualMic();
                        if (VMic.Start())
                        {
                            var listener = session.VMicListener;
                            var mic = VMic;
                            var simulator = session.Root.Engine.AudioSystem.Simulator;
                            if (listener != null && simulator != null)
                            {
                                int frameSize = simulator.FrameSize;
                                var stereoBuf = new Elements.Assets.StereoSample[frameSize];
                                var floatBuf = new float[frameSize * 2];
                                simulator.RenderFinished += (sim) =>
                                {
                                    if (mic.Muted || listener.IsDestroyed) return;
                                    var span = stereoBuf.AsSpan(0, sim.FrameSize);
                                    span.Clear();
                                    listener.Read(span, sim);
                                    for (int s = 0; s < span.Length; s++)
                                    {
                                        floatBuf[s * 2] = span[s].left;
                                        floatBuf[s * 2 + 1] = span[s].right;
                                    }
                                    mic.WriteGameAudio(floatBuf.AsSpan(0, span.Length * 2));
                                };
                                Msg($"[VirtualMic] Hooked AudioListener (frameSize={frameSize})");
                            }
                        }
                        else
                        { VMic.Dispose(); VMic = null; }
                    }
                }
                if (VMic != null)
                    VMic.Muted = session.VMicMuted;

                Perf.IncrementFrames();
            }
        }
        catch (Exception ex)
        {
            Msg($"ERROR in UpdateLoop: {ex}");
        }

        bool hasSessionsInWorld = false;
        for (int i = 0; i < ActiveSessions.Count; i++)
        {
            if (ActiveSessions[i].Root?.World == world) { hasSessionsInWorld = true; break; }
        }
        if (hasSessionsInWorld)
        {
            world.RunInUpdates(1, () => UpdateLoop(world));
        }
        else
        {
            Msg("[UpdateLoop] No sessions left for this world, stopping loop");
            _scheduledWorlds.Remove(world);
        }
    }

    private static string FindCloudflared()
    {
        var modDir = System.IO.Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        string[] candidates = {
            System.IO.Path.Combine(modDir, "..", "cloudflared", "cloudflared.exe"),
            System.IO.Path.Combine(modDir, "cloudflared", "cloudflared.exe"),
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared", "cloudflared.exe"),
            "cloudflared"
        };
        foreach (var c in candidates)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "version",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                });
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) { Msg($"[Tunnel] Found cloudflared: {c}"); return c; }
            }
            catch (Exception ex) { Msg($"[Tunnel] cloudflared probe failed for {c}: {ex.Message}"); }
        }
        return null;
    }

    private static void KillTunnel()
    {
        try { if (_tunnelProcess != null && !_tunnelProcess.HasExited) _tunnelProcess.Kill(); }
        catch (Exception ex) { Msg($"[Tunnel] Kill failed: {ex.Message}"); }
        _tunnelProcess = null;
    }

    private static void OnTunnelError(string data)
    {
    }

    private static void UpdateSessionTunnelUrls()
    {
        if (TunnelUrl == null) return;
        foreach (var session in ActiveSessions)
        {
            if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && session.StreamId > 0)
            {
                var newUrl = new Uri($"{TunnelUrl}/stream/{session.StreamId}");
                var vtp = session.VideoTexture;
                vtp.World.RunInUpdates(0, () =>
                {
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg($"[Tunnel] Updating session VTP: {vtp.URL.Value} -> {newUrl}");
                        vtp.URL.Value = newUrl;
                    }
                });
            }
        }
    }

    private static void RestartTunnel()
    {
        if (_tunnelRestarting) return;
        _tunnelRestarting = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Msg("[Tunnel] === RESTART ===");
                KillTunnel();
                TunnelUrl = null;
                System.Threading.Thread.Sleep(2000);
                StartTunnel();
            }
            finally { _tunnelRestarting = false; }
        });
    }

    private static void StartTunnel()
    {
        try
        {
            if (_cfPath == null)
            {
                _cfPath = FindCloudflared();
                if (_cfPath == null)
                {
                    Msg("[Tunnel] cloudflared not found — tunnel unavailable");
                    return;
                }
            }
            Msg($"[Tunnel] Starting cloudflared tunnel: {_cfPath}");
            var psi = new ProcessStartInfo
            {
                FileName = _cfPath,
                Arguments = $"tunnel --config NUL" +
                    $" --url http://localhost:{STREAM_PORT}" +
                    $" --proxy-keepalive-timeout 5m" +
                    $" --proxy-keepalive-connections 100" +
                    $" --proxy-tcp-keepalive 15s" +
                    $" --proxy-connect-timeout 30s" +
                    $" --no-chunked-encoding" +
                    $" --compression-quality 0" +
                    $" --grace-period 30s" +
                    $" --no-autoupdate" +
                    $" --edge-ip-version 4",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _tunnelProcess = Process.Start(psi);
            if (_tunnelProcess == null) { Msg("[Tunnel] Failed to start cloudflared"); return; }
            var proc = _tunnelProcess;
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                Msg($"[Tunnel] cloudflared exited (code={proc.ExitCode}), restarting");
                RestartTunnel();
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel/stderr] {e.Data}");
                if (e.Data.Contains("https://") && e.Data.Contains(".trycloudflare.com"))
                {
                    int idx = e.Data.IndexOf("https://");
                    string url = e.Data.Substring(idx).Trim();
                    int space = url.IndexOf(' ');
                    if (space > 0) url = url.Substring(0, space);
                    try { url = new Uri(url).GetLeftPart(UriPartial.Authority); } catch (Exception ex) { Msg($"[Tunnel] URL parse error: {ex.Message}"); }
                    string oldUrl = TunnelUrl;
                    TunnelUrl = url;
                    Msg($"[Tunnel] PUBLIC URL: {TunnelUrl}");
                    if (oldUrl != url)
                        UpdateSessionTunnelUrls();
                }
                OnTunnelError(e.Data);
            };
            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel/stdout] {e.Data}");
            };
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            Msg($"[Tunnel] Error: {ex.Message}");
        }
    }

    internal new static void Msg(string msg) => Log.Msg(msg);
    internal new static void Error(string msg) => Log.Error(msg);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnhandledExceptionFilterDelegate(IntPtr exceptionPointers);

    private static UnhandledExceptionFilterDelegate _nativeCrashDelegate;
    private static IntPtr _previousFilter;

    private static void InstallNativeCrashHandler()
    {
        try
        {
            _nativeCrashDelegate = NativeCrashFilter;
            IntPtr fp = Marshal.GetFunctionPointerForDelegate(_nativeCrashDelegate);
            _previousFilter = SetUnhandledExceptionFilter(fp);
            Log.Msg("[NativeCrash] Handler installed");
        }
        catch (Exception ex)
        {
            Log.Msg($"[NativeCrash] Failed to install handler: {ex.Message}");
        }
    }

    private static int NativeCrashFilter(IntPtr exceptionPointersPtr)
    {
        try
        {
            IntPtr recordPtr = Marshal.ReadIntPtr(exceptionPointersPtr, 0);
            uint code = (uint)Marshal.ReadInt32(recordPtr, 0);
            IntPtr address = Marshal.ReadIntPtr(recordPtr, IntPtr.Size == 8 ? 24 : 12);

            string msg = $"[NativeCrash] FATAL: code=0x{code:X8} addr=0x{address:X}\n";

            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule mod in proc.Modules)
                {
                    long modBase = mod.BaseAddress.ToInt64();
                    long modEnd = modBase + mod.ModuleMemorySize;
                    if (address.ToInt64() >= modBase && address.ToInt64() < modEnd)
                    {
                        long offset = address.ToInt64() - modBase;
                        msg += $"[NativeCrash] Faulting module: {mod.ModuleName}+0x{offset:X} ({mod.FileName})\n";
                        break;
                    }
                }
            }
            catch { }

            try
            {
                msg += $"[NativeCrash] Managed stack:\n{Environment.StackTrace}\n";
            }
            catch { }

            Log.Msg(msg);
        }
        catch
        {
            try { Log.Msg("[NativeCrash] FATAL: crash handler failed to log details"); } catch { }
        }

        return 0;
    }

}

[HarmonyPatch(typeof(InteractionHandler), nameof(InteractionHandler.BeforeInputUpdate))]
static class LocomotionSuppressionPatch
{
    private static readonly FieldInfo _inputsField = typeof(InteractionHandler)
        .GetField("_inputs", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Postfix(InteractionHandler __instance)
    {
        try
        {
            if (DesktopBuddyMod.DesktopCanvasIds.Count == 0) return;
            var touchable = __instance.Laser?.CurrentTouchable;
            if (touchable == null) return;

            if (touchable is Canvas canvas && DesktopBuddyMod.DesktopCanvasIds.Contains(canvas.ReferenceID))
            {
                if (_inputsField?.GetValue(__instance) is InteractionHandlerInputs inputs)
                    inputs.Axis.RegisterBlocks = true;
            }
        }
        catch
        {
        }
    }
}

static class KeyMapper
{
    public static readonly Dictionary<Key, ushort> KeyToVK = new()
    {
        { Key.Backspace, 0x08 }, { Key.Tab, 0x09 }, { Key.Return, 0x0D },
        { Key.Escape, 0x1B }, { Key.Space, 0x20 }, { Key.Delete, 0x2E },
        { Key.UpArrow, 0x26 }, { Key.DownArrow, 0x28 },
        { Key.LeftArrow, 0x25 }, { Key.RightArrow, 0x27 },
        { Key.Home, 0x24 }, { Key.End, 0x23 },
        { Key.PageUp, 0x21 }, { Key.PageDown, 0x22 },
        { Key.LeftShift, 0xA0 }, { Key.RightShift, 0xA1 },
        { Key.LeftControl, 0xA2 }, { Key.RightControl, 0xA3 },
        { Key.LeftAlt, 0xA4 }, { Key.RightAlt, 0xA5 },
        { Key.LeftWindows, 0x5B }, { Key.RightWindows, 0x5C },
        { Key.F1, 0x70 }, { Key.F2, 0x71 }, { Key.F3, 0x72 }, { Key.F4, 0x73 },
        { Key.F5, 0x74 }, { Key.F6, 0x75 }, { Key.F7, 0x76 }, { Key.F8, 0x77 },
        { Key.F9, 0x78 }, { Key.F10, 0x79 }, { Key.F11, 0x7A }, { Key.F12, 0x7B },
    };

    public static bool IsModifier(Key key) =>
        key == Key.LeftShift || key == Key.RightShift ||
        key == Key.LeftControl || key == Key.RightControl ||
        key == Key.LeftAlt || key == Key.RightAlt;
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.SimulatePress))]
static class SimulatePressPatch
{
    static bool Prefix(Key key, World origin)
    {
        for (int i = 0; i < DesktopBuddyMod.ActiveSessions.Count; i++)
        {
            var s = DesktopBuddyMod.ActiveSessions[i];
            if (s.Root?.World == origin && s.KeyboardSource != null && !s.KeyboardSource.IsDestroyed)
            {
                s.KeyboardSource.SendKey(key);
                return false;
            }
        }
        return true;
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.TypeAppend))]
static class TypeAppendPatch
{
    static bool Prefix(string typeDelta, World origin)
    {
        for (int i = 0; i < DesktopBuddyMod.ActiveSessions.Count; i++)
        {
            var s = DesktopBuddyMod.ActiveSessions[i];
            if (s.Root?.World == origin && s.KeyboardSource != null && !s.KeyboardSource.IsDestroyed)
            {
                s.KeyboardSource.TypeString(typeDelta);
                return false;
            }
        }
        return true;
    }
}

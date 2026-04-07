using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;
using SkyFrost.Base;
using Key = Renderite.Shared.Key;
using FrooxEngine.ProtoFlux;

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

    internal static readonly List<DesktopSession> ActiveSessions = new();
    private static int _nextStreamId;

    // Track our desktop canvases so the locomotion patch can identify them
    internal static readonly HashSet<RefID> DesktopCanvasIds = new();

    // Shared stream registry: multiple panels for the same hwnd share one encoder
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
    private const int STREAM_PORT = 48080;
    internal static string? TunnelUrl; // Set by cloudflared if available
    private static Process _tunnelProcess;
    internal static readonly PerfTimer Perf = new();

    // Update check
    private static string _latestVersion;
    private static bool _updateShown;

    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Config!.Save(true);

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();

        AudioCapture.LogHandler = Msg;

        // Start streaming server for remote user support
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

        // Start cloudflared tunnel in background (if available)
        if (StreamServer != null)
        {
            System.Threading.Tasks.Task.Run(() => StartTunnel());
        }

        // Kill cloudflared when the process exits
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { if (_tunnelProcess != null && !_tunnelProcess.HasExited) _tunnelProcess.Kill(); }
            catch (Exception ex) { Msg($"[Tunnel] Kill failed: {ex.Message}"); }
        };

        Msg("DesktopBuddy initialized!");
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
                var tag = match.Groups[1].Value; // e.g. "build-aaccf2a"
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

        // Auto-dismiss after 15 seconds
        root.World.RunInUpdates(15 * 60, () =>
        {
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        });
    }

    internal static void SpawnStreaming(World world, IntPtr hwnd, string title, IntPtr monitorHandle = default)
    {
        try
        {
            Msg($"[SpawnStreaming] Starting for '{title}' hwnd={hwnd}");
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

            StartStreaming(root, hwnd, title, monitorHandle: monitorHandle);
        }
        catch (Exception ex)
        {
            Msg($"ERROR in SpawnStreaming: {ex}");
        }
    }

    private static void StartStreaming(Slot root, IntPtr hwnd, string title, bool isChild = false, IntPtr monitorHandle = default)
    {
        Msg($"[StartStreaming] Window: {title} (hwnd={hwnd})");

        // Restore if minimized before attempting capture
        WindowInput.RestoreIfMinimized(hwnd);

        var streamer = new DesktopStreamer(hwnd, monitorHandle);
        if (!streamer.TryInitialCapture())
        {
            Msg($"[StartStreaming] Failed initial capture for: {title}");
            streamer.Dispose();
            return;
        }

        int fps = Config!.GetValue(FrameRate);
        int w = streamer.Width;
        int h = streamer.Height;
        Grabbable grabbable = null; // forward declaration — assigned after UI setup

        Msg($"[StartStreaming] Window size: {w}x{h}, target {fps}fps");

        // Add collider to root encompassing all canvases
        float canvasScale = 0.0005f;
        float worldHalfH = h / 2f * canvasScale;
        float worldHalfW = w / 2f * canvasScale;
        var collider = root.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(w * canvasScale, h * canvasScale, 0.001f);
        collider.Offset.Value = float3.Zero;
        Msg("[StartStreaming] Collider added to root");

        // Display slot holds the Canvas — separate from root so keyboard etc. aren't nested inside Canvas
        var displaySlot = root.AddSlot("Display");
        Msg("[StartStreaming] Display slot created");

        // Per-user visibility: preview only visible to the spawner, hidden from others
        var displayVis = displaySlot.AttachComponent<ValueUserOverride<bool>>();
        displayVis.Target.Target = displaySlot.ActiveSelf_Field;
        displayVis.Default.Value = false; // Other users: hidden
        displayVis.CreateOverrideOnWrite.Value = false;
        displayVis.SetOverride(root.World.LocalUser, true); // Spawner: visible
        Msg("[StartStreaming] Per-user visibility set");

        // SolidColorTexture as our procedural texture host
        var texSlot = displaySlot.AddSlot("Texture");
        var procTex = texSlot.AttachComponent<SolidColorTexture>();
        procTex.Size.Value = new int2(w, h);
        procTex.Format.Value = Renderite.Shared.TextureFormat.RGBA32;
        procTex.Mipmaps.Value = false;
        procTex.FilterMode.Value = Renderite.Shared.TextureFilterMode.Bilinear;
        Msg("[StartStreaming] Texture component created");

        // Canvas with RawImage pointing at the texture — on displaySlot, NOT root
        var ui = new UIBuilder(displaySlot, w, h, canvasScale);

        var displayBg = ui.Image(new colorX(0f, 0f, 0f, 1f));
        ui.NestInto(displayBg.RectTransform);

        var rawImage = ui.RawImage(procTex);
        Msg("[StartStreaming] Canvas + RawImage created");

        var mat = displaySlot.AttachComponent<UI_UnlitMaterial>();
        mat.BlendMode.Value = BlendMode.Alpha;
        mat.ZWrite.Value = ZWrite.On;
        mat.OffsetUnits.Value = 100f;
        rawImage.Material.Target = mat;

        // Attach Button to the RawImage's slot for touch input
        var btn = rawImage.Slot.AttachComponent<Button>();
        btn.PassThroughHorizontalMovement.Value = false;
        btn.PassThroughVerticalMovement.Value = false;
        Msg("[StartStreaming] Button attached");

        // Get process ID for child window tracking
        WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
        Msg($"[StartStreaming] Process ID: {processId}");

        // Create session early so event handlers can reference it
        var session = new DesktopSession
        {
            Streamer = streamer,
            Texture = procTex,
            Canvas = ui.Canvas,
            Root = root,
            TargetInterval = 1.0 / fps,
            Hwnd = hwnd,
            ProcessId = processId,
        };
        ActiveSessions.Add(session);
        DesktopCanvasIds.Add(ui.Canvas.ReferenceID);
        Msg($"[StartStreaming] Registered canvas {ui.Canvas.ReferenceID} for locomotion suppression");

        // Snapshot existing child windows so we only track NEW popups, not pre-existing ones
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

        // --- Input event handlers ---

        // Helper: check if this source should control the mouse hover
        bool IsActiveSource(Component source)
        {
            if (session.LastActiveSource == null || session.LastActiveSource.IsDestroyed)
                return true;
            return source == session.LastActiveSource;
        }

        void ClaimSource(Component source, string reason)
        {
            if (source != session.LastActiveSource)
            {
                // Source claimed
                session.LastActiveSource = source;
            }
        }

        // Find the InteractionHandler from a button event source.
        // The source is a RelayTouchSource on the "Laser" slot. InteractionLaser._handler
        // points to InteractionHandler but is protected. Instead, get the InteractionLaser
        // on the same slot, then read _handler via reflection.
        var _handlerField = typeof(InteractionLaser)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);

        InteractionHandler FindHandler(Component source)
        {
            if (source == null) return null;
            // The source (RelayTouchSource) is on the Laser slot alongside InteractionLaser
            var laser = source.Slot?.GetComponent<InteractionLaser>();
            if (laser != null && _handlerField != null)
            {
                var handlerRef = _handlerField.GetValue(laser) as SyncRef<InteractionHandler>;
                return handlerRef?.Target;
            }
            // Fallback: walk parents (works for non-laser sources)
            return source.Slot?.GetComponentInParents<InteractionHandler>();
        }

        // Get touch ID from source: left hand=0, right hand=1, fallback=0
        uint GetTouchId(Component source)
        {
            var handler = FindHandler(source);
            if (handler != null && handler.Side.Value == Renderite.Shared.Chirality.Right)
                return 1;
            return 0;
        }

        // Hover enter: focus window only if this is the active source
        btn.LocalHoverEnter += (IButton b, ButtonEventData data) =>
        {
            // HoverEnter
        };

        // Touch down — replaces mouse click for drag-to-scroll, hold-to-right-click, multi-touch
        btn.LocalPressed += (IButton b, ButtonEventData data) =>
        {
            if (grabbable.IsGrabbed) return; // no input while grabbed
            ClaimSource(data.source, "touch");
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            // Touch down
            WindowInput.FocusWindow(hwnd);
            WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Touch move (drag)
        btn.LocalPressing += (IButton b, ButtonEventData data) =>
        {
            if (grabbable.IsGrabbed) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Touch up (release)
        btn.LocalReleased += (IButton b, ButtonEventData data) =>
        {
            if (grabbable.IsGrabbed) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            // Touch up
            WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Hover: move cursor + handle scroll (mouse wheel + VR joystick)
        btn.LocalHoverStay += (IButton b, ButtonEventData data) =>
        {
            float hu = data.normalizedPressPoint.x;
            float hv = 1f - data.normalizedPressPoint.y;

            // Only move mouse for the active source (prevents two VR hands fighting)
            if (IsActiveSource(data.source))
            {
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height);
            }

            // --- Mouse wheel scroll (desktop mode) ---
            var mouse = root.World.InputInterface.Mouse;
            if (mouse != null)
            {
                float scrollY = mouse.ScrollWheelDelta.Value.y;
                if (scrollY != 0)
                {
                    ClaimSource(data.source, "mouse-scroll");
                    WindowInput.FocusWindow(hwnd);
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta);
                }
            }

            // --- VR joystick scroll (all controllers) ---
            try
            {
                var handler = FindHandler(data.source);
                if (handler == null && !session.JoystickDiagLogged)
                {
                    session.JoystickDiagLogged = true;
                    // Scroll diag: handler null
                }
                if (handler != null)
                {
                    var side = handler.Side.Value;
                    var controller = root.World.InputInterface.GetControllerNode(side);
                    if (controller == null && !session.JoystickDiagLogged)
                    {
                        session.JoystickDiagLogged = true;
                        // Scroll diag: controller null
                    }
                    if (controller != null)
                    {
                        float axisY = controller.Axis.Value.y;
                        if (!session.JoystickDiagLogged)
                        {
                            session.JoystickDiagLogged = true;
                            // Scroll diag: first read
                        }
                        if (Math.Abs(axisY) > 0.15f)
                        {
                            double tick = root.World.Time.WorldTime;
                            bool sameDir = session.LastScrollSign == 0 || Math.Sign(axisY) == session.LastScrollSign;
                            // Only scroll once per engine tick + suppress jitter direction reversals
                            if (tick != session.LastScrollTick && sameDir)
                            {
                                session.LastScrollTick = tick;
                                session.LastScrollSign = Math.Sign(axisY);
                                ClaimSource(data.source, $"joystick-scroll-{side}");
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
            }
            catch (Exception ex)
            {
                if (!session.JoystickDiagLogged)
                {
                    session.JoystickDiagLogged = true;
                    Msg($"[Scroll] Joystick EXCEPTION: {ex}");
                }
            }
        };

        // --- Unified top bar: avatar + name + toggle + collapsible tools ---
        // Explicit sizing with SmoothValue-animated width transition.
        float barH = 64f;
        float barMarginTop = 10f * canvasScale;
        float barPad = 8f;
        float barGap = 8f;
        float avatarW = 48f;
        float toggleW = 36f;
        // Collapsed/expanded widths computed after username is known (see below)

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
        // Rounded corners via nine-sliced disc texture
        var roundedSprite = barSlot.AttachComponent<SpriteProvider>();
        roundedSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        roundedSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        roundedSprite.FixedSize.Value = 16f;
        barBg.Sprite.Target = roundedSprite;
        barBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBg.Tint.Value = new colorX(0.1f, 0.1f, 0.12f, 1f);

        // Mask clips children to the bar bounds — icons reveal as width animates
        var barMask = barBg.Slot.AttachComponent<Mask>();
        barMask.ShowMaskGraphic.Value = true;
        barUi.NestInto(barBg.RectTransform);
        var barLayout = barUi.HorizontalLayout(8f, padding: 8f, childAlignment: Alignment.MiddleLeft);
        barLayout.ForceExpandWidth.Value = false; // don't stretch children to fill canvas
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = 1f;

        var localUser = root.World.LocalUser;

        // --- Avatar ---
        barUi.Style.MinWidth = 48f;
        barUi.Style.PreferredWidth = 48f;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var imageSpaceSlot = barUi.Empty("Image Space");
        var imgMask = imageSpaceSlot.AttachComponent<Mask>();
        var imgMaskImage = imageSpaceSlot.GetComponent<Image>();
        // Rounded square mask using nine-sliced circle (same technique as the bar bg)
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
        barUi.NestOut(); // out of Image Space

        // --- Username (fixed width based on name length) ---
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

        // Compute bar widths
        float barCollapsedW = barPad * 2f + avatarW + barGap + nameW + barGap + toggleW;
        // 7 buttons(30ea) + 6 gaps(6ea) + separator(1+6+6) + volIcon(24+6) + slider(100)
        float expandContentW = 7f * 30f + 6f * 6f + 13f + 30f + 100f;
        float barExpandedW = barCollapsedW + barGap + expandContentW;

        // Helper: style a toolbar icon button — light gray text, transparent bg with hover
        void StyleButton(Button btn)
        {
            // Set text color on both Text (UIX) and TextRenderer to be safe
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
            // ColorDrivers[0] drives Image.Tint (button background), not text
            if (btn.ColorDrivers.Count > 0)
            {
                var cd = btn.ColorDrivers[0];
                cd.NormalColor.Value = colorX.Clear;
                cd.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.15f);
                cd.PressColor.Value = new colorX(1f, 1f, 1f, 0.08f);
            }
        }

        var darkBtn = new colorX(0.22f, 0.22f, 0.28f, 1f);
        var accentBtn = new colorX(0.25f, 0.35f, 0.55f, 1f);

        // --- Toggle button (hamburger) ---
        barUi.Style.MinWidth = 36f;
        barUi.Style.PreferredWidth = 36f;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;
        var toggleBtn = barUi.Button("≡");
        StyleButton(toggleBtn);
        var toggleText = toggleBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (toggleText != null) toggleText.Size.Value = 42f;

        // --- Expandable panel ---
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

        // Tool buttons (compact)
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

        // Separator
        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        ep.Image(new colorX(0.4f, 0.4f, 0.45f, 0.4f));

        // Volume icon + slider
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
        var volSliderOverride = volSlider.Slot.AttachComponent<ValueUserOverride<float>>();
        volSliderOverride.Target.Target = volSlider.Value;
        volSliderOverride.Default.Value = 0f;
        volSliderOverride.CreateOverrideOnWrite.Value = true;

        var winVolRow = ep.Empty("WinVol");
        var winVolUi = new UIBuilder(winVolRow);
        winVolUi.Style.FlexibleWidth = 1f;
        winVolUi.Style.FlexibleHeight = 1f;
        var winVolSlider = winVolUi.Slider<float>(20f, 1f, 0f, 1f, false);

        var streamVolVis = streamVolRow.AttachComponent<ValueUserOverride<bool>>();
        streamVolVis.Target.Target = streamVolRow.ActiveSelf_Field;
        streamVolVis.Default.Value = true;
        streamVolVis.CreateOverrideOnWrite.Value = false;
        streamVolVis.SetOverride(root.World.LocalUser, false);

        var winVolVis = winVolRow.AttachComponent<ValueUserOverride<bool>>();
        winVolVis.Target.Target = winVolRow.ActiveSelf_Field;
        winVolVis.Default.Value = false;
        winVolVis.CreateOverrideOnWrite.Value = false;
        winVolVis.SetOverride(root.World.LocalUser, true);

        // --- Animated collapse/expand ---
        // SmoothValue<float> drives the canvas width smoothly (synced to all users).
        // A per-frame loop reads the driven width and repositions to keep left edge pinned.
        var widthField = barSlot.AttachComponent<ValueField<float>>();
        widthField.Value.Value = barCollapsedW;
        var widthSmooth = barSlot.AttachComponent<SmoothValue<float>>();
        widthSmooth.Speed.Value = 10f;
        widthSmooth.TargetValue.Value = barCollapsedW;
        widthSmooth.Value.Target = widthField.Value;
        widthSmooth.WriteBack.Value = false;

        bool barExpanded = false;
        // Panel always active — Mask on barBg clips overflow so icons reveal with width animation
        float barYPos = worldHalfH + barH / 2f * canvasScale + barMarginTop;

        // Per-frame: apply driven width to canvas and reposition so left edge stays pinned
        void BarUpdateLoop()
        {
            if (root.IsDestroyed || barSlot.IsDestroyed) return;
            float cw = widthField.Value.Value;
            barCanvas.Size.Value = new float2(cw, barH);
            barSlot.LocalPosition = new float3(
                -worldHalfW + cw / 2f * canvasScale,
                barYPos, 0f);
            root.World.RunInUpdates(1, BarUpdateLoop);
        }
        // Set initial position
        barCanvas.Size.Value = new float2(barCollapsedW, barH);
        barSlot.LocalPosition = new float3(
            -worldHalfW + barCollapsedW / 2f * canvasScale,
            barYPos, 0f);
        root.World.RunInUpdates(1, BarUpdateLoop);

        toggleBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            barExpanded = !barExpanded;
            widthSmooth.TargetValue.Value = barExpanded ? barExpandedW : barCollapsedW;
            if (toggleText != null)
                toggleText.Text.Value = barExpanded ? "✕" : "≡";
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
                    // Reset to default position/rotation in case user dragged it
                    keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);
                    keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
                    keyboardSlot.LocalScale = float3.One;
                }
                return;
            }
            Msg("[Keyboard] Spawning virtual keyboard (favorite or fallback)");
            keyboardSlot = root.AddSlot("Virtual Keyboard");
            // Position just below the keyboard button, angled up toward user
            keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);
            keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
            // Do NOT set LocalScale — the cloud keyboard has its own natural size
            // SpawnEntity loads the user's favorited keyboard from cloud, falls back to SimpleVirtualKeyboard
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

        // Test Stream button — toggles the stream overlay visibility for the local user
        Slot streamSlotRef = null; // Will be set when stream is created below
        bool streamTestMode = false;
        ValueUserOverride<bool> streamVisRef = null; // Set when stream is created
        VideoTextureProvider videoTexRef = null; // Set when stream is created
        // volOverrideRef removed — stream volume driven by ValueDriver from volSlider
        var testActiveColor = new colorX(0.2f, 0.45f, 0.25f, 1f); // Green when active
        testStreamBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[TestStream] Button pressed");
            if (streamVisRef != null && !streamVisRef.IsDestroyed)
            {
                streamTestMode = !streamTestMode;
                // Toggle: show stream to spawner (and hide preview), or restore normal
                streamVisRef.SetOverride(root.World.LocalUser, streamTestMode);
                var displayVisComp = displaySlot.GetComponent<ValueUserOverride<bool>>();
                if (displayVisComp != null)
                    displayVisComp.SetOverride(root.World.LocalUser, !streamTestMode);
                // Mute/unmute spawner's stream audio when previewing
                volSliderOverride.SetOverride(root.World.LocalUser, streamTestMode ? 1f : 0f);
                // Update button color to show active state
                var img = testStreamBtn.Slot.GetComponent<Image>();
                if (img != null) img.Tint.Value = streamTestMode ? testActiveColor : colorX.Clear;
                Msg($"[TestStream] Test mode: {streamTestMode} (stream={streamTestMode}, preview={!streamTestMode})");
            }
            else
            {
                Msg("[TestStream] No stream available");
            }
        };

        // Resync button — forces libVLC to fully disconnect and reconnect
        resyncBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Resync] Button pressed");
            if (videoTexRef != null && !videoTexRef.IsDestroyed)
            {
                var savedUrl = videoTexRef.URL.Value;
                Msg($"[Resync] Forcing full reload: {savedUrl}");
                videoTexRef.URL.Value = null;
                // Wait several frames so FreeAsset fully tears down the player
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

        // Anchor button — parents/unparents the viewer to the local user
        bool isAnchored = false;
        var anchorActiveColor = new colorX(0.2f, 0.45f, 0.25f, 1f); // Green when active
        anchorBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Anchor] Button pressed");
            var localUser = root.World.LocalUser;
            if (localUser?.Root == null) return;
            if (!isAnchored)
            {
                var pos = root.GlobalPosition;
                var rot = root.GlobalRotation;
                root.SetParent(localUser.Root.Slot, keepGlobalTransform: true);
                Msg($"[Anchor] Anchored to user");
                isAnchored = true;
            }
            else
            {
                var pos = root.GlobalPosition;
                var rot = root.GlobalRotation;
                root.SetParent(root.World.RootSlot, keepGlobalTransform: true);
                Msg($"[Anchor] Unanchored to world");
                isAnchored = false;
            }
            var img = anchorBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isAnchored ? anchorActiveColor : colorX.Clear;
        };

        // Paste button — sends Ctrl+V to Resonite window
        pasteBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Paste] Button pressed");
            WindowInput.SendPaste();
        };

        // Private mode — hides entire desktop buddy from others, stops stream
        bool isPrivate = false;
        ValueUserOverride<bool> streamVisForPrivate = null; // set when stream is created
        string savedStreamUrl = null; // saved URL to restore when ungoing private

        // Per-user visibility on the root slot: others see/collide, host always sees
        var rootVis = root.AttachComponent<ValueUserOverride<bool>>();
        rootVis.Target.Target = root.ActiveSelf_Field;
        rootVis.Default.Value = true;
        rootVis.CreateOverrideOnWrite.Value = false;

        privateBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            isPrivate = !isPrivate;
            Msg($"[Private] Mode: {isPrivate}");

            // Hide/show entire desktop buddy for everyone except host
            rootVis.Default.Value = !isPrivate;
            // Ensure host always sees it
            rootVis.SetOverride(root.World.LocalUser, true);

            // Disconnect/reconnect the stream
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

            // Update button visual
            var img = privateBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isPrivate ? new colorX(0.5f, 0.2f, 0.2f, 1f) : colorX.Clear;
        };

        // Volume slider — per-user via ValueUserOverride
        // Spawner: controls Windows system/process volume via WASAPI
        // All users: controls their own Resonite stream playback volume
        bool isDesktopCapture = hwnd == IntPtr.Zero;
        uint capturedPid = processId;

        // Store spawner identity so we can gate Windows volume to the correct user
        var ownerRef = root.AttachComponent<ReferenceField<FrooxEngine.User>>();
        ownerRef.Reference.Target = root.World.LocalUser;

        // Windows volume slider — only the spawner sees this, drives Windows API directly
        winVolSlider.Value.OnValueChange += (SyncField<float> field) =>
        {
            if (ownerRef.Reference.Target == root.World.LocalUser)
            {
                if (isDesktopCapture)
                    WindowVolume.SetMasterVolume(field.Value);
                else if (capturedPid != 0)
                    WindowVolume.SetProcessVolume(capturedPid, field.Value);
            }
        };

        // --- Back panel: dark opaque background with centered icon + title ---
        {
            var backSlot = root.AddSlot("BackPanel");
            backSlot.LocalPosition = new float3(0f, 0f, 0.001f);
            backSlot.LocalRotation = floatQ.Euler(0f, 180f, 0f);
            backSlot.LocalScale = float3.One * canvasScale;

            var backCanvas = backSlot.AttachComponent<Canvas>();
            backCanvas.Size.Value = new float2(w, h);
            var backUi = new UIBuilder(backCanvas);

            var backMat = backSlot.AttachComponent<UI_UnlitMaterial>();
            backMat.BlendMode.Value = BlendMode.Alpha;
            backMat.Sidedness.Value = Sidedness.Double;
            backMat.ZWrite.Value = ZWrite.On;
            backMat.OffsetUnits.Value = 100f;

            // Dark background filling the whole canvas
            var bg = backUi.Image(new colorX(0.08f, 0.08f, 0.1f, 1f));
            bg.Material.Target = backMat;

            // Vertical layout centered in the background
            backUi.NestInto(bg.RectTransform);
            backUi.VerticalLayout(16f);
            backUi.Style.FlexibleWidth = 1f;
            backUi.Style.FlexibleHeight = 1f;

            // Spacer top
            backUi.Spacer(1f);

            // Icon — fixed size square, centered, high-res from exe
            float iconSize = Math.Min(w, h) * 0.25f;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    var iconData = WindowIconExtractor.GetLargeIconRGBA(hwnd, out int iw, out int ih, 128);
                    if (iconData != null && iw > 0 && ih > 0)
                    {
                        // Fixed height row for the icon
                        backUi.Style.MinHeight = iconSize;
                        backUi.Style.PreferredHeight = iconSize;
                        backUi.Style.FlexibleHeight = -1f;

                        // Icon as RawImage with PreserveAspect — let the layout give it full row width,
                        // PreserveAspect will letterbox it within the fixed-height row
                        var iconTex = backSlot.AttachComponent<StaticTexture2D>();
                        var iconMat = backSlot.AttachComponent<UI_UnlitMaterial>();
                        iconMat.Texture.Target = iconTex;
                        iconMat.OffsetFactor.Value = -1f;
                        // Set texture on BOTH RawImage (for PreserveAspect) and material (for rendering)
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

            // Title text
            backUi.Style.MinHeight = 64f;
            backUi.Style.PreferredHeight = 64f;
            backUi.Style.FlexibleHeight = -1f;
            var text = backUi.Text(title, bestFit: true, alignment: Alignment.MiddleCenter);
            text.Size.Value = 48f;
            text.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f);

            // Fix text z-fighting: find the Canvas's auto-created text material and set OffsetFactor
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

            // Spacer bottom
            backUi.Style.FlexibleHeight = 1f;
            backUi.Spacer(1f);

            Msg($"[BackPanel] Created with title '{title}'");
        }

        // --- Update check (once per session, non-child panels only, async) ---
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

        // --- Remote stream: WGC frames → FFmpeg → MPEG-TS → CloudFlare tunnel → VideoTextureProvider ---
        // Multiple panels for the same hwnd share one encoder (saves GPU/CPU).
        if (StreamServer != null && TunnelUrl != null)
        {
            try
            {
                // Look up or create shared stream for this hwnd
                // Monitors have hwnd=0 — never share those, each is a different capture
                SharedStream shared;
                lock (_sharedStreams)
                {
                    if (hwnd == IntPtr.Zero || !_sharedStreams.TryGetValue(hwnd, out shared))
                    {
                        int streamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                        var encoder = StreamServer.CreateEncoder(streamId);

                        // Start audio capture — per-window or desktop-minus-Resonite
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

                // Hook NVENC directly into WGC — encodes on GPU, tiny bitstream piped to FFmpeg for HLS muxing
                // Only the first panel's WGC drives the encoder; additional panels skip encoding
                // (all WGC captures for the same hwnd produce identical frames)
                bool isFirstForHwnd = shared.RefCount == 1;
                if (isFirstForHwnd)
                {
                    var audioForEncoder = shared.Audio;
                    var contextLock = session.Streamer.D3dContextLock;
                    session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
                    {
                        if (!nvEncoder.IsInitialized)
                            nvEncoder.Initialize(device, (uint)fw, (uint)fh, contextLock, audioForEncoder);
                        nvEncoder.EncodeFrame(texture, (uint)fw, (uint)fh);
                    };
                    Msg($"[RemoteStream] This panel drives the encoder for stream {shared.StreamId}");
                }
                else
                {
                    Msg($"[RemoteStream] This panel shares encoder from stream {shared.StreamId}, no encoding hook");
                }

                // VideoTextureProvider on its own always-active slot (must stay active to load the stream)
                var videoSlot = root.AddSlot("StreamProvider");
                var videoTex = videoSlot.AttachComponent<VideoTextureProvider>();
                videoTex.URL.Value = shared.StreamUrl;
                videoTex.Stream.Value = true;
                videoTex.Volume.Value = 0f; // Start muted — test stream button enables audio
                videoTexRef = videoTex;

                // AudioOutput required for VideoTextureProvider to actually play audio
                var audioOutput = videoSlot.AttachComponent<AudioOutput>();
                audioOutput.Source.Target = videoTex;
                audioOutput.AudioTypeGroup.Value = AudioTypeGroup.Multimedia;

                // Drive audio volume from the per-user slider value.
                // The slider has ValueUserOverride so each user has their own position.
                // ValueDriver propagates the per-user value → each user controls their own volume.
                var volDriver = videoSlot.AttachComponent<ValueDriver<float>>();
                volDriver.DriveTarget.Target = audioOutput.Volume;
                volDriver.ValueSource.Target = volSlider.Value;
                // Spawner starts muted (they hear from Windows, not the stream).
                // The slider's ValueUserOverride default is 1.0 for others.
                volSliderOverride.SetOverride(root.World.LocalUser, 0f);
                // Volume driven by ValueDriver from volSlider — no separate override needed

                // Visual display on a separate slot with per-user visibility
                var streamSlot = root.AddSlot("RemoteStreamVisual");
                streamSlot.LocalScale = float3.One * canvasScale;
                streamSlotRef = streamSlot;

                // Per-user visibility on the VISUAL only — VideoTextureProvider stays active
                var streamVis = streamSlot.AttachComponent<ValueUserOverride<bool>>();
                streamVis.Target.Target = streamSlot.ActiveSelf_Field;
                streamVis.Default.Value = true; // Other users: visible
                streamVis.CreateOverrideOnWrite.Value = false;
                streamVis.SetOverride(root.World.LocalUser, false); // Spawner: hidden
                streamVisRef = streamVis;
                streamVisForPrivate = streamVis;
                Msg("[RemoteStream] Per-user visibility on visual (local=false, others=true)");

                var streamCanvas = streamSlot.AttachComponent<Canvas>();
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

                // Monitor state
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

                    // Start playback once asset is available — required for audio to work
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

        // (User profile is integrated into the unified top bar above)

        // Grabbable — scaling enabled, restricted to spawner only
        grabbable = root.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
        grabbable.OnlyUsers.Add().Target = localUser;
        grabbable.AllowSteal.Value = false;
        Msg("[StartStreaming] Grabbable attached (spawner-only)");

        // --- Throwable: track velocity during grab, apply physics on release ---
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
                    // Record position + rotation history for velocity calculation
                    int idx = histIdx % HISTORY_SIZE;
                    posHistory[idx] = root.GlobalPosition;
                    rotHistory[idx] = root.GlobalRotation;
                    timeHistory[idx] = root.World.Time.WorldTime;
                    histIdx++;
                }
                else if (wasGrabbed && histIdx >= 2)
                {
                    // Just released — compute throw velocity from position history
                    int newest = (histIdx - 1) % HISTORY_SIZE;
                    int oldest = (histIdx >= HISTORY_SIZE) ? (histIdx % HISTORY_SIZE) : 0;
                    double dt = timeHistory[newest] - timeHistory[oldest];
                    if (dt > 0.001)
                    {
                        float3 velocity = (posHistory[newest] - posHistory[oldest]) / (float)dt;
                        float speed = velocity.Magnitude;
                        Msg($"[Throw] Release velocity: {speed:F2} m/s");

                        if (speed > 3f) // threshold: 3 m/s minimum throw speed
                        {
                            thrown = true;
                            Msg($"[Throw] Thrown! velocity={speed:F2} m/s");

                            // CharacterController for engine-side physics (syncs to all users)
                            var cc = root.AttachComponent<CharacterController>();
                            cc.SimulatingUser.Target = localUser;
                            cc.Gravity.Value = new float3(0f, -9.81f, 0f);
                            cc.LinearDamping.Value = 0.3f;
                            cc.LinearVelocity = velocity;

                            // Compute per-frame rotation delta from grab history
                            // Use the last two frames for responsive rotation
                            int prev = (histIdx - 2 + HISTORY_SIZE) % HISTORY_SIZE;
                            double frameDt = timeHistory[newest] - timeHistory[prev];
                            floatQ perFrameRot = floatQ.Identity;
                            if (frameDt > 0.001)
                            {
                                floatQ rotDelta = rotHistory[newest] * rotHistory[prev].Conjugated;
                                // Scale to per-update rotation (assume ~60fps)
                                float dtRatio = (1f / 60f) / (float)frameDt;
                                var identity = floatQ.Identity;
                                perFrameRot = MathX.Slerp(in identity, rotDelta, dtRatio);
                            }

                            // Fade out over 3 seconds then destroy
                            float fadeSeconds = 3f;
                            int fadeFrames = (int)(fadeSeconds * 60f);
                            int frame = 0;
                            float3 lastPos = root.GlobalPosition;

                            void FadeAndCollisionLoop()
                            {
                                if (root.IsDestroyed) return;
                                frame++;
                                float t = (float)frame / fadeFrames;

                                // Fade: scale down smoothly
                                float scale = MathX.Lerp(1f, 0f, t * t); // ease-in
                                root.LocalScale = float3.One * MathX.Max(0.01f, scale);

                                // Apply rotation (full quaternion delta from grab)
                                root.LocalRotation = root.LocalRotation * perFrameRot;

                                // Collision check: if position barely changed, we hit something
                                float3 curPos = root.GlobalPosition;
                                if (frame > 5) // skip first few frames
                                {
                                    float delta = (curPos - lastPos).Magnitude;
                                    if (delta < 0.001f)
                                    {
                                        Msg("[Throw] Collision detected, destroying");
                                        root.Destroy();
                                        return;
                                    }
                                }
                                lastPos = curPos;

                                if (frame >= fadeFrames)
                                {
                                    Msg("[Throw] Fade complete, destroying");
                                    root.Destroy();
                                    return;
                                }
                                root.World.RunInUpdates(1, FadeAndCollisionLoop);
                            }
                            root.World.RunInUpdates(1, FadeAndCollisionLoop);
                            return; // stop tracking
                        }
                    }
                    histIdx = 0; // reset if below threshold
                }
                wasGrabbed = isGrabbed;
                root.World.RunInUpdates(1, ThrowTrackLoop);
            }
            root.World.RunInUpdates(1, ThrowTrackLoop);
        }

        root.PersistentSelf = false;
        root.Name = $"Desktop: {title}";

        // Start update loop in this world
        ScheduleUpdate(root.World);

        // Focus the window in Windows — but only for user-initiated panels, not child popups
        if (!isChild)
            WindowInput.FocusWindow(hwnd);
        Msg($"[StartStreaming] Window focused, streaming started for: {title}");
    }

    /// <summary>
    /// Spawn a full DesktopBuddy for a child/popup window, positioned relative to the parent.
    /// </summary>
    private static void SpawnChildWindow(DesktopSession parentSession, IntPtr childHwnd)
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

        // Get window title
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(childHwnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrEmpty(title)) title = $"Popup ({childHwnd})";

        // Position relative to parent: map screen pixel offset to world space
        float canvasScale = 0.0005f;
        float offsetX, offsetY;
        float offsetZ = -0.01f; // 1cm in front of parent

        // Explorer children (file dialogs, properties, etc.) go to center of parent
        // because explorer's child windows have unpredictable screen positions
        bool isExplorer = false;
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById((int)parentSession.ProcessId);
            isExplorer = proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

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

        // Track BEFORE StartStreaming so the session can be found
        parentSession.TrackedChildHwnds.Add(childHwnd);

        try
        {
            StartStreaming(root, childHwnd, title, isChild: true);

            // Find the session that was just created and set up parent-child relationship
            var childSession = ActiveSessions.Find(s => s.Hwnd == childHwnd && s.Root == root);
            if (childSession != null)
            {
                childSession.ParentSession = parentSession;
                parentSession.ChildSessions.Add(childSession);
                Msg($"[ChildWindow] Full DesktopBuddy spawned, parent now tracking {parentSession.ChildSessions.Count} children");
            }
            else
            {
                Msg($"[ChildWindow] Warning: StartStreaming succeeded but session not found");
            }
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

    // Cached reflection — looked up once, compiled to delegates for zero-overhead per-frame calls
    private static readonly Func<ProceduralTextureBase, Bitmap2D> _getTex2D;
    private static readonly Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated> _setFromBitmap;

    private delegate void SetFromCurrentBitmapDelegate(ProceduralTextureBase instance, Renderite.Shared.TextureUploadHint hint, FrooxEngine.AssetIntegrated callback);

    static DesktopBuddyMod()
    {
        var tex2DGetter = typeof(ProceduralTextureBase)
            .GetProperty("tex2D", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetGetMethod(true);
        if (tex2DGetter != null)
            _getTex2D = (Func<ProceduralTextureBase, Bitmap2D>)Delegate.CreateDelegate(
                typeof(Func<ProceduralTextureBase, Bitmap2D>), tex2DGetter);

        var setMethod = typeof(ProceduralTextureBase)
            .GetMethod("SetFromCurrentBitmap", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setMethod != null)
            _setFromBitmap = (Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated>)
                Delegate.CreateDelegate(
                    typeof(Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated>),
                    setMethod);

        Msg("[DesktopBuddyMod] Compiled reflection delegates: " +
            $"getTex2D={_getTex2D != null}, setFromBitmap={_setFromBitmap != null}");
    }

    private static readonly Stopwatch _perfSw = new();

    private static void CleanupSession(DesktopSession session)
    {
        if (session.Cleaned) { Msg($"[Cleanup] Already cleaned hwnd={session.Hwnd} streamId={session.StreamId}, skipping"); return; }
        session.Cleaned = true;
        Msg($"[Cleanup] === START === hwnd={session.Hwnd} streamId={session.StreamId} isChild={session.IsChildPanel} children={session.ChildSessions.Count}");

        // Clean up child popup sessions first
        if (session.ChildSessions.Count > 0)
        {
            Msg($"[Cleanup] Destroying {session.ChildSessions.Count} child popup panels");
            foreach (var child in session.ChildSessions)
            {
                Msg($"[Cleanup] Child: nulling OnGpuFrame hwnd={child.Hwnd}");
                if (child.Streamer != null) child.Streamer.OnGpuFrame = null;
                child.ParentSession = null;
                // Don't remove from ActiveSessions here — it corrupts indices if the
                // caller is iterating ActiveSessions. The Cleaned flag ensures the main
                // loop skips re-cleanup, and it will remove them via RemoveAt(i) safely.
                Msg($"[Cleanup] Child: disconnecting VTP hwnd={child.Hwnd}");
                if (child.Root != null && !child.Root.IsDestroyed)
                {
                    var vtp = child.Root.GetComponentInChildren<VideoTextureProvider>();
                    if (vtp != null && !vtp.IsDestroyed) { vtp.URL.Value = null; vtp.Stop(); }
                    var childWorld = child.Root.World;
                    var rootToDie = child.Root;
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
                Msg($"[Cleanup] Child: calling CleanupSession recursively hwnd={child.Hwnd}");
                CleanupSession(child);
                Msg($"[Cleanup] Child: done hwnd={child.Hwnd}");
            }
            session.ChildSessions.Clear();
            session.TrackedChildHwnds.Clear();
        }

        // If this is a child panel, remove from parent's tracking
        if (session.ParentSession != null)
        {
            Msg($"[Cleanup] Removing from parent tracking");
            session.ParentSession.TrackedChildHwnds.Remove(session.Hwnd);
            session.ParentSession.ChildSessions.Remove(session);
        }

        Msg($"[Cleanup] Removing canvas ID");
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        Msg($"[Cleanup] Nulling OnGpuFrame callback");
        var streamer = session.Streamer;
        if (streamer != null) streamer.OnGpuFrame = null;
        int streamId = session.StreamId;
        IntPtr hwnd = session.Hwnd;
        session.Streamer = null;

        Msg($"[Cleanup] Queuing background dispose for stream {streamId}");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Brief delay to let in-flight GPU frames and encode operations drain
                // before we start disposing resources they may still be using
                System.Threading.Thread.Sleep(200);
                Msg($"[Cleanup:BG] === START === stream {streamId}");

                AudioCapture audioToDispose = null;
                bool shouldStopEncoder = false;
                if (streamId > 0)
                {
                    Msg($"[Cleanup:BG] Taking _sharedStreams lock");
                    lock (_sharedStreams)
                    {
                        Msg($"[Cleanup:BG] Lock acquired");
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
                            Msg($"[Cleanup:BG] Orphaned stream");
                        }
                    }
                    Msg($"[Cleanup:BG] Lock released, shouldStop={shouldStopEncoder}");

                    if (shouldStopEncoder)
                    {
                        Msg($"[Cleanup:BG] Stopping encoder {streamId}...");
                        StreamServer?.StopEncoder(streamId);
                        Msg($"[Cleanup:BG] Encoder {streamId} stopped");
                    }
                }

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
            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];

                // Session already cleaned (e.g. child cleaned during parent cleanup) — just remove
                if (session.Cleaned)
                {
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root == null || session.Root.IsDestroyed ||
                    session.Texture == null || session.Texture.IsDestroyed)
                {
                    Msg($"[UpdateLoop] Session {i} root/texture destroyed, cleaning up (root={session.Root != null} rootDestroyed={session.Root?.IsDestroyed} tex={session.Texture != null} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId})");
                    Msg("[UpdateLoop] Step 1: Nulling OnGpuFrame");
                    if (session.Streamer != null) session.Streamer.OnGpuFrame = null;
                    Msg("[UpdateLoop] Step 2: Calling CleanupSession");
                    CleanupSession(session);
                    Msg("[UpdateLoop] Step 3: Removing from ActiveSessions");
                    ActiveSessions.RemoveAt(i);
                    Msg("[UpdateLoop] Step 4: Session removed, continuing");
                    continue;
                }

                if (session.Root.World != world) continue;
                if (session.UpdateInProgress) continue;

                // Window closed — disconnect libVLC first, then destroy after a delay
                // libVLC's stop can block the engine thread if destroyed synchronously
                if (session.Streamer != null && !session.Streamer.IsValid)
                {
                    Msg($"[UpdateLoop] Window closed (IsValid=false), destroying viewer");
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);

                    // Disconnect VideoTextureProvider before destroying so libVLC doesn't block
                    var vtp = session.Root.GetComponentInChildren<VideoTextureProvider>();
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg("[UpdateLoop] Disconnecting VideoTextureProvider before destroy");
                        vtp.URL.Value = null;
                        vtp.Stop();
                    }
                    // Defer actual slot destruction to give libVLC time to release
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

                // --- Child window polling — runs every tick, independent of frame capture ---
                if (!session.IsChildPanel && session.ProcessId != 0)
                {
                    session.TimeSinceChildCheck += dt;
                    if (session.TimeSinceChildCheck >= 0.1) // 100ms poll
                    {
                        session.TimeSinceChildCheck = 0;
                        try
                        {
                            var procWindows = WindowEnumerator.GetProcessWindows(session.ProcessId);
                            foreach (var win in procWindows)
                            {
                                if (win.Handle == session.Hwnd) continue;
                                if (session.TrackedChildHwnds.Contains(win.Handle)) continue;
                                if (WindowEnumerator.TryGetWindowRect(win.Handle, out _, out _, out int cw2, out int ch2) && cw2 > 10 && ch2 > 10)
                                {
                                    Msg($"[ChildWindow] Detected new popup: hwnd={win.Handle} title='{win.Title}' size={cw2}x{ch2}");
                                    SpawnChildWindow(session, win.Handle);
                                }
                            }

                            var activeHwnds = new HashSet<IntPtr>(procWindows.Select(pw => pw.Handle));
                            for (int c = session.ChildSessions.Count - 1; c >= 0; c--)
                            {
                                var child = session.ChildSessions[c];
                                if (child.Streamer == null || !activeHwnds.Contains(child.Hwnd))
                                {
                                    Msg($"[ChildWindow] Popup closed: hwnd={child.Hwnd}");
                                    session.TrackedChildHwnds.Remove(child.Hwnd);
                                    if (child.Streamer != null) child.Streamer.OnGpuFrame = null;
                                    child.ParentSession = null;
                                    ActiveSessions.Remove(child);
                                    session.ChildSessions.RemoveAt(c);
                                    if (child.Root != null && !child.Root.IsDestroyed)
                                    {
                                        var cvtp = child.Root.GetComponentInChildren<VideoTextureProvider>();
                                        if (cvtp != null && !cvtp.IsDestroyed) { cvtp.URL.Value = null; cvtp.Stop(); }
                                        var cRoot = child.Root;
                                        world.RunInUpdates(10, () =>
                                        {
                                            if (cRoot != null && !cRoot.IsDestroyed) cRoot.Destroy();
                                        });
                                    }
                                    CleanupSession(child);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Msg($"[ChildWindow] Polling error: {ex.Message}");
                        }
                    }
                }

                // Throttle to target FPS using engine time
                session.TimeSinceLastCapture += dt;
                if (session.TimeSinceLastCapture < session.TargetInterval)
                    continue;
                session.TimeSinceLastCapture = 0;

                // Wait for the asset to be created by the first normal update
                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");
                    continue;
                }

                // Switch to manual mode after first update so we control the data
                if (!session.ManualModeSet)
                {
                    session.Texture.LocalManualUpdate = true;
                    session.ManualModeSet = true;
                    Msg("[UpdateLoop] Set LocalManualUpdate = true");
                }

                // Get frame
                var frame = session.Streamer.CaptureFrame(out int w, out int h);
                if (frame == null) continue;

                // Window resized — update texture + canvas size, reset manual mode so bitmap gets recreated
                if (session.Texture.Size.Value.x != w || session.Texture.Size.Value.y != h)
                {
                    Msg($"[UpdateLoop] Window resize {session.Texture.Size.Value.x}x{session.Texture.Size.Value.y} -> {w}x{h}");
                    session.Texture.Size.Value = new int2(w, h);
                    if (session.Canvas != null)
                        session.Canvas.Size.Value = new float2(w, h);
                    session.Texture.LocalManualUpdate = false;
                    session.ManualModeSet = false;
                    continue; // Skip this frame, let texture recreate
                }

                var bitmap = _getTex2D?.Invoke(session.Texture);
                if (bitmap == null || bitmap.Size.x != w || bitmap.Size.y != h)
                {
                    if (_updateCount <= 10) Msg($"[UpdateLoop] Bitmap null or size mismatch, waiting...");
                    continue;
                }

                // Copy frame into bitmap
                using (Perf.Time("bitmap_copy"))
                    frame.AsSpan(0, w * h * 4).CopyTo(bitmap.RawData);

                // Upload to GPU
                using (Perf.Time("texture_upload"))
                    _setFromBitmap?.Invoke(session.Texture, default, null);

                Perf.IncrementFrames();
            }
        }
        catch (Exception ex)
        {
            Msg($"ERROR in UpdateLoop: {ex}");
        }

        // Check if any sessions left for this world
        bool hasSessionsInWorld = ActiveSessions.Any(s => s.Root?.World == world);
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

    private static void StartTunnel()
    {
        try
        {
            // Check if cloudflared is available
            var modDir = System.IO.Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
            string[] candidates = {
                System.IO.Path.Combine(modDir, "..", "cloudflared", "cloudflared.exe"),
                System.IO.Path.Combine(modDir, "cloudflared", "cloudflared.exe"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared", "cloudflared.exe"),
                "cloudflared"
            };
            string cfPath = null;
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
                    if (p?.ExitCode == 0) { cfPath = c; Msg($"[Tunnel] Found cloudflared: {c}"); break; }
                }
                catch { }
            }

            if (cfPath == null)
            {
                Msg("[Tunnel] cloudflared not found — stream only available on localhost");
                return;
            }

            Msg($"[Tunnel] Starting cloudflared tunnel: {cfPath}");
            var psi = new ProcessStartInfo
            {
                FileName = cfPath,
                // --config NUL bypasses any existing named tunnel config in .cloudflared/config.yml
                Arguments = $"tunnel --config NUL --url http://localhost:{STREAM_PORT}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _tunnelProcess = Process.Start(psi);
            if (_tunnelProcess == null) { Msg("[Tunnel] Failed to start cloudflared"); return; }
            var proc = _tunnelProcess;

            // cloudflared prints the tunnel URL to stderr
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel/stderr] {e.Data}");
                // Look for the tunnel URL in output
                if (e.Data.Contains("https://") && e.Data.Contains(".trycloudflare.com"))
                {
                    int idx = e.Data.IndexOf("https://");
                    string url = e.Data.Substring(idx).Trim();
                    int space = url.IndexOf(' ');
                    if (space > 0) url = url.Substring(0, space);
                    // Always strip to origin — cloudflared logs proxied request URLs with paths
                    try { url = new Uri(url).GetLeftPart(UriPartial.Authority); } catch { }
                    TunnelUrl = url;
                    Msg($"[Tunnel] PUBLIC URL: {TunnelUrl}");
                }
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

    // Max frame size for pipe throughput: ~4MB/frame works at realtime
    // 1280x720x4 = 3.6MB — tested at 1.8x realtime
    private const int STREAM_MAX_W = 1280;
    private const int STREAM_MAX_H = 720;

    private static void DownscaleAndShareFrame(DesktopSession session, byte[] frame, int w, int h)
    {
        int dstW = w, dstH = h;
        bool needsScale = w > STREAM_MAX_W || h > STREAM_MAX_H;
        if (needsScale)
        {
            float scale = Math.Min((float)STREAM_MAX_W / w, (float)STREAM_MAX_H / h);
            dstW = ((int)(w * scale)) & ~1;
            dstH = ((int)(h * scale)) & ~1;
        }

        lock (session.StreamLock)
        {
            if (!needsScale)
            {
                // Pass through — no copy, FFmpeg handles vflip
                session.StreamFrame = frame;
            }
            else
            {
                // Bilinear-ish downscale (2x2 box average for better quality than nearest-neighbor)
                if (session.ScaledBuffer == null || session.ScaledBuffer.Length != dstW * dstH * 4)
                    session.ScaledBuffer = new byte[dstW * dstH * 4];

                unsafe
                {
                    fixed (byte* srcPtr = frame, dstPtr = session.ScaledBuffer)
                    {
                        uint* src = (uint*)srcPtr;
                        uint* dst = (uint*)dstPtr;
                        for (int y = 0; y < dstH; y++)
                        {
                            int srcY = y * h / dstH;
                            for (int x = 0; x < dstW; x++)
                            {
                                int srcX = x * w / dstW;
                                dst[y * dstW + x] = src[srcY * w + srcX];
                            }
                        }
                    }
                }
                session.StreamFrame = session.ScaledBuffer;
            }
            session.StreamWidth = dstW;
            session.StreamHeight = dstH;
            session.StreamFrameReady = true;
            System.Threading.Monitor.PulseAll(session.StreamLock);
        }
    }

    internal new static void Msg(string msg) => ResoniteMod.Msg(msg);
    internal new static void Error(string msg) => ResoniteMod.Error(msg);
}

/// <summary>
/// Suppress locomotion for the specific hand pointing at a desktop viewer canvas.
/// Patches BeforeInputUpdate (which runs before the input system evaluates bindings).
/// Sets _inputs.Axis.RegisterBlocks = true so the input system blocks the locomotion
/// module from reading this hand's joystick. Only the pointing hand is affected.
///
/// Verified from decompiled source:
/// - InteractionHandler._inputs is private InteractionHandlerInputs (line 206707)
/// - InteractionHandlerInputs.Axis is public readonly Analog2DAction (line 205096)
/// - InputAction.RegisterBlocks is public bool (line 350410)
/// - BeforeInputUpdate normally sets: _inputs.Axis.RegisterBlocks = ActiveTool?.UsesSecondary ?? false (line 207475)
/// - Our postfix overrides that to true when laser touches our canvas
/// </summary>
[HarmonyPatch(typeof(InteractionHandler), nameof(InteractionHandler.BeforeInputUpdate))]
static class LocomotionSuppressionPatch
{
    // InteractionHandler._inputs (private)
    private static readonly FieldInfo _inputsField = typeof(InteractionHandler)
        .GetField("_inputs", BindingFlags.NonPublic | BindingFlags.Instance);
    // InteractionHandlerInputs.Axis (public)
    private static readonly FieldInfo _axisField = typeof(InteractionHandlerInputs)
        .GetField("Axis");

    static void Postfix(InteractionHandler __instance)
    {
        try
        {
            var touchable = __instance.Laser?.CurrentTouchable;
            if (touchable == null) return;

            if (touchable is Canvas canvas && DesktopBuddyMod.DesktopCanvasIds.Contains(canvas.ReferenceID))
            {
                // Set RegisterBlocks = true on this hand's Axis action.
                // This tells the input system that this action is consuming the physical joystick,
                // preventing SmoothLocomotionInputs.Move from reading it.
                if (_inputsField != null && _axisField != null)
                {
                    var inputs = _inputsField.GetValue(__instance);
                    if (inputs is InteractionHandlerInputs typedInputs)
                    {
                        typedInputs.Axis.RegisterBlocks = true;
                    }
                }
            }
        }
        catch
        {
            // Silent — runs every frame
        }
    }
}

/// <summary>
/// Map Resonite Key enum to Windows Virtual Key codes.
/// Verified: Key enum in Renderite.Shared (line 1650)
/// </summary>
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

/// <summary>
/// Intercept InputInterface.SimulatePress to forward keys to Windows AND block Resonite.
/// SimulatePress is called for every key press from the virtual keyboard.
/// Modifiers (Shift/Ctrl/Alt) are held down until released by ShiftActive changing.
/// Non-modifier keys get a press+release.
///
/// Verified: SimulatePress(Key key, World origin) at line 359293
/// </summary>
[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.SimulatePress))]
static class SimulatePressPatch
{
    static bool Prefix(Key key, World origin)
    {
        if (DesktopBuddyMod.ActiveSessions.Count == 0 ||
            !DesktopBuddyMod.ActiveSessions.Any(s => s.Root?.World == origin))
        {
            return true; // Not our world, let Resonite handle it
        }

        // Forward to Windows
        if (KeyMapper.KeyToVK.TryGetValue(key, out ushort vk))
        {
            if (KeyMapper.IsModifier(key))
            {
                // Modifier: hold down (don't release — will be released when shift state changes)
                WindowInput.SendVirtualKeyDown(vk);
                // Modifier down
            }
            else
            {
                // Regular key: press and release
                WindowInput.SendVirtualKey(vk);
                // Key press
                // Release any held modifiers after the key press
                WindowInput.ReleaseAllModifiers();
            }
        }
        else
        {
            // Unmapped key ignored
        }

        return false; // Block Resonite
    }
}

/// <summary>
/// Intercept InputInterface.TypeAppend to forward text to Windows AND block Resonite.
/// TypeAppend is called for character input from the virtual keyboard.
///
/// Verified: TypeAppend(string typeDelta, World origin) at line 359277
/// </summary>
[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.TypeAppend))]
static class TypeAppendPatch
{
    static bool Prefix(string typeDelta, World origin)
    {
        if (DesktopBuddyMod.ActiveSessions.Count == 0 ||
            !DesktopBuddyMod.ActiveSessions.Any(s => s.Root?.World == origin))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(typeDelta))
        {
            WindowInput.SendString(typeDelta);
            // Release modifiers after text input
            WindowInput.ReleaseAllModifiers();
        }

        return false;
    }
}

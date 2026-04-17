using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Renderite.Shared;
using FrooxEngine;
using SkyFrost.Base;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
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
        }
        else if (hwnd == IntPtr.Zero && monitorIndex >= 0)
        {
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
            if (handler != null && handler.Side.Value == Chirality.Right)
                return 1;
            return 0;
        }

        btn.LocalPressed += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if ((Config?.GetValue(CancelInputInDesktopMode) ?? false) && IsDesktopMode(root.World)) return;
            ClaimSource(data.source);
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.FocusWindow(hwnd);
            WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source), streamer.MonitorHandle);
        };

        btn.LocalPressing += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if ((Config?.GetValue(CancelInputInDesktopMode) ?? false) && IsDesktopMode(root.World)) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source), streamer.MonitorHandle);
        };

        btn.LocalReleased += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if ((Config?.GetValue(CancelInputInDesktopMode) ?? false) && IsDesktopMode(root.World)) return;
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, GetTouchId(data.source), streamer.MonitorHandle);
        };

        btn.LocalHoverStay += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if ((Config?.GetValue(CancelInputInDesktopMode) ?? false) && IsDesktopMode(root.World)) return;
            float hu = data.normalizedPressPoint.x;
            float hv = 1f - data.normalizedPressPoint.y;

            if (IsActiveSource(data.source))
            {
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height, streamer.MonitorHandle);
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
                    WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle);
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
                            WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle);
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
            cam.Clear.Value = CameraClearMode.Color;
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
                                    TextureFormat.RGBA32, false, ColorProfile.sRGB, false);
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

        if (!_updateShown && !isChild && Config!.GetValue(CheckForUpdates))
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

        bool useMediaMtx = IsMediaMtxEnabled;
        if (useMediaMtx || (StreamServer != null && TunnelUrl != null))
        {
            try
            {
                SharedStream shared;
                lock (_sharedStreams)
                {
                    if (hwnd == IntPtr.Zero || !_sharedStreams.TryGetValue(hwnd, out shared))
                    {
                        int streamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                        FfmpegEncoder encoder;
                        Uri url;

                        if (useMediaMtx)
                        {
                            var rtspUrl = GetMediaMtxRtspUrl(streamId);
                            encoder = new FfmpegEncoder(streamId, rtspUrl);
                            url = new Uri(rtspUrl);
                            Msg($"[RemoteStream] Using MediaMTX RTSP: {rtspUrl}");
                        }
                        else
                        {
                            encoder = StreamServer.CreateEncoder(streamId);
                            url = new Uri($"{TunnelUrl}/stream/{streamId}");
                        }

                        var audio = new AudioCapture();
                        if (hwnd != IntPtr.Zero)
                            audio.Start(hwnd, AudioCaptureMode.IncludeProcess);
                        else
                            audio.Start(IntPtr.Zero, AudioCaptureMode.ExcludeProcess);

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
                session.Encoder = shared.Encoder;
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
            Msg($"[RemoteStream] Skipped: MediaMtx={IsMediaMtxEnabled} StreamServer={StreamServer != null} TunnelUrl={TunnelUrl ?? "null"}");
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
            root.Tag = "Desktop Buddy";
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
            var proc = Process.GetProcessById((int)parentSession.ProcessId);
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

}

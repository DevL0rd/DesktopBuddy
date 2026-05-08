using System;
using System.Collections.Generic;
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
    private const float DesktopPanelCurvature = 0.18f;
    private const int DesktopPanelCurveSegments = 48;

    private static CurvedPlaneMesh AddCurvedTexturePlane(
        Slot parent,
        string name,
        int width,
        int height,
        float scale,
        IAssetProvider<ITexture2D> texture,
        float zOffset,
        bool flipY,
        float offsetUnits)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition = new float3(0f, 0f, zOffset);
        slot.LocalScale = float3.One * scale;

        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<CurvedPlaneMesh>();
        mesh.Size.Value = new float2(width, height);
        mesh.Curvature.Value = DesktopPanelCurvature;
        mesh.AspectRatioCompensation.Value = CurvedPlaneMesh.CurvatureAspectRatioCompensation.DecreaseWidth;
        mesh.Segments.Value = DesktopPanelCurveSegments;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<MeshCollider>();
        collider.Mesh.Target = mesh;
        collider.Sidedness.Value = MeshColliderSidedness.Front;

        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = texture;
        material.BlendMode.Value = BlendMode.Opaque;
        material.Sidedness.Value = Sidedness.Double;
        material.ZWrite.Value = ZWrite.On;
        material.OffsetUnits.Value = offsetUnits;
        if (flipY)
        {
            material.TextureScale.Value = new float2(1f, -1f);
            material.TextureOffset.Value = new float2(0f, 1f);
        }
        renderer.Materials.Add(material);

        return mesh;
    }

    private static CurvedPlaneMesh AddCurvedBackPlane(Slot parent, int width, int height, float scale)
    {
        var slot = parent.AddSlot("BackPanelCurvedPlane");
        slot.LocalPosition = new float3(0f, 0f, 0.001f);
        slot.LocalScale = float3.One * scale;

        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<CurvedPlaneMesh>();
        mesh.Size.Value = new float2(width, height);
        mesh.Curvature.Value = DesktopPanelCurvature;
        mesh.AspectRatioCompensation.Value = CurvedPlaneMesh.CurvatureAspectRatioCompensation.DecreaseWidth;
        mesh.Segments.Value = DesktopPanelCurveSegments;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<MeshCollider>();
        collider.Mesh.Target = mesh;
        collider.Sidedness.Value = MeshColliderSidedness.DualSided;

        var material = slot.AttachComponent<PBS_DualSidedMetallic>();
        material.AlbedoColor.Value = new colorX(0.08f, 0.08f, 0.1f, 1f);
        material.Culling.Value = Culling.Front;
        material.AlphaHandling.Value = FrooxEngine.AlphaHandling.Opaque;
        material.Metallic.Value = 0f;
        material.Smoothness.Value = 0.35f;
        renderer.Materials.Add(material);

        return mesh;
    }

    private static CurvedPlaneMesh AddCurvedStripPlane(Slot parent, string name, int width, float height, float scale, float yOffset, float zOffset)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition = new float3(0f, yOffset, zOffset);
        slot.LocalScale = float3.One * scale;

        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<CurvedPlaneMesh>();
        mesh.Size.Value = new float2(width, height);
        mesh.Curvature.Value = DesktopPanelCurvature;
        mesh.AspectRatioCompensation.Value = CurvedPlaneMesh.CurvatureAspectRatioCompensation.DecreaseWidth;
        mesh.Segments.Value = DesktopPanelCurveSegments;
        renderer.Mesh.Target = mesh;

        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = new colorX(1f, 1f, 1f, 0f);
        material.BlendMode.Value = BlendMode.Alpha;
        material.Sidedness.Value = Sidedness.Double;
        material.ZWrite.Value = ZWrite.Off;
        renderer.Materials.Add(material);

        return mesh;
    }

    private static CurvedPlaneMesh AddCurvedRenderPlane(
        Slot parent,
        string name,
        int width,
        float height,
        float scale,
        float yOffset,
        float zOffset,
        IAssetProvider<ITexture2D> texture,
        Camera rayExit,
        Slot raycastTargetRoot,
        bool addCollider = true,
        Sidedness sidedness = Sidedness.Front,
        ZWrite zWrite = ZWrite.On,
        float offsetUnits = 120f,
        BlendMode blendMode = BlendMode.Alpha,
        int renderQueue = -1,
        float alphaCutoff = 0.01f,
        float2? textureScale = null,
        float2? textureOffset = null)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition = new float3(0f, yOffset, zOffset);
        slot.LocalScale = float3.One * scale;

        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<CurvedPlaneMesh>();
        mesh.Size.Value = new float2(width, height);
        mesh.Curvature.Value = DesktopPanelCurvature;
        mesh.AspectRatioCompensation.Value = CurvedPlaneMesh.CurvatureAspectRatioCompensation.DecreaseWidth;
        mesh.Segments.Value = DesktopPanelCurveSegments;
        renderer.Mesh.Target = mesh;

        if (addCollider)
        {
            var collider = slot.AttachComponent<MeshCollider>();
            collider.Mesh.Target = mesh;
            collider.Sidedness.Value = MeshColliderSidedness.Front;
        }

        if (rayExit != null)
        {
            var portal = slot.AttachComponent<MeshUVRaycastPortal>();
            portal.RayExit.Target = rayExit;
            portal.OverrideHitTriggers.Value = true;
            portal.RepeatUV.Value = false;
        }

        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = texture;
        material.BlendMode.Value = blendMode;
        material.AlphaCutoff.Value = alphaCutoff;
        material.Sidedness.Value = sidedness;
        material.ZWrite.Value = zWrite;
        material.OffsetUnits.Value = offsetUnits;
        material.RenderQueue.Value = renderQueue;
        if (textureScale.HasValue)
            material.TextureScale.Value = textureScale.Value;
        if (textureOffset.HasValue)
            material.TextureOffset.Value = textureOffset.Value;
        renderer.Materials.Add(material);

        return mesh;
    }

    private static bool IsSlotOrChild(Slot slot, Slot parent)
    {
        for (var current = slot; current != null; current = current.Parent)
        {
            if (current == parent)
                return true;
        }
        return false;
    }

    private static float GetCurvedPanelDepth(CurvedPlaneMesh mesh, float scale)
    {
        if (mesh == null || mesh.IsDestroyed)
            return 0f;

        return GetCurvedPanelDepthAtU(mesh, 0.5f, scale);
    }

    private static float GetCurvedPanelDepthAtU(CurvedPlaneMesh mesh, float u, float scale)
    {
        if (mesh == null || mesh.IsDestroyed)
            return 0f;

        float curvature = MathX.Clamp01(mesh.Curvature.Value);
        if (curvature < 0.01f)
            return 0f;

        float2 size = CurvedPlaneMesh.CompensateSize(mesh.Size.Value, curvature, mesh.AspectRatioCompensation.Value);
        float radius = size.x * 0.5f;
        float totalAngle = MathF.PI * curvature;
        float startAngle = (MathF.PI - totalAngle) * 0.5f;
        float angle = startAngle + totalAngle * MathX.Clamp01(u);
        return (MathX.Sin(angle) * radius - MathX.Sin(startAngle) * radius) * scale;
    }

    private static bool TryGetCurvedPlaneUV(CurvedPlaneMesh mesh, in float3 globalPoint, out float2 uv)
    {
        uv = default;
        if (mesh == null || mesh.IsDestroyed)
            return false;

        float3 localPoint = mesh.Slot.GlobalPointToLocal(in globalPoint);
        float curvature = MathX.Clamp01(mesh.Curvature.Value);
        float2 size = CurvedPlaneMesh.CompensateSize(mesh.Size.Value, curvature, mesh.AspectRatioCompensation.Value);
        if (size.x <= 0f || size.y <= 0f)
            return false;

        float u;
        if (curvature < 0.01f)
        {
            u = localPoint.x / size.x + 0.5f;
        }
        else
        {
            float radius = size.x * 0.5f;
            float totalAngle = MathF.PI * curvature;
            float startAngle = (MathF.PI - totalAngle) * 0.5f;
            float widthAdjust = 1f / MathX.Cos(startAngle);
            float cosAngle = MathX.Clamp(localPoint.x / (-widthAdjust * radius), -1f, 1f);
            float angle = MathF.Acos(cosAngle);
            u = (angle - startAngle) / totalAngle;
        }

        float v = 0.5f - localPoint.y / size.y;
        uv = new float2(MathX.Clamp01(u), MathX.Clamp01(v));
        return true;
    }

    internal static void SpawnStreaming(World world, IntPtr hwnd, string title, IntPtr monitorHandle = default, int monitorIndex = -1)
    {
        try
        {
            Msg($"[SpawnStreaming] Starting for '{title}' hwnd={hwnd} monitorIndex={monitorIndex}");
            if (hwnd != IntPtr.Zero)
            {
                WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
                if (!WindowEnumerator.TryValidateStandaloneProcessWindow(hwnd, processId, out string currentTitle, out string validationReason))
                {
                    Msg($"[SpawnStreaming] Ignored hwnd={hwnd}: {validationReason}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(currentTitle))
                    title = currentTitle;
            }

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

    private static void StartStreaming(Slot root, IntPtr hwnd, string title, IntPtr monitorHandle = default, int monitorIndex = -1)
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
                world.RunInUpdates(0, () =>
                {
                    if (root != null && !root.IsDestroyed)
                        root.Destroy();
                });
                return;
            }
            world.RunInUpdates(0, () => FinishStartStreaming(root, hwnd, title, streamer, monitorIndex));
        });
    }

    private static void FinishStartStreaming(Slot root, IntPtr hwnd, string title, DesktopStreamer streamer, int monitorIndex = -1)
    {
        if (root == null || root.IsDestroyed)
        {
            Msg($"[StartStreaming] Root slot destroyed before capture completed: {title}");
            streamer.Dispose();
            return;
        }

        int w = streamer.Width;
        int h = streamer.Height;
        Grabbable grabbable = null;

        Msg($"[StartStreaming] Window size: {w}x{h}, WGC event-driven capture");

        float canvasScale = 0.0005f;
        float worldHalfH = h / 2f * canvasScale;
        float worldHalfW = w / 2f * canvasScale;
        BoxCollider collider = null;
        Msg("[StartStreaming] Panel grab/click colliders are curved mesh colliders");
        CurvedPlaneMesh frontPlaneRef = null;
        CurvedPlaneMesh backPlaneRef = null;
        CurvedPlaneMesh streamPlaneRef = null;
        CurvedPlaneMesh topBarStripRef = null;
        CurvedPlaneMesh topBarBackStripRef = null;
        DesktopUVRayExit displayRayExitRef = null;
        TextRenderer titleTextRef = null;
        Slot deviceIndicatorsSlot = null;
        string panelCurvePreferenceKey = GetPanelCurvePreferenceKey(hwnd);
        float currentPanelCurvature = GetPanelCurvePreference(panelCurvePreferenceKey, DesktopPanelCurvature);

        void ApplyPanelCurvature(float curvature)
        {
            currentPanelCurvature = MathX.Clamp(curvature, 0f, 1f);

            if (frontPlaneRef != null && !frontPlaneRef.IsDestroyed)
                frontPlaneRef.Curvature.Value = currentPanelCurvature;

            if (backPlaneRef != null && !backPlaneRef.IsDestroyed)
                backPlaneRef.Curvature.Value = currentPanelCurvature;

            if (streamPlaneRef != null && !streamPlaneRef.IsDestroyed)
                streamPlaneRef.Curvature.Value = currentPanelCurvature;

            if (topBarStripRef != null && !topBarStripRef.IsDestroyed)
                topBarStripRef.Curvature.Value = currentPanelCurvature;

            if (topBarBackStripRef != null && !topBarBackStripRef.IsDestroyed)
                topBarBackStripRef.Curvature.Value = currentPanelCurvature;
        }

        var displaySlot = root.AddLocalSlot("Display", false);
        displaySlot.LocalScale = float3.One * canvasScale;
        Msg("[StartStreaming] Display slot (local) created");

        var texSlot = displaySlot.AddSlot("Texture");
        var procTex = TextureProviderSettings.ClampWrap(texSlot.AttachComponent<DesktopTextureProvider>());
        OurProviders.Add(procTex);
        int sharedTextureSlot = -1;
        bool useTextureBridge = TextureBridgeChannel != null && TextureBridgeChannel.IsOpen &&
            (hwnd != IntPtr.Zero || streamer.MonitorHandle != IntPtr.Zero || monitorIndex >= 0);
        if (useTextureBridge)
        {
            sharedTextureSlot = TextureBridgeChannel.RegisterTexture(
                streamer.SharedTextureHandle,
                streamer.SharedTextureWidth,
                streamer.SharedTextureHeight);
            if (sharedTextureSlot < 0)
            {
                Msg($"[StartStreaming] No free shared texture slots for: {title}");
                streamer.Dispose();
                root.Destroy();
                return;
            }
            int bridgeIndex = SharedTextureBridgeProtocol.MagicIndexBase + sharedTextureSlot;
            procTex.DisplayIndex.Value = bridgeIndex;
            Msg($"[StartStreaming] Shared texture bridge: slot {sharedTextureSlot}, bridgeIndex={bridgeIndex}, shared=0x{streamer.SharedTextureHandle.ToInt64():X}");
            int textureSlot = sharedTextureSlot;
            root.World.RunInUpdates(120, () =>
            {
                if (TextureBridgeChannel != null && !TextureBridgeChannel.IsTextureRunning(textureSlot))
                    Msg($"[StartStreaming] WARNING: Shared texture slot {textureSlot} did not report running after 120 updates");
            });
        }
        else if (hwnd == IntPtr.Zero && monitorIndex >= 0)
        {
            procTex.DisplayIndex.Value = monitorIndex;
            Msg($"[StartStreaming] WARNING: Shared texture bridge unavailable; falling back to built-in monitor DisplayIndex={monitorIndex}");
        }
        else
        {
            Msg($"[StartStreaming] WARNING: Cannot set up texture (hwnd={hwnd}, monitorIndex={monitorIndex}, bridge={(TextureBridgeChannel?.IsOpen ?? false)})");
        }
        Msg("[StartStreaming] Texture component created");

        var interactionSlot = displaySlot.AddSlot("InteractionCanvas");
        interactionSlot.LocalScale = float3.One;
        var interactionCanvas = interactionSlot.AttachComponent<Canvas>();
        interactionCanvas.Size.Value = new float2(w, h);
        var ui = new UIBuilder(interactionCanvas);
        ui.Canvas.Collider.Target.SetTrigger();

        var displayBg = ui.Image(new colorX(0f, 0f, 0f, 1f));
        displayBg.Tint.Value = colorX.Clear;
        ui.NestInto(displayBg.RectTransform);

        var rawImage = ui.RawImage(procTex);
        rawImage.UVRect.Value = new Rect(new float2(0f, 1f), new float2(1f, -1f));
        rawImage.Tint.Value = new colorX(1f, 1f, 1f, 0f);
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

        var displayCameraSlot = interactionSlot.AddSlot("InteractionCamera");
        displayCameraSlot.LocalPosition = float3.Zero;
        displayCameraSlot.LocalRotation = floatQ.Identity;
        displayRayExitRef = displayCameraSlot.AttachComponent<DesktopUVRayExit>();
        displayRayExitRef.Size = new float2(w, h);

        frontPlaneRef = AddCurvedTexturePlane(displaySlot, "FrontCurvedPlane", w, h, 1f, procTex, 0f, flipY: true, offsetUnits: 100f);
        ApplyPanelCurvature(currentPanelCurvature);

        WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
        Msg($"[StartStreaming] Process ID: {processId}");

        var seenRelatedHwnds = new HashSet<IntPtr>();
        if (processId != 0)
        {
            try
            {
                foreach (var win in WindowEnumerator.GetProcessWindows(processId))
                {
                    seenRelatedHwnds.Add(win.Handle);
                }
                Msg($"[StartStreaming] Baseline related windows for PID {processId}: {seenRelatedHwnds.Count}");
            }
            catch (Exception ex)
            {
                Msg($"[StartStreaming] Baseline related windows failed for PID {processId}: {ex.Message}");
            }
        }

        var session = new DesktopSession
        {
            Streamer = streamer,
            Texture = procTex,
            TextureImage = rawImage,
            Canvas = ui.Canvas,
            Root = root,
            Hwnd = hwnd,
            ProcessId = processId,
            Collider = collider,
            SharedTextureSlot = sharedTextureSlot,
            LastKnownW = w,
            LastKnownH = h,
            SeenRelatedHwnds = seenRelatedHwnds,
        };
        ActiveSessions.Add(session);
        DesktopCanvasIds.Add(ui.Canvas.ReferenceID);
        Msg($"[StartStreaming] Registered canvas {ui.Canvas.ReferenceID} for locomotion suppression");

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

        float2 GetDesktopPoint(ButtonEventData data)
        {
            float aspect = h > 0 ? (float)w / h : 1f;
            float u = 0.5f + (data.normalizedPressPoint.x - 0.5f) * aspect;
            return new float2(MathX.Clamp01(u), 1f - data.normalizedPressPoint.y);
        }

        void SendDesktopPressed(Component source, float2 point)
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if (IsDesktopMode(root.World)) return;
            ClaimSource(source);
            float u = point.x;
            float v = point.y;
            uint touchId = GetTouchId(source);
            session.ActiveTouchIds.Add(touchId);
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch down {touchId}");
        }

        void SendDesktopPressing(Component source, float2 point)
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if (IsDesktopMode(root.World)) return;
            uint touchId = GetTouchId(source);
            if (!session.ActiveTouchIds.Contains(touchId)) return;
            float u = point.x;
            float v = point.y;
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch move {touchId}");
        }

        void SendDesktopReleased(Component source, float2 point)
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if (IsDesktopMode(root.World)) return;
            uint touchId = GetTouchId(source);
            float u = point.x;
            float v = point.y;
            if (!session.ActiveTouchIds.Remove(touchId)) return;
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch up {touchId}");
        }

        void SendDesktopHovering(Component source, float2 point)
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if (IsDesktopMode(root.World)) return;
            float hu = point.x;
            float hv = point.y;

            if (IsActiveSource(source))
            {
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height, streamer.MonitorHandle);
            }

            var mouse = root.World.InputInterface.Mouse;
            if (mouse != null)
            {
                float scrollY = mouse.ScrollWheelDelta.Value.y;
                if (scrollY != 0)
                {
                    ClaimSource(source);
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendAtPointWhenTargetAcceptable(
                        hwnd,
                        hu,
                        hv,
                        streamer.Width,
                        streamer.Height,
                        streamer.MonitorHandle,
                        () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                        "mouse wheel");
                }
            }

            try
            {
                var handler = FindHandler(source);
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
                            ClaimSource(source);
                            int wheelDelta = (int)(axisY * 120f);
                            WindowInput.SendAtPointWhenTargetAcceptable(
                                hwnd,
                                hu,
                                hv,
                                streamer.Width,
                                streamer.Height,
                                streamer.MonitorHandle,
                                () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                                "controller wheel");
                        }
                    }
                    else
                    {
                        session.LastScrollSign = 0;
                    }
                }
            }
            catch { }
        }

        btn.LocalPressed += (IButton b, ButtonEventData data) =>
        {
            SendDesktopPressed(data.source, GetDesktopPoint(data));
        };

        btn.LocalPressing += (IButton b, ButtonEventData data) =>
        {
            SendDesktopPressing(data.source, GetDesktopPoint(data));
        };

        btn.LocalReleased += (IButton b, ButtonEventData data) =>
        {
            SendDesktopReleased(data.source, GetDesktopPoint(data));
        };

        btn.LocalHoverStay += (IButton b, ButtonEventData data) =>
        {
            if (grabbable != null && grabbable.IsGrabbed) return;
            if (IsDesktopMode(root.World)) return;
            float2 point = GetDesktopPoint(data);
            float hu = point.x;
            float hv = point.y;

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
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendAtPointWhenTargetAcceptable(
                        hwnd,
                        hu,
                        hv,
                        streamer.Width,
                        streamer.Height,
                        streamer.MonitorHandle,
                        () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                        "mouse wheel");
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
                            int wheelDelta = (int)(axisY * 120f);
                            WindowInput.SendAtPointWhenTargetAcceptable(
                                hwnd,
                                hu,
                                hv,
                                streamer.Width,
                                streamer.Height,
                                streamer.MonitorHandle,
                                () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                                "controller wheel");
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

        var directInput = frontPlaneRef.Slot.AttachComponent<DesktopCurvedScreenInput>();
        directInput.ScreenMesh = frontPlaneRef;
        directInput.Pressed = SendDesktopPressed;
        directInput.Pressing = SendDesktopPressing;
        directInput.Released = SendDesktopReleased;
        directInput.Hovering = SendDesktopHovering;

        float barH = 64f;
        float barMarginTop = 10f * canvasScale;
        float barPad = 8f;
        float barGap = 8f;
        float avatarW = 48f;
        float toggleW = 36f;
        const float deviceIndicatorTopOffset = 0.02f;
        float DeviceIndicatorY() => worldHalfH + deviceIndicatorTopOffset;
        float DeviceIndicatorZ() => -0.001f + GetCurvedPanelDepth(frontPlaneRef, canvasScale);
        void UpdateDeviceIndicators()
        {
            if (deviceIndicatorsSlot != null && !deviceIndicatorsSlot.IsDestroyed)
                deviceIndicatorsSlot.LocalPosition = new float3(0f, DeviceIndicatorY(), DeviceIndicatorZ());
        }

                var barRenderHost = root.World.RootSlot.AddSlot("DesktopBuddyTopBarRenderHost", false);
        root.Destroyed += _ =>
        {
            if (barRenderHost != null && !barRenderHost.IsDestroyed)
                barRenderHost.Destroy();
        };

        var barRenderRoot = barRenderHost.AddSlot("TopBarRender");
        barRenderRoot.AttachComponent<HiddenLayer>();
        var barBackRenderRoot = barRenderHost.AddSlot("TopBarBackRender");
        barBackRenderRoot.AttachComponent<HiddenLayer>();

        var barCameraSlot = barRenderHost.AddSlot("TopBarCamera");
        barCameraSlot.LocalPosition = new float3(0f, 0f, -1f);
        var barRenderTex = barCameraSlot.AttachComponent<RenderTextureProvider>();
        barRenderTex.Size.Value = new int2(w, (int)barH);
        barRenderTex.WrapModeU.Value = TextureWrapMode.Clamp;
        barRenderTex.WrapModeV.Value = TextureWrapMode.Clamp;

        var barCamera = barCameraSlot.AttachComponent<Camera>();
        barCamera.Projection.Value = CameraProjection.Orthographic;
        barCamera.OrthographicSize.Value = barH * 0.5f;
        barCamera.UseTransformScale.Value = false;
        barCamera.Clear.Value = CameraClearMode.Color;
        barCamera.ClearColor.Value = colorX.Clear;
        barCamera.NearClipping.Value = 0.01f;
        barCamera.FarClipping.Value = 4f;
        barCamera.Postprocessing.Value = false;
        barCamera.RenderShadows.Value = false;
        barCamera.ForwardOnly.Value = true;
        barCamera.RenderTexture.Target = barRenderTex;
        barCamera.SelectiveRender.Add(barRenderRoot);

        var barBackCameraSlot = barRenderHost.AddSlot("TopBarBackCamera");
        barBackCameraSlot.LocalPosition = new float3(0f, 0f, -1f);
        var barBackRenderTex = barBackCameraSlot.AttachComponent<RenderTextureProvider>();
        barBackRenderTex.Size.Value = new int2(w, (int)barH);
        barBackRenderTex.WrapModeU.Value = TextureWrapMode.Clamp;
        barBackRenderTex.WrapModeV.Value = TextureWrapMode.Clamp;

        var barBackCamera = barBackCameraSlot.AttachComponent<Camera>();
        barBackCamera.Projection.Value = CameraProjection.Orthographic;
        barBackCamera.OrthographicSize.Value = barH * 0.5f;
        barBackCamera.UseTransformScale.Value = false;
        barBackCamera.Clear.Value = CameraClearMode.Color;
        barBackCamera.ClearColor.Value = colorX.Clear;
        barBackCamera.NearClipping.Value = 0.01f;
        barBackCamera.FarClipping.Value = 4f;
        barBackCamera.Postprocessing.Value = false;
        barBackCamera.RenderShadows.Value = false;
        barBackCamera.ForwardOnly.Value = true;
        barBackCamera.RenderTexture.Target = barBackRenderTex;
        barBackCamera.SelectiveRender.Add(barBackRenderRoot);

        var barSlot = barRenderRoot.AddSlot("TopBar");
        barSlot.LocalScale = float3.One;

        var barCanvas = barSlot.AttachComponent<Canvas>();
        barCanvas.Collider.Target.SetTrigger();

        const float topBarBackgroundOffset = 500f;
        const float topBarForegroundOffset = -500f;
        const float topBarFillOffset = -1000f;
        const float topBarTopOffset = -1500f;
        const float topBarTextOffset = -2000f;

        var barMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barMat.BlendMode.Value = BlendMode.Alpha;
        barMat.ZWrite.Value = ZWrite.On;
        barMat.OffsetUnits.Value = topBarBackgroundOffset;

        var barElementMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barElementMat.BlendMode.Value = BlendMode.Alpha;
        barElementMat.Sidedness.Value = Sidedness.Front;
        barElementMat.ZWrite.Value = ZWrite.On;
        barElementMat.OffsetFactor.Value = -1f;
        barElementMat.OffsetUnits.Value = topBarForegroundOffset;

        var barFillMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barFillMat.BlendMode.Value = BlendMode.Alpha;
        barFillMat.Sidedness.Value = Sidedness.Front;
        barFillMat.ZWrite.Value = ZWrite.On;
        barFillMat.OffsetFactor.Value = -1f;
        barFillMat.OffsetUnits.Value = topBarFillOffset;

        var barTopMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barTopMat.BlendMode.Value = BlendMode.Alpha;
        barTopMat.Sidedness.Value = Sidedness.Front;
        barTopMat.ZWrite.Value = ZWrite.On;
        barTopMat.OffsetFactor.Value = -1f;
        barTopMat.OffsetUnits.Value = topBarTopOffset;

        var barTextMat = barSlot.AttachComponent<UI_TextUnlitMaterial>();
        barTextMat.BlendMode.Value = BlendMode.Alpha;
        barTextMat.Sidedness.Value = Sidedness.Front;
        barTextMat.ZWrite.Value = ZWrite.On;
        barTextMat.OffsetFactor.Value = -1f;
        barTextMat.OffsetUnits.Value = topBarTextOffset;

        var barBackSlot = barBackRenderRoot.AddSlot("TopBarBackPanel");
        barBackSlot.LocalScale = float3.One;

        var barBackCanvas = barBackSlot.AttachComponent<Canvas>();
        barBackCanvas.Collider.RawTarget.Enabled = false;

        var barBackMat = barBackSlot.AttachComponent<UI_UnlitMaterial>();
        barBackMat.BlendMode.Value = BlendMode.Alpha;
        barBackMat.Sidedness.Value = Sidedness.Double;
        barBackMat.ZWrite.Value = ZWrite.On;
        barBackMat.OffsetUnits.Value = topBarBackgroundOffset;

        var barBackTextMat = barBackSlot.AttachComponent<UI_TextUnlitMaterial>();
        barBackTextMat.BlendMode.Value = BlendMode.Alpha;
        barBackTextMat.Sidedness.Value = Sidedness.Double;
        barBackTextMat.ZWrite.Value = ZWrite.On;
        barBackTextMat.OffsetFactor.Value = -1f;
        barBackTextMat.OffsetUnits.Value = topBarTextOffset;

        var barUi = new UIBuilder(barCanvas);
        var barBg = barUi.Image(new colorX(0.1f, 0.1f, 0.12f, 1f));
        barBg.Material.Target = barMat;
        var roundedSprite = TextureProviderSettings.ClampWrap(barSlot.AttachComponent<SpriteProvider>());
        roundedSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        roundedSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        roundedSprite.FixedSize.Value = 16f;
        barBg.Sprite.Target = roundedSprite;
        barBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBg.Tint.Value = new colorX(0.1f, 0.1f, 0.12f, 1f);

        var barBackUi = new UIBuilder(barBackCanvas);
        var barBackBg = barBackUi.Image(new colorX(0.08f, 0.08f, 0.1f, 1f));
        barBackBg.Material.Target = barBackMat;
        barBackBg.Sprite.Target = roundedSprite;
        barBackBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBackUi.NestInto(barBackBg.RectTransform);
        var barBackLayout = barBackUi.HorizontalLayout(8f, padding: 10f, childAlignment: Alignment.MiddleLeft);
        barBackLayout.ForceExpandWidth.Value = false;

        barBackUi.Style.MinWidth = 40f;
        barBackUi.Style.PreferredWidth = 40f;
        barBackUi.Style.MinHeight = 40f;
        barBackUi.Style.PreferredHeight = 40f;
        barBackUi.Style.FlexibleWidth = -1f;
        barBackUi.Style.FlexibleHeight = -1f;

        StaticTexture2D barBackIconTex = hwnd != IntPtr.Zero
            ? ContextMenuPatch.GetIconTexture(hwnd, root.Engine, barBackSlot)
            : ContextMenuPatch.GetDesktopIconTexture(root.Engine, barBackSlot);
        if (barBackIconTex != null)
        {
            var barBackIconMat = barBackSlot.AttachComponent<UI_UnlitMaterial>();
            barBackIconMat.Texture.Target = barBackIconTex;
            barBackIconMat.BlendMode.Value = BlendMode.Alpha;
            barBackIconMat.Sidedness.Value = Sidedness.Double;
            barBackIconMat.ZWrite.Value = ZWrite.On;
            barBackIconMat.OffsetFactor.Value = -1f;
            barBackIconMat.OffsetUnits.Value = topBarForegroundOffset;

            var barBackIcon = barBackUi.RawImage(barBackIconTex);
            barBackIcon.PreserveAspect.Value = true;
            barBackIcon.Material.Target = barBackIconMat;
        }
        else
        {
            barBackUi.Empty("IconPlaceholder");
        }

        barBackUi.Style.MinWidth = 80f;
        barBackUi.Style.PreferredWidth = 180f;
        barBackUi.Style.MinHeight = 48f;
        barBackUi.Style.PreferredHeight = 48f;
        barBackUi.Style.FlexibleWidth = 1f;
        barBackUi.Style.FlexibleHeight = -1f;
        var barBackTitle = barBackUi.Text(title, bestFit: true, alignment: Alignment.MiddleLeft);
        barBackTitle.RectTransform.AddFixedPadding(0f, 0f, 0f, 4f);
        titleTextRef = barBackTitle.Slot.GetComponent<TextRenderer>();
        barBackTitle.Size.Value = 20f;
        barBackTitle.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f);
        barBackTitle.Material.Target = barBackTextMat;
        root.World.RunInUpdates(2, () =>
        {
            try
            {
                var autoMat = barBackTitle.Slot.GetComponentInParents<UI_TextUnlitMaterial>();
                if (autoMat != null)
                {
                    autoMat.Sidedness.Value = Sidedness.Double;
                    autoMat.OffsetFactor.Value = -1f;
                    autoMat.OffsetUnits.Value = topBarTextOffset;
                }
            }
            catch (Exception ex) { Msg($"[TopBarBackPanel] Text material fix error: {ex.Message}"); }
        });
        barBackUi.NestOut();

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
        var avatarMask = imageSpaceSlot.AttachComponent<Mask>();
        avatarMask.ShowMaskGraphic.Value = false;
        var imgMaskImage = imageSpaceSlot.GetComponent<Image>();
        var avatarMaskSprite = TextureProviderSettings.ClampWrap(imageSpaceSlot.AttachComponent<SpriteProvider>());
        avatarMaskSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        avatarMaskSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        avatarMaskSprite.FixedSize.Value = 18f;
        imgMaskImage.Sprite.Target = avatarMaskSprite;
        imgMaskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        imgMaskImage.Material.Target = barElementMat;

        barUi.NestInto(imageSpaceSlot);
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var cloudUserInfo = barSlot.AttachComponent<CloudUserInfo>();
        var defaultImg = new Uri("resdb:///bb7d7f1414e0c0a44b4684ecd2a5dc2086c18b3f70c9ed53d467fe96af94e9a9.png");
        var avatarTex = TextureProviderSettings.ClampWrap(barSlot.AttachComponent<StaticTexture2D>());
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

        var avatarImage = barUi.Image(avatarTex);
        avatarImage.Material.Target = barTopMat;
        var avatarButton = avatarImage.Slot.AttachComponent<Button>();
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
        nameText.Material.Target = barTextMat;

        float barCollapsedW = barPad * 2f + avatarW + barGap + nameW + barGap + toggleW;
        float expandContentW = 430f;
        float barExpandedW = barCollapsedW + barGap + expandContentW;

        void StyleButton(Button btn)
        {
            var textComp = btn.Slot.GetComponentInChildren<FrooxEngine.UIX.Text>();
            if (textComp != null)
            {
                textComp.Size.Value = 18f;
                textComp.Color.Value = new colorX(0.85f, 0.85f, 0.88f, 1f);
                textComp.Material.Target = barTextMat;
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

            var image = btn.Slot.GetComponent<Image>();
            if (image != null)
            {
                image.Tint.Value = colorX.Clear;
                image.Sprite.Target = null;
                image.Material.Target = barElementMat;
            }
        }

        FrooxEngine.User PressingUser(ButtonEventData data) => data.source?.Slot?.ActiveUser ?? root.World.LocalUser;

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
        barUi.Style.MinWidth = expandContentW;
        barUi.Style.PreferredWidth = expandContentW;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        var expandPanel = barUi.Empty("ExpandPanel");
        expandPanel.ActiveSelf = false;
        var ep = new UIBuilder(expandPanel);
        var epLayout = ep.HorizontalLayout(6f, padding: 6f, childAlignment: Alignment.MiddleLeft);
        epLayout.ForceExpandWidth.Value = false;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = 1f;

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var separatorA = ep.Image(new colorX(0.4f, 0.4f, 0.45f, 0.4f));
        separatorA.Material.Target = barElementMat;

        ep.Style.MinWidth = 30f;
        ep.Style.PreferredWidth = 30f;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;

        var kbBtn = ep.Button("⌨️"); StyleButton(kbBtn);
        var anchorBtn = ep.Button("⚓");   StyleButton(anchorBtn);
        var privateBtn = ep.Button("🔒"); StyleButton(privateBtn);

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        var separatorB = ep.Image(new colorX(0.4f, 0.4f, 0.45f, 0.4f));
        separatorB.Material.Target = barElementMat;

        ep.Style.MinWidth = 30f;
        ep.Style.PreferredWidth = 30f;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var resyncBtn = ep.Button("🔄");  StyleButton(resyncBtn);

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        var separatorC = ep.Image(new colorX(0.4f, 0.4f, 0.45f, 0.4f));
        separatorC.Material.Target = barElementMat;

        ep.Style.MinWidth = 38f;
        ep.Style.PreferredWidth = 38f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;
        ep.Style.FlexibleWidth = -1f;
        var curveText = ep.Text("Curve", bestFit: true, alignment: Alignment.MiddleCenter);
        curveText.Size.Value = 14f;
        curveText.Color.Value = new colorX(0.6f, 0.6f, 0.6f, 1f);
        curveText.Material.Target = barTextMat;

        ep.Style.FlexibleWidth = -1f;
        ep.Style.MinWidth = 80f;
        ep.Style.PreferredWidth = 80f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;

        var curveRow = ep.Empty("Curve");
        var curveUi = new UIBuilder(curveRow);
        curveUi.Style.FlexibleWidth = 1f;
        curveUi.Style.FlexibleHeight = 1f;
        var curveSlider = curveUi.Slider<float>(20f, currentPanelCurvature, 0f, 1f, false,
            out var curveLine, out var curveFillLine, out var curveHandle);
        curveLine.Material.Target = barElementMat;
        curveFillLine.Material.Target = barFillMat;
        curveHandle.Material.Target = barTopMat;
        curveRow.GetComponentInChildren<Image>(image => image.Slot.Name == "Background").Material.Target = barElementMat;
        curveRow.ForeachComponentInChildren<FrooxEngine.UIX.Text>(text => text.Material.Target = barTextMat);
        float pendingPanelCurvature = currentPanelCurvature;
        curveSlider.Value.OnValueChange += (SyncField<float> field) =>
        {
            pendingPanelCurvature = MathX.Clamp(field.Value, 0f, 1f);
            SetPanelCurvePreference(panelCurvePreferenceKey, pendingPanelCurvature);
        };
        curveSlider.IsPressed.OnValueChange += (SyncField<bool> field) =>
        {
            if (!field.Value)
            {
                ApplyPanelCurvature(pendingPanelCurvature);
                UpdateDeviceIndicators();
            }
        };

        ep.Style.MinWidth = 24f;
        ep.Style.PreferredWidth = 24f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;
        ep.Style.FlexibleWidth = -1f;
        var volIcon = ep.Text("🔊", bestFit: false, alignment: Alignment.MiddleCenter);
        volIcon.Size.Value = 16f;
        volIcon.Color.Value = new colorX(0.6f, 0.6f, 0.6f, 1f);
        volIcon.Material.Target = barTextMat;

        ep.Style.FlexibleWidth = -1f;
        ep.Style.MinWidth = 80f;
        ep.Style.PreferredWidth = 100f;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;

        var streamVolRow = ep.Empty("StreamVol");
        var streamVolUi = new UIBuilder(streamVolRow);
        streamVolUi.Style.FlexibleWidth = 1f;
        streamVolUi.Style.FlexibleHeight = 1f;
        var volSlider = streamVolUi.Slider<float>(20f, 1f, 0f, 1f, false,
            out var volLine, out var volFillLine, out var volHandle);
        volLine.Material.Target = barElementMat;
        volFillLine.Material.Target = barFillMat;
        volHandle.Material.Target = barTopMat;
        streamVolRow.GetComponentInChildren<Image>(image => image.Slot.Name == "Background").Material.Target = barElementMat;
        streamVolRow.ForeachComponentInChildren<FrooxEngine.UIX.Text>(text => text.Material.Target = barTextMat);

        var widthField = barSlot.AttachComponent<ValueField<float>>();
        widthField.Value.Value = barCollapsedW;
        var widthSmooth = barSlot.AttachComponent<SmoothValue<float>>();
        widthSmooth.Speed.Value = 10f;
        widthSmooth.TargetValue.Value = barCollapsedW;
        widthSmooth.Value.Target = widthField.Value;
        widthSmooth.WriteBack.Value = false;

        float barYPos = worldHalfH + barH / 2f * canvasScale + barMarginTop;
        widthField.Value.Value = barCollapsedW;
        widthSmooth.TargetValue.Value = barCollapsedW;
        float currentBarWidth = barCollapsedW;
        bool barExpanded = false;

        void ApplyBarLayout(float width)
        {
            if (barCanvas != null && !barCanvas.IsDestroyed)
                barCanvas.Size.Value = new float2(w, barH);

            if (barRenderTex != null && !barRenderTex.IsDestroyed)
                barRenderTex.Size.Value = new int2(w, (int)barH);

            if (barBackRenderTex != null && !barBackRenderTex.IsDestroyed)
                barBackRenderTex.Size.Value = new int2(w, (int)barH);

            if (barBg != null && !barBg.IsDestroyed)
                barBg.RectTransform.SetFixedRect(new Rect(0f, -barH * 0.5f, width, barH), new float2(0f, 0.5f));

            if (barSlot != null && !barSlot.IsDestroyed)
                barSlot.LocalPosition = float3.Zero;

            if (barBackCanvas != null && !barBackCanvas.IsDestroyed)
                barBackCanvas.Size.Value = new float2(w, barH);

            if (barBackBg != null && !barBackBg.IsDestroyed)
                barBackBg.RectTransform.SetFixedRect(new Rect(-width, -barH * 0.5f, width, barH), new float2(1f, 0.5f));

            if (topBarStripRef != null && !topBarStripRef.IsDestroyed)
            {
                topBarStripRef.Size.Value = new float2(w, barH);
                topBarStripRef.Slot.LocalPosition = new float3(0f, barYPos, 0f);
            }

            if (topBarBackStripRef != null && !topBarBackStripRef.IsDestroyed)
            {
                topBarBackStripRef.Size.Value = new float2(w, barH);
                topBarBackStripRef.Slot.LocalPosition = new float3(0f, barYPos, 0.004f);
            }
        }

        void BarUpdateLoop()
        {
            if (root == null || root.IsDestroyed ||
                barSlot == null || barSlot.IsDestroyed ||
                barCanvas == null || barCanvas.IsDestroyed ||
                widthField == null || widthField.IsDestroyed ||
                widthSmooth == null || widthSmooth.IsDestroyed)
                return;

            float width = widthField.Value.Value;
            if (width != currentBarWidth)
            {
                currentBarWidth = width;
                ApplyBarLayout(width);
            }

            float target = widthSmooth.TargetValue.Value;
            if (Math.Abs(width - target) > 0.5f)
                root.World.RunInUpdates(1, BarUpdateLoop);
        }

        toggleBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            if (root == null || root.IsDestroyed || widthSmooth == null || widthSmooth.IsDestroyed) return;
            barExpanded = !barExpanded;
            if (expandPanel != null && !expandPanel.IsDestroyed)
                expandPanel.ActiveSelf = barExpanded;
            widthSmooth.TargetValue.Value = barExpanded ? barExpandedW : barCollapsedW;
            root.World.RunInUpdates(1, BarUpdateLoop);
        };
        topBarStripRef = AddCurvedRenderPlane(
            root,
            "TopBarCurvedMesh",
            w,
            barH,
            canvasScale,
            barYPos,
            0f,
            barRenderTex,
            barCamera,
            barRenderRoot,
            addCollider: true,
            sidedness: Sidedness.Front,
            zWrite: ZWrite.Off,
            offsetUnits: 120f,
            blendMode: BlendMode.Alpha,
            renderQueue: 3001,
            alphaCutoff: 0.01f);
        topBarBackStripRef = AddCurvedRenderPlane(
            root,
            "TopBarBackCurvedMesh",
            w,
            barH,
            canvasScale,
            barYPos,
            0.004f,
            barBackRenderTex,
            null,
            null,
            addCollider: false,
            sidedness: Sidedness.Back,
            zWrite: ZWrite.Off,
            offsetUnits: 121f,
            blendMode: BlendMode.Alpha,
            renderQueue: 3002,
            alphaCutoff: 0.01f,
            textureScale: new float2(-1f, 1f),
            textureOffset: new float2(1f, 0f));
        ApplyPanelCurvature(currentPanelCurvature);
        ApplyBarLayout(barCollapsedW);
        root.World.RunInUpdates(1, BarUpdateLoop);

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

        ValueUserOverride<bool> streamVisRef = null;
        VideoTextureProvider videoTexRef = null;
        var previewUsers = new HashSet<FrooxEngine.User>();

        avatarButton.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            if (displaySlot == null || displaySlot.IsDestroyed ||
                streamVisRef == null || streamVisRef.IsDestroyed)
            {
                Msg("[Preview] No stream available");
                return;
            }

            var user = PressingUser(d);
            if (user == null)
            {
                Msg("[Preview] No pressing user");
                return;
            }

            bool streamPreview = !previewUsers.Contains(user);
            if (streamPreview)
                previewUsers.Add(user);
            else
                previewUsers.Remove(user);

            streamVisRef.SetOverride(user, streamPreview);
            if (user == root.World.LocalUser)
            {
                displaySlot.ActiveSelf = !streamPreview;
                avatarImage.Tint.Value = streamPreview
                    ? new colorX(1f, 0.05f, 0.03f, 1f)
                    : colorX.White;
            }

            Msg($"[Preview] {user.UserName}: stream={streamPreview}, direct={!streamPreview}");
        };

        resyncBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Resync] Button pressed");
            if (videoTexRef == null || videoTexRef.IsDestroyed)
            {
                Msg("[Resync] No stream available");
                return;
            }

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

        {
            deviceIndicatorsSlot = root.AddSlot("DeviceIndicators");
            deviceIndicatorsSlot.LocalPosition = new float3(0f, DeviceIndicatorY(), DeviceIndicatorZ());
            deviceIndicatorsSlot.LocalRotation = floatQ.Identity;
            deviceIndicatorsSlot.LocalScale = float3.One;

            var camSlot = deviceIndicatorsSlot.AddSlot("VirtualCamera");
            camSlot.LocalPosition = float3.Zero;
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
                var micSlot = deviceIndicatorsSlot.AddSlot("VirtualMic");
                micSlot.LocalPosition = new float3(0.03f, 0f, 0f);
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
                session.VMicSlot = micSlot;
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

        if (!(Config?.GetValue(SpatialAudioEnabled) ?? false))
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

        Canvas streamCanvasRef = null;

        {
            backPlaneRef = AddCurvedBackPlane(root, w, h, canvasScale);
            ApplyPanelCurvature(currentPanelCurvature);
            Msg("[BackPanel] Created curved backing");
        }

        if (!_updateShown && Config!.GetValue(CheckForUpdates))
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
        bool allowRemoteStream = useMediaMtx || (StreamServer != null && TunnelUrl != null);
        if (allowRemoteStream)
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

                bool shouldDriveEncoder;
                lock (_sharedStreams)
                {
                    shouldDriveEncoder = shared.DriverSession == null ||
                        shared.DriverSession.Cleaned ||
                        shared.DriverSession.Streamer == null;
                    if (shouldDriveEncoder)
                        shared.DriverSession = session;
                }

                if (shouldDriveEncoder)
                {
                    ConnectEncoder(session, nvEncoder);
                    Msg($"[RemoteStream] This panel drives the encoder for stream {shared.StreamId}");
                }
                else
                {
                    Msg($"[RemoteStream] This panel shares encoder from stream {shared.StreamId}, no encoding hook");
                }

                var videoSlot = root.AddSlot("StreamProvider");
                var videoTex = TextureProviderSettings.ClampWrap(videoSlot.AttachComponent<VideoTextureProvider>());
                videoTex.ForcePlaybackEngine.Value = "libVLC";
                videoTex.Stream.Value = true;
                videoTex.URL.Value = null;
                videoTex.Volume.Value = 0f;
                videoTexRef = videoTex;
                session.VideoTexture = videoTex;
                var streamUrl = shared.StreamUrl;
                bool waitForHttpKeyframe = !useMediaMtx;

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

                streamPlaneRef = AddCurvedTexturePlane(streamSlot, "VideoTextureCurvedPlane", w, h, 1f, videoTex, 0f, flipY: false, offsetUnits: -100f);
                ApplyPanelCurvature(currentPanelCurvature);

                var streamCanvas = streamSlot.AttachComponent<Canvas>();
                streamCanvas.Collider.RawTarget.Enabled = false;
                streamCanvasRef = streamCanvas;
                streamCanvas.Size.Value = new float2(w, h);
                var streamUi = new UIBuilder(streamCanvas);

                var streamBg = streamUi.Image(new colorX(0f, 0f, 0f, 1f));
                streamBg.Tint.Value = colorX.Clear;
                streamUi.NestInto(streamBg.RectTransform);

                var streamImg = streamUi.RawImage(videoTex);
                streamImg.Tint.Value = new colorX(1f, 1f, 1f, 0f);
                var streamMat = streamSlot.AttachComponent<UI_UnlitMaterial>();
                streamMat.BlendMode.Value = BlendMode.Alpha;
                streamMat.ZWrite.Value = ZWrite.On;
                streamMat.OffsetUnits.Value = -100f;
                streamImg.Material.Target = streamMat;

                Msg($"[RemoteStream] Created, URL={streamUrl}, streamId={shared.StreamId}, refs={shared.RefCount}");

                const int StreamBindRetryUpdates = 6;
                const int StreamBindMaxAttempts = 300;
                bool streamUrlBound = false;
                root.World.RunInUpdates(1, () => BindStreamUrlWhenReady(0));

                void BindStreamUrlWhenReady(int attempt)
                {
                    if (videoTex == null || videoTex.IsDestroyed || root.IsDestroyed) return;

                    bool ready = waitForHttpKeyframe
                        ? nvEncoder.HasReadableVideoKeyframe
                        : nvEncoder.IsRunning;

                    if (ready && !isPrivate)
                    {
                        videoTex.URL.Value = streamUrl;
                        videoTex.Play();
                        streamUrlBound = true;
                        Msg($"[RemoteStream] URL bound after encoder readiness: attempt={attempt} streamId={shared.StreamId} {nvEncoder.ReadableStreamState}");
                        return;
                    }

                    if (attempt >= StreamBindMaxAttempts)
                    {
                        Msg($"[RemoteStream] URL not bound: encoder did not become readable in time, private={isPrivate}, streamId={shared.StreamId}, {nvEncoder.ReadableStreamState}");
                        return;
                    }

                    if (attempt == 0 || attempt % 30 == 0)
                        Msg($"[RemoteStream] Waiting before URL bind: attempt={attempt}, private={isPrivate}, waitForHttpKeyframe={waitForHttpKeyframe}, {nvEncoder.ReadableStreamState}");

                    root.World.RunInUpdates(StreamBindRetryUpdates, () => BindStreamUrlWhenReady(attempt + 1));
                }

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
                    Msg($"[RemoteStream] Check #{checkCount}: urlBound={streamUrlBound} avail={assetAvail} engine={playbackEngine} playing={isPlaying} clockErr={clockErr:F2}");

                    if (streamUrlBound && assetAvail && !isPlaying)
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
            w = newW;
            h = newH;
            worldHalfW = newW / 2f * canvasScale;
            worldHalfH = newH / 2f * canvasScale;
            barYPos = worldHalfH + barH / 2f * canvasScale + barMarginTop;
            ApplyBarLayout(currentBarWidth);

            if (session.Collider != null && !session.Collider.IsDestroyed)
                session.Collider.Size.Value = new float3(newW * canvasScale, newH * canvasScale, 0.001f);

            if (ui.Canvas != null && !ui.Canvas.IsDestroyed)
                ui.Canvas.Size.Value = new float2(newW, newH);

            if (frontPlaneRef != null && !frontPlaneRef.IsDestroyed)
                frontPlaneRef.Size.Value = new float2(newW, newH);

            if (displayRayExitRef != null && !displayRayExitRef.IsDestroyed)
                displayRayExitRef.Size = new float2(newW, newH);

            if (backPlaneRef != null && !backPlaneRef.IsDestroyed)
                backPlaneRef.Size.Value = new float2(newW, newH);

            if (streamPlaneRef != null && !streamPlaneRef.IsDestroyed)
                streamPlaneRef.Size.Value = new float2(newW, newH);

            if (streamCanvasRef != null && !streamCanvasRef.IsDestroyed)
                streamCanvasRef.Size.Value = new float2(newW, newH);

            if (topBarStripRef != null && !topBarStripRef.IsDestroyed)
                topBarStripRef.Size.Value = new float2(newW, barH);

            if (keyboardSlot != null && keyboardSlot.ActiveSelf && !keyboardSlot.IsDestroyed)
                keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);

            UpdateDeviceIndicators();

            Msg($"[Resize] UI updated to {newW}x{newH}");
        }
        session.OnResize = UpdateLayout;

        root.PersistentSelf = false;
        root.Name = $"Desktop: {title}";
        session.TitleText = titleTextRef;
        session.LastTitle = title;

        ScheduleUpdate(root.World);

        root.Tag = "Desktop Buddy";
        bool focused = WindowInput.FocusWindow(hwnd);

        bool useSpatialAudio = Config?.GetValue(SpatialAudioEnabled) ?? false;
        if (useSpatialAudio && !isDesktopCapture && processId != 0 && VBCableSetup.IsInstalled())
        {
            string cableId = VBCableSetup.FindCableInputDeviceId();
            if (cableId != null)
            {
                AudioRouter.SetProcessOutputDevice(processId, cableId);
                session.OwnsAudioRedirect = true;
            }
        }

        Msg(focused
            ? $"[StartStreaming] Window focused, streaming started for: {title}"
            : $"[StartStreaming] Streaming started, but Windows did not foreground the window yet: {title}");
    }
}

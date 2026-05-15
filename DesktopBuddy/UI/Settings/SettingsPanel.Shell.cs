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
        state.BackgroundBlur = AddCurvedMeshBackdropBlur(mesh.Slot, mesh, 64, 0.012f);
        state.BackgroundBlurMask = TextureProviderSettings.ClampWrap(mesh.Slot.AttachComponent<StaticTexture2D>());
        UpdateSettingsBlurMask(state);
        RegisterTopBarRaycastPortal(mesh.Slot, renderRoot);

        BuildSettingsPanelShell(state, session);
        Msg("[Settings] Created shared curved settings panel");
    }

}

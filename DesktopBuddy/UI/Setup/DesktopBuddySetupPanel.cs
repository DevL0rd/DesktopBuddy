using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private const int SetupPanelWidth = 920;
    private const int SetupPanelHeight = 600;
    private const float SetupPanelScale = 0.00055f;

    private static Slot _setupPanelRoot;
    private static Canvas _setupPanelCanvas;
    private static Text _setupBodyText;
    private static Slot _setupInstallButtonSlot;
    private static readonly List<SetupStatusRowRefs> _setupStatusRows = new();
    private static bool _setupNoticeDismissed;
    private static bool _setupInstallInProgress;
    private static bool _setupCompleteAwaitingClose;
    private static DateTime _setupInstallStartedUtc;

    private sealed class SetupStatusRowRefs
    {
        public Text Name;
        public Text Detail;
        public Text Status;
        public Image Badge;
    }

    internal static bool ShowSetupNoticeFromDesktopClick(World world)
    {
        if (DesktopBuddyPlatform.IsLinux)
        {
            EnsureDependencyRuntimeStarted();
            return false;
        }

        if (_setupNoticeDismissed)
        {
            EnsureDependencyRuntimeStarted();
            return false;
        }

        if (_setupInstallInProgress)
        {
            if (_setupPanelRoot == null || _setupPanelRoot.IsDestroyed)
                ShowSetupPanel(world, DesktopBuddyFirstRunSetup.Check());
            return true;
        }

        if (_setupCompleteAwaitingClose)
        {
            if (_setupPanelRoot == null || _setupPanelRoot.IsDestroyed)
                ShowSetupPanel(world, DesktopBuddyFirstRunSetup.Check());
            return true;
        }

        var state = DesktopBuddyFirstRunSetup.Check();
        if (!state.HasIssues)
        {
            EnsureDependencyRuntimeStarted();
            return false;
        }

        return ShowSetupPanel(world, state);
    }

    private static World GetSetupPanelWorld()
    {
        try
        {
            var focused = Userspace.UserspaceWorld?.Engine?.WorldManager?.FocusedWorld;
            if (focused != null && !focused.IsDestroyed && focused != Userspace.UserspaceWorld)
                return focused;
        }
        catch
        {

        }

        return null;
    }

    private static bool ShowSetupPanel(World world, DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (world == null || world.IsDestroyed)
            return false;

        if (_setupPanelRoot != null && !_setupPanelRoot.IsDestroyed)
            return true;

        var localUser = world.LocalUser;
        if (localUser?.Root?.Slot == null)
        {
            Msg("[SetupPanel] Local user root is not available yet");
            return false;
        }

        var parent = localUser.Root.Slot?.Parent ?? world.RootSlot;
        _setupPanelRoot = parent.AddSlot("DesktopBuddy Setup", false);
        _setupPanelRoot.PersistentSelf = false;
        _setupPanelRoot.Destroyed += _ =>
        {
            var startRuntime = _setupCompleteAwaitingClose && !_setupNoticeDismissed;
            if (_setupPanelRoot != null && _setupPanelRoot.IsDestroyed)
                _setupPanelRoot = null;
            _setupPanelCanvas = null;
            _setupBodyText = null;
            _setupInstallButtonSlot = null;
            _setupStatusRows.Clear();
            if (startRuntime)
            {
                Msg("[SetupPanel] Setup panel closed after completion; continuing DesktopBuddy initialization");
                _setupNoticeDismissed = true;
                _setupCompleteAwaitingClose = false;
                EnsureDependencyRuntimeStarted();
            }
        };
        var destroyer = _setupPanelRoot.AttachComponent<DestroyOnUserLeave>();
        destroyer.TargetUser.Target = localUser;

        var headPos = localUser.Root.HeadPosition;
        var headRot = localUser.Root.HeadRotation;
        var forward = headRot * float3.Forward;
        _setupPanelRoot.GlobalPosition = headPos + forward * 1.05f;
        _setupPanelRoot.GlobalRotation = floatQ.LookRotation(forward, float3.Up);
        _setupPanelRoot.LocalScale = float3.One;

        ConfigureSetupGrabbableRoot(_setupPanelRoot);
        CreateSetupRenderSurface(_setupPanelRoot);

        BuildSetupPanel(state);
        Msg("[SetupPanel] Showing grabbable curved setup panel");
        return true;
    }

    private static void ConfigureSetupGrabbableRoot(Slot root)
    {
        var grabbable = root.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
    }

    private static void CreateSetupRenderSurface(Slot root)
    {
        var renderHost = root.AddLocalSlot("DesktopBuddySetupRenderHost", false);
        renderHost.PersistentSelf = false;
        renderHost.AttachComponent<HiddenLayer>();

        var renderRoot = renderHost.AddLocalSlot("SetupRender", false);
        renderRoot.PersistentSelf = false;
        renderRoot.AttachComponent<HiddenLayer>();

        var cameraSlot = renderHost.AddLocalSlot("SetupCamera", false);
        cameraSlot.PersistentSelf = false;
        cameraSlot.LocalPosition = new float3(0f, 0f, -1f);

        var renderTexture = cameraSlot.AttachComponent<RenderTextureProvider>();
        renderTexture.Size.Value = new int2(SetupPanelWidth, SetupPanelHeight);
        renderTexture.WrapModeU.Value = TextureWrapMode.Clamp;
        renderTexture.WrapModeV.Value = TextureWrapMode.Clamp;

        var camera = cameraSlot.AttachComponent<Camera>();
        camera.Projection.Value = CameraProjection.Orthographic;
        camera.OrthographicSize.Value = SetupPanelHeight * 0.5f;
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

        var canvasSlot = renderRoot.AddLocalSlot("SetupCanvas", false);
        canvasSlot.PersistentSelf = false;
        _setupPanelCanvas = canvasSlot.AttachComponent<Canvas>();
        _setupPanelCanvas.Size.Value = new float2(SetupPanelWidth, SetupPanelHeight);
        _setupPanelCanvas.Collider.Target.SetTrigger();
        var setupCanvasId = _setupPanelCanvas.ReferenceID;
        DesktopCanvasIds.Add(setupCanvasId);
        _setupPanelCanvas.Destroyed += _ => DesktopCanvasIds.Remove(setupCanvasId);

        var surfaceMesh = AddSetupCurvedRenderPlane(
            root,
            "SetupCurvedMesh",
            SetupPanelWidth,
            SetupPanelHeight,
            SetupPanelScale,
            0f,
            SettingsPanelZOffset,
            renderTexture,
            camera);
        AddCurvedMeshBackdropBlur(surfaceMesh.Slot, surfaceMesh, 64, 0.012f);
        RegisterTopBarRaycastPortal(surfaceMesh.Slot, renderRoot);
    }

    private static CurvedPlaneMesh AddSetupCurvedRenderPlane(
        Slot parent,
        string name,
        int width,
        float height,
        float scale,
        float yOffset,
        float zOffset,
        IAssetProvider<ITexture2D> texture,
        Camera rayExit)
    {
        var slot = parent.AddLocalSlot(name, false);
        slot.PersistentSelf = false;
        slot.LocalPosition = new float3(0f, yOffset, zOffset);
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

        if (rayExit != null)
        {
            var portal = slot.AttachComponent<MeshUVRaycastPortal>();
            portal.RayExit.Target = rayExit;
            portal.OverrideHitTriggers.Value = true;
            portal.RepeatUV.Value = false;
        }

        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = texture;
        material.BlendMode.Value = BlendMode.Alpha;
        material.AlphaCutoff.Value = 0.01f;
        material.Sidedness.Value = Sidedness.Front;
        material.ZWrite.Value = ZWrite.Off;
        material.OffsetUnits.Value = 80f;
        material.RenderQueue.Value = SettingsPanelRenderQueue;
        renderer.Materials.Add(material);

        return mesh;
    }

    private static void BuildSetupPanel(DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (_setupPanelCanvas == null || _setupPanelCanvas.IsDestroyed)
            return;

        _setupBodyText = null;
        _setupInstallButtonSlot = null;
        _setupStatusRows.Clear();
        _setupPanelCanvas.Slot.DestroyChildren();
        var ui = new UIBuilder(_setupPanelCanvas);
        ConfigureSetupCanvasStyle(ui);

        var bg = ui.Image(SettingsBg);
        bg.Sprite.Target = CreateRoundedSprite(bg.Slot, ui.Root.World, 22f);
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.VerticalLayout(10f, paddingTop: 20f, paddingRight: 24f, paddingBottom: 20f, paddingLeft: 24f, childAlignment: Alignment.TopLeft);

        AddSetupHeader(ui);
        AddSetupBodyText(ui, GetSetupPanelMessage(state));

        foreach (var item in state.Items)
            AddSetupStatusRow(ui, item);

        ui.Style.FlexibleHeight = 1f;
        ui.Empty("Spacer");

        AddSetupActions(ui, state);
        ui.NestOut();
    }

    private static void ConfigureSetupCanvasStyle(UIBuilder ui)
    {
        ui.Style.MinWidth = SetupPanelWidth;
        ui.Style.PreferredWidth = SetupPanelWidth;
        ui.Style.MinHeight = SetupPanelHeight;
        ui.Style.PreferredHeight = SetupPanelHeight;
        ui.Style.FlexibleWidth = 0f;
        ui.Style.FlexibleHeight = 0f;
    }

    private static void AddSetupHeader(UIBuilder ui)
    {
        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        var row = ui.Empty("Header");
        ui.NestInto(row);
        ui.LayoutTarget = row;
        ui.HorizontalLayout(12f, childAlignment: Alignment.MiddleLeft);

        ui.Style.MinWidth = 6f;
        ui.Style.PreferredWidth = 6f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleWidth = -1f;
        var accent = ui.Image(SettingsAccent);
        accent.Sprite.Target = CreateRoundedSprite(accent.Slot, ui.Root.World, 6f);
        accent.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ApplyPurpleBlueGradient(accent, 6f, 1f, interactionTarget: false);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        var title = ui.Text("DesktopBuddy Setup", bestFit: true, alignment: Alignment.MiddleLeft);
        title.Size.Value = 30f;
        title.Color.Value = SettingsText;
        ui.NestOut();
    }

    private static void AddSetupBodyText(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.Style.FlexibleWidth = 1f;
        var label = ui.Text(text ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        label.Size.Value = 17f;
        label.Color.Value = SettingsSubtext;
        _setupBodyText = label;
    }

    private static void AddSetupStatusRow(UIBuilder ui, DesktopBuddyFirstRunSetup.SetupItem item)
    {
        const float badgeWidth = 112f;
        const float badgeHeight = 30f;

        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var row = ui.Image(SettingsPanel);
        row.Sprite.Target = CreateRoundedSprite(row.Slot, ui.Root.World, 14f);
        row.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(row.RectTransform);
        ui.LayoutTarget = row.Slot;
        ui.HorizontalLayout(12f, paddingTop: 8f, paddingRight: 12f, paddingBottom: 8f, paddingLeft: 14f, childAlignment: Alignment.MiddleCenter);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 40f;
        ui.Style.PreferredHeight = 40f;
        var textRoot = ui.Empty("Text");
        ui.NestInto(textRoot);
        ui.LayoutTarget = textRoot;
        ui.VerticalLayout(2f, childAlignment: Alignment.MiddleLeft);

        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 24f;
        ui.Style.PreferredHeight = 24f;
        var name = ui.Text(item.Name ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        name.Size.Value = 17f;
        name.Color.Value = SettingsText;

        ui.Style.MinHeight = 22f;
        ui.Style.PreferredHeight = 22f;
        var detail = ui.Text(item.Detail ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        detail.Size.Value = 13f;
        detail.Color.Value = SettingsSubtext;
        ui.NestOut();

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = badgeWidth;
        ui.Style.PreferredWidth = badgeWidth;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        var color = item.IsOk ? SettingsStatusGood : SettingsStatusBad;
        var badge = ui.Image(color);
        ApplyFixedLayout(badge.Slot, badgeWidth, badgeHeight);
        StyleBadgePill(badge, color);
        ui.NestInto(badge.RectTransform);
        ui.LayoutTarget = badge.Slot;
        var badgeLayout = ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        badgeLayout.ForceExpandWidth.Value = true;
        badgeLayout.ForceExpandHeight.Value = true;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = badgeHeight;
        ui.Style.PreferredHeight = badgeHeight;
        var status = ui.Text(item.Status ?? "", bestFit: true, alignment: Alignment.MiddleCenter);
        status.Size.Value = 13f;
        status.Color.Value = SettingsText;
        ui.NestOut();
        ui.NestOut();
        _setupStatusRows.Add(new SetupStatusRowRefs
        {
            Name = name,
            Detail = detail,
            Status = status,
            Badge = badge
        });
    }

    private static void ApplyFixedLayout(Slot slot, float width, float height)
    {
        if (slot == null)
            return;

        var element = slot.GetComponent<LayoutElement>() ?? slot.AttachComponent<LayoutElement>();
        element.MinWidth.Value = width;
        element.PreferredWidth.Value = width;
        element.FlexibleWidth.Value = -1f;
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
        element.FlexibleHeight.Value = -1f;
    }

    private static void AddSetupActions(UIBuilder ui, DesktopBuddyFirstRunSetup.SetupState state)
    {
        ui.Style.MinHeight = 40f;
        ui.Style.PreferredHeight = 40f;
        ui.Style.FlexibleWidth = 1f;
        var row = ui.Empty("Actions");
        ui.NestInto(row);
        ui.LayoutTarget = row;
        ui.HorizontalLayout(12f, childAlignment: Alignment.MiddleRight);

        AddSetupButton(ui, "Close", false, CloseSetupPanel);

        if (state.HasRequiredActions && !_setupCompleteAwaitingClose && !_setupInstallInProgress)
        {
            var install = AddSetupButton(ui, "Install", true, () => InstallSetupActions(state));
            _setupInstallButtonSlot = install?.Slot;
        }

        ui.NestOut();
    }

    private static string GetSetupPanelMessage(DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (_setupInstallInProgress)
            return "Installing required Windows setup.";

        if (!state.HasIssues)
            return _setupCompleteAwaitingClose
                ? "Setup complete. Close to start DesktopBuddy."
                : "DesktopBuddy setup is complete.";

        bool hasPackageIssues = state.Items.Any(item => !item.IsOk && !item.RequiresAdminAction);
        if (state.HasRequiredActions && hasPackageIssues)
            return "Install Windows setup. Restore missing package files separately.";

        if (state.HasRequiredActions)
            return "Install required Windows setup.";

        return "Package files are missing. DesktopBuddy will still load.";
    }

    private static Button AddSetupButton(UIBuilder ui, string label, bool primary, Action pressed)
    {
        ui.Style.MinWidth = primary ? 126f : 108f;
        ui.Style.PreferredWidth = primary ? 126f : 108f;
        ui.Style.MinHeight = 32f;
        ui.Style.PreferredHeight = 32f;
        ui.Style.FlexibleWidth = -1f;

        var button = ui.Button(label, primary ? SettingsAccent : SettingsPanelSoft);
        StyleSettingsButton(button, primary);
        button.LocalPressed += (_, _) => pressed?.Invoke();
        return button;
    }

    private static void InstallSetupActions(DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (_setupInstallInProgress)
            return;

        if (state?.HasRequiredActions != true)
            return;

        Msg("[SetupPanel] Install pressed");
        _setupInstallInProgress = true;
        _setupCompleteAwaitingClose = false;
        _setupInstallStartedUtc = DateTime.UtcNow;
        UpdateSetupPanelState(state);

        var actions = state.RequiredActions.ToArray();
        try
        {
            var process = DesktopBuddyFirstRunSetup.StartElevatedSetup(actions);
            if (process == null)
            {
                _setupInstallInProgress = false;
                var current = DesktopBuddyFirstRunSetup.Check();
                if (!current.HasIssues)
                    ShowSetupCompletePanel();
                else
                    UpdateSetupPanelState(current);
                return;
            }

            try { process?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            Msg($"[SetupPanel] Install failed: {ex.Message}");
            _setupInstallInProgress = false;
            UpdateSetupPanelState(DesktopBuddyFirstRunSetup.Check());
            return;
        }

        ScheduleSetupInstallPoll();
    }

    private static void ScheduleSetupInstallPoll()
    {
        var world = _setupPanelRoot?.World ?? GetSetupPanelWorld();
        if (world == null || world.IsDestroyed)
            return;

        world.RunInUpdates(120, PollSetupInstall);
    }

    private static void PollSetupInstall()
    {
        if (!_setupInstallInProgress)
            return;

        var state = DesktopBuddyFirstRunSetup.Check();
        if (!state.HasIssues)
        {
            _setupInstallInProgress = false;
            ShowSetupCompletePanel();
            return;
        }

        if (DateTime.UtcNow - _setupInstallStartedUtc > TimeSpan.FromMinutes(3))
        {
            Msg("[SetupPanel] Install did not complete; showing current setup status");
            _setupInstallInProgress = false;
            UpdateSetupPanelState(state);
            return;
        }

        UpdateSetupBodyText(state);
        ScheduleSetupInstallPoll();
    }

    private static void UpdateSetupPanelState(DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (_setupPanelCanvas == null || _setupPanelCanvas.IsDestroyed || state == null)
            return;

        if (_setupStatusRows.Count != state.Items.Count)
        {
            BuildSetupPanel(state);
            return;
        }

        UpdateSetupBodyText(state);
        for (int i = 0; i < state.Items.Count; i++)
            UpdateSetupStatusRow(_setupStatusRows[i], state.Items[i]);

        if (_setupInstallButtonSlot != null && !_setupInstallButtonSlot.IsDestroyed)
            _setupInstallButtonSlot.ActiveSelf = state.HasRequiredActions && !_setupInstallInProgress && !_setupCompleteAwaitingClose;
    }

    private static void UpdateSetupBodyText(DesktopBuddyFirstRunSetup.SetupState state)
    {
        if (_setupBodyText != null && !_setupBodyText.IsDestroyed)
            _setupBodyText.Content.Value = GetSetupPanelMessage(state);
    }

    private static void UpdateSetupStatusRow(SetupStatusRowRefs row, DesktopBuddyFirstRunSetup.SetupItem item)
    {
        if (row == null || item == null)
            return;

        if (row.Name != null && !row.Name.IsDestroyed)
            row.Name.Content.Value = item.Name ?? "";
        if (row.Detail != null && !row.Detail.IsDestroyed)
            row.Detail.Content.Value = item.Detail ?? "";
        if (row.Status != null && !row.Status.IsDestroyed)
            row.Status.Content.Value = item.Status ?? "";
        if (row.Badge != null && !row.Badge.IsDestroyed)
        {
            var color = item.IsOk ? SettingsStatusGood : SettingsStatusBad;
            row.Badge.Tint.Value = color;
            StyleBadgePill(row.Badge, color);
        }
    }

    private static void ShowSetupCompletePanel()
    {
        Msg("[SetupPanel] Local setup complete; waiting for close");
        _setupInstallInProgress = false;
        _setupCompleteAwaitingClose = true;
        UpdateSetupPanelState(DesktopBuddyFirstRunSetup.Check());
    }

    private static void CloseSetupPanel()
    {
        Msg("[SetupPanel] Setup notice closed");
        if (_setupCompleteAwaitingClose)
        {
            if (_setupPanelRoot != null && !_setupPanelRoot.IsDestroyed)
            {
                _setupPanelRoot.Destroy();
                return;
            }

            _setupNoticeDismissed = true;
            _setupCompleteAwaitingClose = false;
            EnsureDependencyRuntimeStarted();
            return;
        }

        _setupNoticeDismissed = true;
        _setupCompleteAwaitingClose = false;
        if (_setupPanelRoot != null && !_setupPanelRoot.IsDestroyed)
            _setupPanelRoot.Destroy();

        EnsureDependencyRuntimeStarted();
    }
}

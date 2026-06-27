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

    private static void BuildDevicesTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Virtual Devices");
        bool softCamRegistered = SoftCam.IsFilterRegistered();
        bool vbCableInstalled = VBCable.HasCableInputDevice();
        AddStatusRow(ui, state, "SoftCam", softCamRegistered ? "Installed" : "Missing", softCamRegistered ? SettingsStatusGood : SettingsStatusBad);
        AddStatusRow(ui, state, "VB-Cable", vbCableInstalled ? "Installed" : "Missing", vbCableInstalled ? SettingsStatusGood : SettingsStatusBad);
        AddCheckbox(ui, state, "Virtual camera enabled", VCam != null && !VCam.ManuallyDisabled, value =>
        {
            if (VCam != null) VCam.ManuallyDisabled = !value;
        });
        if (DesktopBuddyPlatform.IsLinux)
        {
            AddButtonRow(ui, state, "Virtual camera setup (v4l2loopback)", () => LinuxVirtualCameraSetup.Run(), buttonLabel: "Install");
        }
        AddVirtualCameraPreview(ui, state, session);
        AddCheckbox(ui, state, "Virtual mic muted", session?.VMicMuted ?? false, value =>
        {
            if (session != null) session.VMicMuted = value;
            if (VMic != null) VMic.Muted = value;
        });
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

}

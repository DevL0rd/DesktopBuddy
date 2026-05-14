using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static Slot CreateVirtualDeviceControls(Slot root, DesktopSession session, float y, float z, bool spatialAudio)
    {
        var deviceIndicatorsSlot = root.AddSlot("DeviceIndicators");
        deviceIndicatorsSlot.LocalPosition = new float3(0f, y, z);
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

        return deviceIndicatorsSlot;
    }
}

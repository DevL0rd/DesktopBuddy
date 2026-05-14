using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    private const int SettingsBackdropBlurRenderQueue = 2990;
    private const int SettingsUiRenderQueue = 3004;
    private const float TopBarSurfaceZOffset = -0.006f;
    private const float TopBarBackZOffset = 0.004f;
    private const float TopBarHoverCollapseDelaySeconds = 2f;
    private static int _nextTopBarRenderHostId;

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

    private static CurvedPlaneMesh AddCurvedMeshOnly(Slot parent, string name, float width, float height, float scale, float yOffset, float zOffset)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition = new float3(0f, yOffset, zOffset);
        slot.LocalScale = float3.One * scale;

        var mesh = slot.AttachComponent<CurvedPlaneMesh>();
        mesh.Size.Value = new float2(width, height);
        mesh.Curvature.Value = DesktopPanelCurvature;
        mesh.AspectRatioCompensation.Value = CurvedPlaneMesh.CurvatureAspectRatioCompensation.DecreaseWidth;
        mesh.Segments.Value = DesktopPanelCurveSegments;
        return mesh;
    }

    private static BlurMaterial AddCurvedMeshBackdropBlur(Slot slot, CurvedPlaneMesh mesh, int iterations, float spread, int renderQueue)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;

        var material = slot.AttachComponent<BlurMaterial>();
        material.Iterations.Value = iterations;
        material.Spread.Value = float2.One * spread;
        material.PerObject.Value = true;
        material.RenderQueue.Value = renderQueue;
        material.ZWrite.Value = ZWrite.Off;
        material.ZTest.Value = FrooxEngine.ZTest.LessOrEqual;
        material.Sidedness.Value = Sidedness.Front;
        material.BlendMode.Value = BlendMode.Opaque;
        material.UsePoissonDisc.Value = true;
        material.Refract.Value = false;
        material.DepthFadeDivisor.Value = 1f;
        renderer.Materials.Add(material);
        return material;
    }

    private static float GetCurvedPanelDepth(CurvedPlaneMesh mesh, float scale)
    {
        if (mesh == null || mesh.IsDestroyed)
            return 0f;

        return GetCurvedPanelDepthAtU(mesh, 0.5f, scale);
    }

}

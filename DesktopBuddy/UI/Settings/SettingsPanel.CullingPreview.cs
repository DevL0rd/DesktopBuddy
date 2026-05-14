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

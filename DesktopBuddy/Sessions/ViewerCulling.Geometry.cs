using System;
using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void UpdateViewerCullingTrigger(DesktopSession session)
    {
        if (session?.CullingTriggerSlot == null || session.CullingTriggerSlot.IsDestroyed)
            return;

        float range = Math.Clamp(Config?.GetValue(ViewerDistance) ?? Config?.GetValue(ViewerFrustumDepth) ?? 3f, 1f, 10f);
        string mode = NormalizeViewerCullingMode(Config?.GetValue(ViewerCullingMode));
        float originZ = GetCullingPreviewOriginZ(session);

        if (session.CullingSphereCollider != null && !session.CullingSphereCollider.IsDestroyed)
        {
            session.CullingSphereCollider.Enabled = mode == "distance";
            session.CullingSphereCollider.Radius.Value = range;
            session.CullingSphereCollider.Offset.Value = new float3(0f, 0f, originZ);
        }

        if (session.CullingFrustumCollider != null && !session.CullingFrustumCollider.IsDestroyed)
        {
            int panelPixelsW = session.LastKnownW > 0 ? session.LastKnownW : MathX.RoundToInt(session.Canvas?.Size.Value.x ?? 0f);
            int panelPixelsH = session.LastKnownH > 0 ? session.LastKnownH : MathX.RoundToInt(session.Canvas?.Size.Value.y ?? 0f);
            float scale = session.PanelCanvasScale > 0f ? session.PanelCanvasScale : 0.0005f;
            float panelW = Math.Max(1, panelPixelsW) * scale;
            float panelH = Math.Max(1, panelPixelsH) * scale;
            float angle = NormalizeViewerFrustumAngle(Config?.GetValue(ViewerFrustumWidth) ?? 120f);
            float verticalAngle = angle * 0.5f;
            float farHalfW = panelW * 0.5f + MathF.Tan(angle * MathF.PI / 360f) * range;
            float farHalfH = panelH * 0.5f + MathF.Tan(verticalAngle * MathF.PI / 360f) * range;

            session.CullingFrustumCollider.Enabled = mode != "distance";
            session.CullingFrustumCollider.Size.Value = new float3(farHalfW * 2f, farHalfH * 2f, range);
            session.CullingFrustumCollider.Offset.Value = new float3(0f, 0f, originZ - range * 0.5f);
        }
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
}

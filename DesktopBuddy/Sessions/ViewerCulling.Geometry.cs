using System;
using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
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

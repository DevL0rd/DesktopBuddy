using System;
using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public class DesktopCurvedScreenInput : Component, ITouchable
{
    private const float BoundsEpsilon = 0.001f;

    public CurvedPlaneMesh ScreenMesh;
    public Func<bool> ShouldIgnore;
    public Action<Component, float2> Pressed;
    public Action<Component, float2> Pressing;
    public Action<Component, float2> Released;
    public Action<Component, float2> Hovering;

    public bool AcceptsExistingTouch => false;
    public bool CanTouchOutOfSight => false;

    public bool CanTouchInteract(TouchSource source)
    {
        return source != null && source.SafeTouchSource;
    }

    public void OnTouch(in TouchEventInfo eventInfo)
    {
        if (ShouldIgnore?.Invoke() == true)
            return;

        if (eventInfo.source == null || !eventInfo.source.SafeTouchSource)
            return;

        if (!TryGetDesktopPoint(eventInfo.point, out float2 point))
            return;

        Component source = eventInfo.source;

        if (eventInfo.hover == EventState.Begin || eventInfo.hover == EventState.Stay)
            Hovering?.Invoke(source, point);

        if (eventInfo.touch == EventState.Begin)
            Pressed?.Invoke(source, point);
        else if (eventInfo.touch == EventState.Stay)
            Pressing?.Invoke(source, point);
        else if (eventInfo.touch == EventState.End)
            Released?.Invoke(source, point);
    }

    private bool TryGetDesktopPoint(in float3 globalPoint, out float2 point)
    {
        point = default;
        CurvedPlaneMesh mesh = ScreenMesh;
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
        if (u < -BoundsEpsilon || u > 1f + BoundsEpsilon ||
            v < -BoundsEpsilon || v > 1f + BoundsEpsilon)
        {
            return false;
        }

        point = new float2(MathX.Clamp01(u), MathX.Clamp01(v));
        return true;
    }
}

using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public class DesktopUVRayExit : Component, IUVToRayConverter
{
    public float2 Size;
    public float BackOffset = 10f;

    public void UVToRay(float2 uv, out float3 rayOrigin, out float3 rayDirection)
    {
        float3 localPoint = new float3((uv.x - 0.5f) * Size.x, (uv.y - 0.5f) * Size.y, -BackOffset);
        rayOrigin = Slot.LocalPointToGlobal(in localPoint);
        rayDirection = Slot.Forward;
    }
}

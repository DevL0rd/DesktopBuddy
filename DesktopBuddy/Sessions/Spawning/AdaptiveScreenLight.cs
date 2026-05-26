using System;
using System.Diagnostics;
using System.Threading;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static readonly long AdaptiveLightSampleIntervalTicks = Stopwatch.Frequency / 10;
    private const float AdaptiveLightSurfaceZOffset = -0.02f;

    private static void CreateAdaptiveScreenLight(Slot root, DesktopSession session, IntPtr hwnd, IntPtr monitorHandle)
    {
        if (root == null || root.IsDestroyed || session == null)
            return;

        var lightSlot = root.AddSlot("DesktopBuddyAdaptiveLight");
        lightSlot.PersistentSelf = false;
        lightSlot.LocalPosition = new float3(0f, 0f, GetAdaptiveLightZOffset(session));

        var light = lightSlot.AttachComponent<Light>();
        light.LightType.Value = LightType.Point;
        light.Intensity.Value = 1f;
        light.Range.Value = 15f;
        light.ShadowType.Value = ShadowType.Soft;
        light.ShadowStrength.Value = 1f;
        light.Color.Value = new colorX(1f, 1f, 1f, 1f);
        session.AdaptiveLight = light;
        session.AdaptiveLightSampler = new D3D11AverageColorSampler();
    }

    private static void UpdateAdaptiveScreenLightPosition(DesktopSession session)
    {
        if (session?.AdaptiveLight == null || session.AdaptiveLight.IsDestroyed)
            return;

        session.AdaptiveLight.Slot.LocalPosition = new float3(0f, 0f, GetAdaptiveLightZOffset(session));
    }

    private static float GetAdaptiveLightZOffset(DesktopSession session)
    {
        float canvasScale = session != null && session.PanelCanvasScale > 0f ? session.PanelCanvasScale : 1f;
        return GetCurvedPanelDepth(session?.PanelMesh, canvasScale) + AdaptiveLightSurfaceZOffset;
    }

    private static void TryUpdateAdaptiveScreenLightFromGpuFrame(DesktopSession session, IntPtr device, IntPtr context, IntPtr texture, int width, int height)
    {
        if (session?.AdaptiveLight == null || session.AdaptiveLight.IsDestroyed ||
            session.AdaptiveLightSampler == null ||
            device == IntPtr.Zero || context == IntPtr.Zero || texture == IntPtr.Zero ||
            width <= 0 || height <= 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref session.AdaptiveLightLastSampleTicks);
        if (last != 0 && now - last < AdaptiveLightSampleIntervalTicks)
            return;
        Interlocked.Exchange(ref session.AdaptiveLightLastSampleTicks, now);

        colorX color;
        try
        {
            if (!session.AdaptiveLightSampler.TrySample(device, context, texture, width, height, out color))
                return;
        }
        catch (Exception ex)
        {
            Msg($"[AdaptiveLight] GPU average sample failed: {ex.Message}");
            return;
        }

        var root = session.Root;
        if (root == null || root.IsDestroyed)
            return;

        root.World.RunInUpdates(0, () =>
        {
            if (session.Cleaned || root.IsDestroyed || session.AdaptiveLight == null || session.AdaptiveLight.IsDestroyed)
                return;
            session.AdaptiveLight.Color.Value = color;
        });
    }
}

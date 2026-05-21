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

    private static void ResizeSettingsPanel(DesktopSession session, int width, int height, float canvasScale, float curvature)
    {
        var state = session?.SettingsPanel;
        if (state == null) return;

        (state.ModalWidth, state.ModalHeight) = GetSettingsModalSize(width, height);
        state.RenderWidth = state.ModalWidth;
        state.RenderHeight = state.ModalHeight;
        state.CanvasScale = canvasScale;

        if (state.RenderTexture != null && !state.RenderTexture.IsDestroyed)
            state.RenderTexture.Size.Value = new int2(state.RenderWidth, state.RenderHeight);
        if (state.Camera != null && !state.Camera.IsDestroyed)
            state.Camera.OrthographicSize.Value = state.RenderHeight * 0.5f;
        if (state.Canvas != null && !state.Canvas.IsDestroyed)
            state.Canvas.Size.Value = new float2(state.RenderWidth, state.RenderHeight);
        if (state.Mesh != null && !state.Mesh.IsDestroyed)
        {
            state.Mesh.Size.Value = new float2(state.ModalWidth, state.ModalHeight);
            state.Mesh.Curvature.Value = curvature;
            state.Mesh.Slot.LocalScale = float3.One * canvasScale;
            state.Mesh.Slot.LocalPosition = new float3(0f, 0f, SettingsPanelZOffset);
        }
        UpdateSettingsBlurMask(state);
        SetSettingsModalRect(state);
        UpdateCullingPreview(session, state);
    }

    private static void SyncLiveCullingStateFromConfig(SettingsPanelState state)
    {
        if (state == null) return;
        state.ViewerCullingPreviewEnabled = Config?.GetValue(ViewerCullingPreview) ?? false;
        state.ViewerCullingMode = NormalizeViewerCullingMode(Config?.GetValue(ViewerCullingMode));
        state.ViewerFrustumAngle = NormalizeViewerFrustumAngle(Config?.GetValue(ViewerFrustumWidth) ?? 120f);
        float range = Math.Clamp(Config?.GetValue(ViewerDistance) ?? Config?.GetValue(ViewerFrustumDepth) ?? 3f, 1f, 10f);
        state.ViewerFrustumDepth = range;
        state.ViewerDistance = range;
    }

    private static void SetSettingsModalRect(SettingsPanelState state)
    {
        if (state?.ModalRect == null || state.ModalRect.IsDestroyed)
            return;

        state.ModalRect.SetFixedRect(
            new Rect(state.ModalWidth * -0.5f, state.ModalHeight * -0.5f, state.ModalWidth, state.ModalHeight),
            new float2(0.5f, 0.5f));
    }

    private static void UpdateSettingsBlurMask(SettingsPanelState state)
    {
        if (state?.BackgroundBlur == null || state.BackgroundBlur.IsDestroyed ||
            state.BackgroundBlurMask == null || state.BackgroundBlurMask.IsDestroyed ||
            state.OwnerRoot == null || state.OwnerRoot.IsDestroyed)
            return;

        int modalW = Math.Max(1, state.ModalWidth);
        int modalH = Math.Max(1, state.ModalHeight);
        if (state.BackgroundBlurMaskWidth == modalW && state.BackgroundBlurMaskHeight == modalH)
            return;

        state.BackgroundBlurMaskWidth = modalW;
        state.BackgroundBlurMaskHeight = modalH;

        var tex = state.BackgroundBlurMask;
        var blur = state.BackgroundBlur;
        var engine = state.OwnerRoot.Engine;
        byte[] data = CreateRoundedMaskPixels(modalW, modalH, 28f, out int texW, out int texH);

        Task.Run(async () =>
        {
            try
            {
                var bitmap = new Bitmap2D(data, texW, texH, Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.Linear, false);
                var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                if (uri == null)
                    return;

                tex.World.RunInUpdates(0, () =>
                {
                    if (tex.IsDestroyed || blur.IsDestroyed)
                        return;

                    tex.URL.Value = uri;
                    blur.SpreadMagnitudeTexture.Target = tex;
                    blur.SpreadTextureScale.Value = float2.One;
                    blur.SpreadTextureOffset.Value = float2.Zero;
                });
            }
            catch (Exception ex)
            {
                Msg($"[Settings] Blur mask generation failed: {ex.Message}");
            }
        });
    }

    private static byte[] CreateRoundedMaskPixels(int modalW, int modalH, float radiusPixels, out int texW, out int texH)
    {
        float aspect = modalW / (float)Math.Max(1, modalH);
        if (aspect >= 1f)
        {
            texW = 512;
            texH = Math.Clamp((int)MathF.Round(texW / aspect), 128, 512);
        }
        else
        {
            texH = 512;
            texW = Math.Clamp((int)MathF.Round(texH * aspect), 128, 512);
        }

        byte[] data = new byte[texW * texH * 4];
        float radius = Math.Clamp(radiusPixels, 1f, Math.Min(modalW, modalH) * 0.5f);
        const float edge = 2f;

        for (int y = 0; y < texH; y++)
        {
            float py = (y + 0.5f) / texH * modalH;
            for (int x = 0; x < texW; x++)
            {
                float px = (x + 0.5f) / texW * modalW;
                float cx = Math.Clamp(px, radius, modalW - radius);
                float cy = Math.Clamp(py, radius, modalH - radius);
                float dist = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                float mask = 1f - Math.Clamp((dist - (radius - edge)) / (edge * 2f), 0f, 1f);
                byte v = (byte)Math.Clamp((int)MathF.Round(mask * 255f), 0, 255);
                int i = (y * texW + x) * 4;
                data[i] = v;
                data[i + 1] = v;
                data[i + 2] = v;
                data[i + 3] = 255;
            }
        }

        return data;
    }

    private static byte[] CreateCenteredRoundedMaskPixels(int canvasW, int canvasH, int pillW, int pillH, float radiusPixels, out int texW, out int texH)
    {
        float aspect = canvasW / (float)Math.Max(1, canvasH);
        if (aspect >= 1f)
        {
            texW = 512;
            texH = Math.Clamp((int)MathF.Round(texW / aspect), 64, 512);
        }
        else
        {
            texH = 512;
            texW = Math.Clamp((int)MathF.Round(texH * aspect), 64, 512);
        }

        byte[] data = new byte[texW * texH * 4];
        float pillLeft = (canvasW - pillW) * 0.5f;
        float pillRight = pillLeft + pillW;
        float pillTop = (canvasH - pillH) * 0.5f;
        float pillBottom = pillTop + pillH;
        float radius = Math.Clamp(radiusPixels, 1f, Math.Min(pillW, pillH) * 0.5f);
        const float edge = 2f;

        for (int y = 0; y < texH; y++)
        {
            float py = (y + 0.5f) / texH * canvasH;
            for (int x = 0; x < texW; x++)
            {
                float px = (x + 0.5f) / texW * canvasW;
                float cx = Math.Clamp(px, pillLeft + radius, pillRight - radius);
                float cy = Math.Clamp(py, pillTop + radius, pillBottom - radius);
                float dist = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                float mask = 1f - Math.Clamp((dist - (radius - edge)) / (edge * 2f), 0f, 1f);

                if (px < pillLeft || px > pillRight || py < pillTop || py > pillBottom)
                    mask = 0f;

                byte v = (byte)Math.Clamp((int)MathF.Round(mask * 255f), 0, 255);
                int i = (y * texW + x) * 4;
                data[i] = v;
                data[i + 1] = v;
                data[i + 2] = v;
                data[i + 3] = 255;
            }
        }

        return data;
    }

    private static (int Width, int Height) GetSettingsModalSize(int panelWidth, int panelHeight)
    {
        int maxW = Math.Max(360, Math.Min(1120, panelWidth - 120));
        int maxH = Math.Max(300, Math.Min(760, panelHeight - 120));
        int minW = Math.Min(720, maxW);
        int minH = Math.Min(480, maxH);
        int width = (int)Math.Clamp(panelWidth * 0.62f, minW, maxW);
        int height = (int)Math.Clamp(panelHeight * 0.68f, minH, maxH);
        return (width, height);
    }

}

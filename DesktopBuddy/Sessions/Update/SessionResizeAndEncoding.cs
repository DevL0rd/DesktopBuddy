using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void RetriggerDesktopTexture(DesktopTextureProvider provider)
    {
        try
        {
            var type = typeof(DesktopTextureProvider);
            var assetField = type.GetField("_desktopTex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (assetField == null) return;

            var desktopTex = assetField.GetValue(provider) as DesktopTexture;
            if (desktopTex == null) return;

            var onCreatedMethod = type.GetMethod("OnTextureCreated",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (onCreatedMethod == null) return;

            var callback = (Action)Delegate.CreateDelegate(typeof(Action), provider, onCreatedMethod);
            desktopTex.Update(provider.DisplayIndex.Value, callback);
        }
        catch (Exception ex)
        {
            Msg($"[RetriggerDesktopTexture] Error: {ex.Message}");
        }
    }

    private static void ApplySessionVisualResize(DesktopSession session, int width, int height)
    {
        if (session == null || session.Cleaned || width <= 0 || height <= 0) return;

        if (session.Canvas != null && !session.Canvas.IsDestroyed)
            session.Canvas.Size.Value = new float2(width, height);

        session.OnResize?.Invoke(width, height);
        Msg($"[UpdateLoop] Visual resize applied to {width}x{height}");
    }

    private static void ConnectEncoder(
        DesktopSession session,
        FfmpegEncoder encoder,
        AudioCapture audioForEncoder = null,
        Action startAudioForEncoder = null)
    {
        if (encoder == null || session.Streamer == null) return;
        audioForEncoder ??= session.StreamAudioCapture ?? GetSharedStreamAudio(session.Hwnd);
        startAudioForEncoder ??= session.StartStreamAudioCapture ?? GetSharedStreamAudioStart(session.Hwnd);
        var enc = encoder;
        var d3dContextLock = session.Streamer.D3dContextLock;
        session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
        {
            enc.StartInitializeAsync(device, (uint)fw, (uint)fh, audioForEncoder, startAudioForEncoder, d3dContextLock);
            enc.QueueFrame(texture, (uint)fw, (uint)fh);
        };

        IntPtr latestTexture = session.Streamer.SharedTexture;
        int latestWidth = session.Streamer.SharedTextureWidth;
        int latestHeight = session.Streamer.SharedTextureHeight;
        IntPtr latestDevice = session.Streamer.D3dDevice;
        if (latestTexture != IntPtr.Zero && latestDevice != IntPtr.Zero && latestWidth > 0 && latestHeight > 0)
        {
            enc.StartInitializeAsync(latestDevice, (uint)latestWidth, (uint)latestHeight, audioForEncoder, startAudioForEncoder, d3dContextLock);
            enc.QueueFrame(latestTexture, (uint)latestWidth, (uint)latestHeight);
            Msg($"[RemoteStream] Seeded encoder from latest captured frame {latestWidth}x{latestHeight}");
        }
    }
}

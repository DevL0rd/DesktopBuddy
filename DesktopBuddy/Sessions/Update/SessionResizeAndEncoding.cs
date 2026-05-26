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
        var d3dContext = session.Streamer.D3dContext;
        session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
        {
            TryUpdateAdaptiveScreenLightFromGpuFrameNonBlocking(session, device, d3dContext, texture, fw, fh, d3dContextLock);

            enc.StartInitializeAsync(device, (uint)fw, (uint)fh, audioForEncoder, startAudioForEncoder, d3dContextLock);
            enc.QueueFrame(texture, (uint)fw, (uint)fh);
        };

        IntPtr latestTexture = session.Streamer.SharedTexture;
        int latestWidth = session.Streamer.SharedTextureWidth;
        int latestHeight = session.Streamer.SharedTextureHeight;
        IntPtr latestDevice = session.Streamer.D3dDevice;
        IntPtr latestContext = session.Streamer.D3dContext;
        if (latestTexture != IntPtr.Zero && latestDevice != IntPtr.Zero && latestWidth > 0 && latestHeight > 0)
        {
            TryUpdateAdaptiveScreenLightFromGpuFrameNonBlocking(session, latestDevice, latestContext, latestTexture, latestWidth, latestHeight, d3dContextLock);

            MarkEncoderInitialSourceSize(session, latestWidth, latestHeight);
            enc.StartInitializeAsync(latestDevice, (uint)latestWidth, (uint)latestHeight, audioForEncoder, startAudioForEncoder, d3dContextLock);
            enc.QueueFrame(latestTexture, (uint)latestWidth, (uint)latestHeight);
            Msg($"[RemoteStream] Seeded encoder from latest captured frame {latestWidth}x{latestHeight}");
        }
    }

    private static void TryUpdateAdaptiveScreenLightFromGpuFrameNonBlocking(
        DesktopSession session,
        IntPtr device,
        IntPtr context,
        IntPtr texture,
        int width,
        int height,
        object d3dContextLock)
    {
        if (context == IntPtr.Zero) return;

        if (d3dContextLock == null)
        {
            TryUpdateAdaptiveScreenLightFromGpuFrame(session, device, context, texture, width, height);
            return;
        }

        var lockTaken = false;
        try
        {
            Monitor.TryEnter(d3dContextLock, 0, ref lockTaken);
            if (!lockTaken) return;
            TryUpdateAdaptiveScreenLightFromGpuFrame(session, device, context, texture, width, height);
        }
        finally
        {
            if (lockTaken) Monitor.Exit(d3dContextLock);
        }
    }

    private static void MarkEncoderInitialSourceSize(DesktopSession session, int width, int height)
    {
        if (session == null || session.StreamId <= 0 || width <= 0 || height <= 0)
            return;

        if (session.EncoderInitialStreamId == session.StreamId &&
            session.EncoderInitialSourceW > 0 &&
            session.EncoderInitialSourceH > 0)
        {
            return;
        }

        session.EncoderInitialStreamId = session.StreamId;
        session.EncoderInitialSourceW = width;
        session.EncoderInitialSourceH = height;
    }
}

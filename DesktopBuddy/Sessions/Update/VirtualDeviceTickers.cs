using System;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void FindLastVirtualDeviceSessionIndexes(World world, out int lastVCamIdx, out int lastVMicIdx)
    {
        lastVCamIdx = -1;
        lastVMicIdx = -1;
        for (int k = 0; k < ActiveSessions.Count; k++)
        {
            var s = ActiveSessions[k];
            if (s.Root?.World != world) continue;
            if (s.VCamCamera != null && !s.VCamCamera.IsDestroyed) lastVCamIdx = k;
            if (s.VMicListener != null && !s.VMicListener.IsDestroyed) lastVMicIdx = k;
        }
    }

    private static void TickVirtualCamera(DesktopSession session, int sessionIndex, int lastVCamIdx)
    {
        if (VCam != null && !VCam.ManuallyDisabled && VCam.ConsumerConnected &&
            session.VCamCamera != null && !session.VCamCamera.IsDestroyed &&
            !session.VCamRenderPending)
        {
            if (sessionIndex == lastVCamIdx)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long minIntervalTicks = System.Diagnostics.Stopwatch.Frequency / VirtualCameraTargetFps;
                if (session.VCamLastSubmitTicks == 0 ||
                    nowTicks - session.VCamLastSubmitTicks >= minIntervalTicks)
                {
                    session.VCamLastSubmitTicks = nowTicks;
                    session.VCamRenderPending = true;
                    var vcam = session.VCamCamera;
                    var vcamRef = VCam;
                    var renderSettings = vcam.GetRenderSettings(new int2(1280, 720));
                    renderSettings.parameters.textureFormat = TextureFormat.RGB24;
                    vcam.World.Render.RenderToBitmap(renderSettings, willHandleBuffer: true).ContinueWith(task =>
                    {
                        Bitmap2D bmp = null;
                        try
                        {
                            if (task.IsFaulted || task.Result == null) return;
                            bmp = task.Result;
                            if (bmp.RawData.Length == 0) return;
                            if (vcamRef._logNextFrame)
                            {
                                vcamRef._logNextFrame = false;
                                DesktopBuddy.Log.Msg($"[VirtualCamera] Bitmap: {bmp.Size.x}x{bmp.Size.y} format={bmp.Format} bpp={bmp.BitsPerPixel} profile={bmp.Profile}");
                            }
                            vcamRef.SendFrame(bmp.RawData, bmp.Size.x, bmp.Size.y, bmp.Format);
                        }
                        finally
                        {
                            try { bmp?.Buffer?.Dispose(); } catch { }
                            session.VCamRenderPending = false;
                        }
                    });
                }
            }
        }

        UpdateVirtualCameraIndicator(session);
    }

    private static void UpdateVirtualCameraIndicator(DesktopSession session)
    {
        if (session.VCamIndicator != null && !session.VCamIndicator.IsDestroyed && VCam != null)
        {
            bool lit = VCam.ConsumerConnected && !VCam.ManuallyDisabled;
            if (lit != session.VCamLastLitState)
            {
                session.VCamLastLitState = lit;
                session.VCamIndicator.Tint.Value = lit
                    ? new colorX(0.8f, 0.1f, 0.1f, 1f)
                    : new colorX(0.05f, 0.05f, 0.05f, 1f);
            }
        }
    }

    private static void TickVirtualMic(DesktopSession session, int sessionIndex, int lastVMicIdx)
    {
        if ((VMic == null || !VMic.IsActive) && VBCable.HasCableInputDevice() &&
            session.VMicListener != null && !session.VMicListener.IsDestroyed)
        {
            if (sessionIndex == lastVMicIdx)
            {
                VMic = new VirtualMic();
                if (VMic.Start())
                {
                    var listener = session.VMicListener;
                    var mic = VMic;
                    var simulator = session.Root.Engine.AudioSystem.Simulator;
                    if (listener != null && simulator != null)
                    {
                        int frameSize = simulator.FrameSize;
                        var stereoBuf = new StereoSample[frameSize];
                        var floatBuf = new float[frameSize * 2];
                        simulator.RenderFinished += (sim) =>
                        {
                            if (mic.Muted || listener.IsDestroyed) return;
                            var span = stereoBuf.AsSpan(0, sim.FrameSize);
                            span.Clear();
                            listener.Read(span, sim);
                            for (int s = 0; s < span.Length; s++)
                            {
                                floatBuf[s * 2] = span[s].left;
                                floatBuf[s * 2 + 1] = span[s].right;
                            }
                            mic.WriteGameAudio(floatBuf.AsSpan(0, span.Length * 2));
                        };
                        Msg($"[VirtualMic] Hooked AudioListener (frameSize={frameSize})");
                    }
                }
                else
                {
                    VMic.Dispose();
                    VMic = null;
                }
            }
        }

        if (VMic != null)
            VMic.Muted = session.VMicMuted;
    }
}

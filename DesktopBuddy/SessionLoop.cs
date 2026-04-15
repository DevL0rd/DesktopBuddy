using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static readonly HashSet<World> _scheduledWorlds = new();

    internal static void ScheduleUpdate(World world)
    {
        if (_scheduledWorlds.Contains(world)) return;
        _scheduledWorlds.Add(world);
        world.RunInUpdates(1, () => UpdateLoop(world));
    }

    private static int _updateCount;

    private static void WindowPollerLoop()
    {
        while (_windowPollerRunning)
        {
            Thread.Sleep(100);
            if (!_windowPollerRunning) break;

            DesktopSession[] snapshot;
            try { snapshot = ActiveSessions.ToArray(); }
            catch { continue; }

            var byProcess = new Dictionary<uint, List<DesktopSession>>();
            foreach (var session in snapshot)
            {
                if (session.Cleaned || session.IsChildPanel || session.ProcessId == 0) continue;
                if (!byProcess.TryGetValue(session.ProcessId, out var list))
                    byProcess[session.ProcessId] = list = new List<DesktopSession>();
                list.Add(session);
            }

            foreach (var kvp in byProcess)
            {
                if (!_windowPollerRunning) break;
                var sessions = kvp.Value;

                List<WindowEnumerator.WindowInfo> procWindows;
                HashSet<IntPtr> windowSet;
                try
                {
                    procWindows = WindowEnumerator.GetProcessWindows(kvp.Key);
                    windowSet = new HashSet<IntPtr>(procWindows.Count);
                    for (int pw = 0; pw < procWindows.Count; pw++)
                        windowSet.Add(procWindows[pw].Handle);
                }
                catch (Exception ex)
                {
                    Msg($"[WindowPoller] Error enumerating PID {kvp.Key}: {ex.Message}");
                    continue;
                }

                foreach (var session in sessions)
                {
                    try
                    {
                        for (int pw = 0; pw < procWindows.Count; pw++)
                        {
                            if (procWindows[pw].Handle == session.Hwnd && !string.IsNullOrEmpty(procWindows[pw].Title))
                            {
                                if (procWindows[pw].Title != session.LastTitle)
                                {
                                    _windowEvents.Enqueue(new WindowEvent
                                    {
                                        Session = session,
                                        EventType = WindowEventType.TitleChanged,
                                        Title = procWindows[pw].Title
                                    });
                                }
                                break;
                            }
                        }

                        foreach (var win in procWindows)
                        {
                            if (win.Handle == session.Hwnd) continue;
                            if (session.TrackedChildHwnds.Contains(win.Handle)) continue;
                            if (WindowEnumerator.TryGetWindowRect(win.Handle, out _, out _, out int cw2, out int ch2) && cw2 > 10 && ch2 > 10)
                            {
                                session.TrackedChildHwnds.Add(win.Handle);
                                _windowEvents.Enqueue(new WindowEvent
                                {
                                    Session = session,
                                    EventType = WindowEventType.NewChild,
                                    ChildHwnd = win.Handle,
                                    Title = win.Title
                                });
                            }
                        }

                        for (int c = session.ChildSessions.Count - 1; c >= 0; c--)
                        {
                            var child = session.ChildSessions[c];
                            bool childStillActive = child.Streamer != null && windowSet.Contains(child.Hwnd);
                            if (!childStillActive)
                            {
                                _windowEvents.Enqueue(new WindowEvent
                                {
                                    Session = session,
                                    EventType = WindowEventType.ChildClosed,
                                    ChildHwnd = child.Hwnd
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Msg($"[WindowPoller] Error for session hwnd={session.Hwnd}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static void UpdateLoop(World world)
    {
        _updateCount++;
        double dt = world.Time.Delta;

        if (world.IsDestroyed)
        {
            Msg("[UpdateLoop] World destroyed, cleaning up sessions for this world");
            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];
                if (session.Root == null || session.Root.IsDestroyed || session.Root.World == world)
                {
                    Msg($"[UpdateLoop] Cleaning up session {i} (world destroyed)");
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                }
            }
            _scheduledWorlds.Remove(world);
            return;
        }

        try
        {
            int lastVCamIdx = -1;
            int lastVMicIdx = -1;
            for (int k = 0; k < ActiveSessions.Count; k++)
            {
                var s = ActiveSessions[k];
                if (s.Root?.World != world) continue;
                if (s.VCamCamera != null && !s.VCamCamera.IsDestroyed) lastVCamIdx = k;
                if (s.VMicListener != null && !s.VMicListener.IsDestroyed) lastVMicIdx = k;
            }

            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];

                if (session.Cleaned)
                {
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root == null || session.Root.IsDestroyed ||
                    session.Texture == null || session.Texture.IsDestroyed)
                {
                    Msg($"[UpdateLoop] Session {i} root/texture destroyed, cleaning up (root={session.Root != null} rootDestroyed={session.Root?.IsDestroyed} tex={session.Texture != null} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId})");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed) { vtp.URL.Value = null; vtp.Stop(); }
                    session.VideoTexture = null;
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root.World != world) continue;
                if (session.UpdateInProgress) continue;

                session.TimeSinceValidCheck += dt;
                if (session.TimeSinceValidCheck >= 0.5)
                {
                    session.TimeSinceValidCheck = 0;
                    session.LastValidState = session.Streamer == null || session.Streamer.IsValid;
                }
                if (session.Streamer != null && !session.LastValidState)
                {
                    Msg($"[UpdateLoop] Window closed (IsValid=false), destroying viewer");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg("[UpdateLoop] Disconnecting VideoTextureProvider before cleanup");
                        vtp.URL.Value = null;
                        vtp.Stop();
                    }
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                    var rootToDestroy = session.Root;
                    world.RunInUpdates(10, () =>
                    {
                        Msg("[UpdateLoop] Deferred destroy executing");
                        if (rootToDestroy != null && !rootToDestroy.IsDestroyed)
                        {
                            rootToDestroy.DestroyChildren();
                            rootToDestroy.Destroy();
                        }
                        Msg("[UpdateLoop] Deferred destroy complete");
                    });
                    continue;
                }

                while (_windowEvents.TryDequeue(out var evt))
                {
                    if (evt.Session.Cleaned || evt.Session.Root == null || evt.Session.Root.IsDestroyed) continue;
                    if (evt.Session.Root.World != world) continue;

                    switch (evt.EventType)
                    {
                        case WindowEventType.TitleChanged:
                            evt.Session.LastTitle = evt.Title;
                            if (evt.Session.TitleText != null && !evt.Session.TitleText.IsDestroyed)
                                evt.Session.TitleText.Text.Value = evt.Title;
                            if (evt.Session.Root != null && !evt.Session.Root.IsDestroyed)
                                evt.Session.Root.Name = $"Desktop: {evt.Title}";
                            break;

                        case WindowEventType.NewChild:
                            Msg($"[ChildWindow] Detected new popup: hwnd={evt.ChildHwnd} title='{evt.Title}'");
                            SpawnChildWindow(evt.Session, evt.ChildHwnd, evt.Title);
                            break;

                        case WindowEventType.ChildClosed:
                        {
                            var child = evt.Session.ChildSessions.Find(c => c.Hwnd == evt.ChildHwnd);
                            if (child != null)
                            {
                                Msg($"[ChildWindow] Popup closed: hwnd={child.Hwnd}");
                                evt.Session.TrackedChildHwnds.Remove(child.Hwnd);
                                child.ParentSession = null;
                                evt.Session.ChildSessions.Remove(child);
                                {
                                    var cvtp = child.VideoTexture;
                                    if (cvtp != null && !cvtp.IsDestroyed) { cvtp.URL.Value = null; cvtp.Stop(); }
                                    child.VideoTexture = null;
                                    var cRoot = child.Root;
                                    world.RunInUpdates(10, () =>
                                    {
                                        if (cRoot != null && !cRoot.IsDestroyed) cRoot.Destroy();
                                    });
                                }
                                CleanupSession(child);
                            }
                            break;
                        }
                    }
                }

                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");
                    if (session.CaptureSlot >= 0 && _updateCount % 5 == 0)
                    {
                        RetriggerDesktopTexture(session.Texture);
                    }
                    continue;
                }

                var streamerForResize = session.Streamer;
                if (streamerForResize != null)
                {
                    streamerForResize.RecreatePoolIfNeeded();
                    int sw = streamerForResize.Width;
                    int sh = streamerForResize.Height;

                    if (sw > 0 && sh > 0 && (session.LastKnownW != sw || session.LastKnownH != sh))
                    {
                        Msg($"[UpdateLoop] Window resize {session.LastKnownW}x{session.LastKnownH} -> {sw}x{sh}");
                        session.LastKnownW = sw;
                        session.LastKnownH = sh;

                        if (session.Canvas != null && !session.Canvas.IsDestroyed)
                            session.Canvas.Size.Value = new float2(sw, sh);

                        session.OnResize?.Invoke(sw, sh);
                        session.PendingResizeW = sw;
                        session.PendingResizeH = sh;
                        session.ResizeDebounceUntil = world.Time.WorldTime + 0.5;
                        Msg($"[UpdateLoop] UI resized to {sw}x{sh}");
                        continue;
                    }
                }

                if (session.ResizeDebounceUntil > 0 && world.Time.WorldTime >= session.ResizeDebounceUntil)
                {
                    session.ResizeDebounceUntil = 0;
                    int rw = session.PendingResizeW;
                    int rh = session.PendingResizeH;
                    Msg($"[UpdateLoop] Resize debounce expired, reiniting encoder for {rw}x{rh}");

                    if (session.Streamer != null) session.Streamer.OnGpuFrame = null;

                    var oldStreamId = session.StreamId;
                    int newStreamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                    var newEncoder = StreamServer?.CreateEncoder(newStreamId);
                    session.StreamId = newStreamId;

                    FfmpegEncoder oldEncoder = null;
                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(session.Hwnd, out var oldShared))
                            oldEncoder = oldShared.Encoder;
                    }

                    var oldStreamer = session.Streamer;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            oldEncoder?.Stop();
                            oldStreamer?.FlushD3dContext();
                            StreamServer?.StopEncoder(oldStreamId);
                        }
                        catch (Exception ex) { Msg($"[Resize:BG] Old encoder cleanup error: {ex.Message}"); }
                    });

                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(session.Hwnd, out var shared))
                        {
                            shared.StreamId = newStreamId;
                            shared.Encoder = newEncoder;
                        }
                    }

                    ConnectEncoder(session, newEncoder);

                    if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && TunnelUrl != null)
                    {
                        var newUrl = new Uri($"{TunnelUrl}/stream/{newStreamId}");
                        Msg($"[UpdateLoop] Updating VTP URL: {session.VideoTexture.URL.Value} -> {newUrl}");
                        session.VideoTexture.URL.Value = newUrl;
                    }

                    Msg($"[UpdateLoop] New encoder {newStreamId} created and connected for {rw}x{rh}");
                }

                if (VCam != null && VCam.ConsumerConnected && !VCam.ManuallyDisabled &&
                    session.VCamCamera != null && !session.VCamCamera.IsDestroyed &&
                    !session.VCamRenderPending)
                {
                    if (i == lastVCamIdx)
                    {
                        session.VCamRenderPending = true;
                        var vcam = session.VCamCamera;
                        var vcamRef = VCam;
                        vcam.RenderToBitmap(new int2(1280, 720)).ContinueWith(task =>
                        {
                            session.VCamRenderPending = false;
                            if (task.IsFaulted || task.Result == null) return;
                            var bmp = task.Result;
                            if (bmp.RawData.Length == 0) return;
                            if (vcamRef._logNextFrame)
                            {
                                vcamRef._logNextFrame = false;
                                Log.Msg($"[VirtualCamera] Bitmap: {bmp.Size.x}x{bmp.Size.y} format={bmp.Format} bpp={bmp.BitsPerPixel} profile={bmp.Profile}");
                            }
                            vcamRef.SendFrame(bmp.RawData, bmp.Size.x, bmp.Size.y, bmp.Format);
                        });
                    }
                }

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

                if ((VMic == null || !VMic.IsActive) && VBCableSetup.IsInstalled() &&
                    session.VMicListener != null && !session.VMicListener.IsDestroyed)
                {
                    if (i == lastVMicIdx)
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
                        { VMic.Dispose(); VMic = null; }
                    }
                }
                if (VMic != null)
                    VMic.Muted = session.VMicMuted;

                Perf.IncrementFrames();
            }
        }
        catch (Exception ex)
        {
            Msg($"ERROR in UpdateLoop: {ex}");
        }

        bool hasSessionsInWorld = false;
        for (int i = 0; i < ActiveSessions.Count; i++)
        {
            if (ActiveSessions[i].Root?.World == world) { hasSessionsInWorld = true; break; }
        }
        if (hasSessionsInWorld)
        {
            world.RunInUpdates(1, () => UpdateLoop(world));
        }
        else
        {
            Msg("[UpdateLoop] No sessions left for this world, stopping loop");
            _scheduledWorlds.Remove(world);
        }
    }

    private static void CleanupSession(DesktopSession session)
    {
        if (session.Cleaned) { Msg($"[Cleanup] Already cleaned hwnd={session.Hwnd} streamId={session.StreamId}, skipping"); return; }
        session.Cleaned = true;
        Msg($"[Cleanup] === START === hwnd={session.Hwnd} streamId={session.StreamId} isChild={session.IsChildPanel} children={session.ChildSessions.Count}");

        if (VMic != null && session.VMicListener != null)
        {
            Msg("[Cleanup] Disposing VMic (listener destroyed)");
            VMic.Dispose();
            VMic = null;
        }

        if (session.OwnsAudioRedirect && session.ProcessId != 0)
        {
            bool otherSessionUsesSamePid = false;
            foreach (var s in ActiveSessions)
            {
                if (s != session && !s.Cleaned && s.ProcessId == session.ProcessId)
                {
                    otherSessionUsesSamePid = true;
                    break;
                }
            }
            if (!otherSessionUsesSamePid)
            {
                AudioRouter.ResetProcessToDefault(session.ProcessId);
                Msg($"[Cleanup] Reset audio routing for PID {session.ProcessId}");
            }
            else
            {
                Msg($"[Cleanup] Keeping audio routing for PID {session.ProcessId} (other sessions still active)");
            }
        }

        if (session.ChildSessions.Count > 0)
        {
            Msg($"[Cleanup] Destroying {session.ChildSessions.Count} child popup panels");
            foreach (var child in session.ChildSessions)
            {
                child.ParentSession = null;
                Msg($"[Cleanup] Child: disconnecting VTP hwnd={child.Hwnd}");
                {
                    var vtp = child.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed) { vtp.URL.Value = null; vtp.Stop(); }
                    child.VideoTexture = null;
                    var rootToDie = child.Root;
                    if (rootToDie != null && !rootToDie.IsDestroyed)
                    {
                        var childWorld = rootToDie.World;
                        if (childWorld != null && !childWorld.IsDestroyed)
                        {
                            childWorld.RunInUpdates(10, () =>
                            {
                                Msg($"[Cleanup] Child deferred destroy executing hwnd={child.Hwnd}");
                                if (rootToDie != null && !rootToDie.IsDestroyed) rootToDie.Destroy();
                                Msg($"[Cleanup] Child deferred destroy complete hwnd={child.Hwnd}");
                            });
                        }
                        else
                        {
                            Msg($"[Cleanup] Child world dead, destroying now hwnd={child.Hwnd}");
                            rootToDie.Destroy();
                        }
                    }
                }
                Msg($"[Cleanup] Child: calling CleanupSession recursively hwnd={child.Hwnd}");
                CleanupSession(child);
                Msg($"[Cleanup] Child: done hwnd={child.Hwnd}");
            }
            session.ChildSessions.Clear();
            session.TrackedChildHwnds.Clear();
        }

        if (session.ParentSession != null)
        {
            Msg($"[Cleanup] Removing from parent tracking");
            session.ParentSession.TrackedChildHwnds.Remove(session.Hwnd);
            session.ParentSession.ChildSessions.Remove(session);
        }

        Msg($"[Cleanup] Removing canvas ID");
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        if (session.CaptureSlot >= 0 && CaptureChannel != null)
        {
            CaptureChannel.StopSession(session.CaptureSlot);
            Msg($"[Cleanup] Stopped capture slot {session.CaptureSlot}");
        }

        if (session.Texture != null)
            OurProviders.Remove(session.Texture);

        Msg($"[Cleanup] Disconnecting encoder");
        var streamer = session.Streamer;
        if (streamer != null) streamer.OnGpuFrame = null;
        if (session.StreamId > 0)
        {
            lock (_sharedStreams)
            {
                if (_sharedStreams.TryGetValue(session.Hwnd, out var shared) && shared.Encoder != null)
                    shared.Encoder.Stop();
            }
        }
        int streamId = session.StreamId;
        IntPtr hwnd = session.Hwnd;
        session.Streamer = null;

        Msg($"[Cleanup] Queuing background dispose for stream {streamId}");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Msg($"[Cleanup:BG] === START === stream {streamId}");

                streamer?.FlushD3dContext();

                AudioCapture audioToDispose = null;
                bool shouldStopEncoder = false;
                if (streamId > 0)
                {
                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(hwnd, out var shared) && shared.StreamId == streamId)
                        {
                            shared.RefCount--;
                            Msg($"[Cleanup:BG] Stream {shared.StreamId} refs now {shared.RefCount}");
                            if (shared.RefCount <= 0)
                            {
                                _sharedStreams.Remove(hwnd);
                                audioToDispose = shared.Audio;
                                shouldStopEncoder = true;
                            }
                        }
                        else
                        {
                            shouldStopEncoder = true;
                        }
                    }

                    if (shouldStopEncoder)
                    {
                        Msg($"[Cleanup:BG] Stopping encoder {streamId}...");
                        StreamServer?.StopEncoder(streamId);
                        Msg($"[Cleanup:BG] Encoder {streamId} stopped");
                    }

                    bool forceGC = Config?.GetValue(ImmediateGC) ?? false;
                    if (forceGC)
                    {
                        Msg("[Cleanup:BG] Forcing GC after encoder dispose");
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }

                Msg($"[Cleanup:BG] Stopping capture...");
                streamer?.StopCapture();
                Msg($"[Cleanup:BG] Capture stopped");

                Msg($"[Cleanup:BG] Disposing streamer...");
                streamer?.Dispose();
                Msg($"[Cleanup:BG] Streamer disposed");

                if (audioToDispose != null)
                {
                    Msg($"[Cleanup:BG] Disposing audio...");
                    audioToDispose.Dispose();
                    Msg($"[Cleanup:BG] Audio disposed");
                }

                Msg($"[Cleanup:BG] === DONE === stream {streamId}");
            }
            catch (Exception ex)
            {
                Msg($"[Cleanup:BG] ERROR: {ex}");
            }
        });
        Msg($"[Cleanup] === END (bg queued) === stream {streamId}");
    }

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

    private static void ConnectEncoder(DesktopSession session, FfmpegEncoder encoder)
    {
        if (encoder == null || session.Streamer == null) return;
        var contextLock = session.Streamer.D3dContextLock;
        AudioCapture audioForEncoder = null;
        lock (_sharedStreams)
        {
            if (_sharedStreams.TryGetValue(session.Hwnd, out var shared))
                audioForEncoder = shared.Audio;
        }
        var enc = encoder;
        session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
        {
            enc.StartInitializeAsync(device, (uint)fw, (uint)fh, contextLock, audioForEncoder);
            enc.QueueFrame(texture, (uint)fw, (uint)fh);
        };
    }
}

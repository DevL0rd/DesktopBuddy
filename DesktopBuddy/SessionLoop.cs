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

    private static void CleanupTrace(string message) => Log.MsgImmediate($"[CleanupTrace] {message}");

    private static long TraceStart(string label)
    {
        CleanupTrace($"{label} START");
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static void TraceDone(string label, long startTicks)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        CleanupTrace($"{label} DONE {ms:F2}ms");
    }

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
            var activeWindows = new HashSet<IntPtr>();
            foreach (var session in snapshot)
            {
                if (!session.Cleaned && session.Hwnd != IntPtr.Zero)
                    activeWindows.Add(session.Hwnd);
            }

            var byProcess = new Dictionary<uint, List<DesktopSession>>();
            foreach (var session in snapshot)
            {
                if (session.Cleaned || session.ProcessId == 0) continue;
                if (!byProcess.TryGetValue(session.ProcessId, out var list))
                    byProcess[session.ProcessId] = list = new List<DesktopSession>();
                list.Add(session);
            }

            foreach (var kvp in byProcess)
            {
                if (!_windowPollerRunning) break;
                var sessions = kvp.Value;

                List<WindowEnumerator.WindowInfo> procWindows;
                try
                {
                    procWindows = WindowEnumerator.GetProcessWindows(kvp.Key);
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
                            if (activeWindows.Contains(win.Handle)) continue;
                            if (session.SeenRelatedHwnds.Contains(win.Handle)) continue;

                            session.SeenRelatedHwnds.Add(win.Handle);
                            _windowEvents.Enqueue(new WindowEvent
                            {
                                Session = session,
                                EventType = WindowEventType.NewTopLevelWindow,
                                WindowHwnd = win.Handle,
                                Title = win.Title
                            });
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
                    CleanupTrace($"UpdateLoop destroyed branch sessionIndex={i} root={(session.Root != null)} rootDestroyed={session.Root?.IsDestroyed} tex={(session.Texture != null)} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId}");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        var stopTicks = TraceStart($"VideoTexture stop stream={session.StreamId}");
                        vtp.URL.Value = null;
                        vtp.Stop();
                        TraceDone($"VideoTexture stop stream={session.StreamId}", stopTicks);
                    }
                    session.VideoTexture = null;
                    var cleanupTicks = TraceStart($"CleanupSession call stream={session.StreamId}");
                    CleanupSession(session);
                    TraceDone($"CleanupSession call stream={session.StreamId}", cleanupTicks);
                    ActiveSessions.RemoveAt(i);
                    CleanupTrace($"ActiveSessions removed destroyed sessionIndex={i}");
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
                        var stopTicks = TraceStart($"VideoTexture stop invalid hwnd={session.Hwnd} stream={session.StreamId}");
                        vtp.URL.Value = null;
                        vtp.Stop();
                        TraceDone($"VideoTexture stop invalid hwnd={session.Hwnd} stream={session.StreamId}", stopTicks);
                    }
                    var cleanupTicks = TraceStart($"CleanupSession invalid hwnd={session.Hwnd} stream={session.StreamId}");
                    CleanupSession(session);
                    TraceDone($"CleanupSession invalid hwnd={session.Hwnd} stream={session.StreamId}", cleanupTicks);
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

                        case WindowEventType.NewTopLevelWindow:
                            if (!WindowEnumerator.TryValidateStandaloneProcessWindow(
                                    evt.WindowHwnd,
                                    evt.Session.ProcessId,
                                    out string currentTitle,
                                    out string validationReason))
                            {
                                Msg($"[WindowPoller] Ignored new window hwnd={evt.WindowHwnd} title='{evt.Title}': {validationReason}");
                                break;
                            }

                            var spawnTitle = !string.IsNullOrWhiteSpace(currentTitle) ? currentTitle : evt.Title;
                            Msg($"[WindowPoller] Detected new top-level window: hwnd={evt.WindowHwnd} title='{spawnTitle}'");
                            SpawnStreaming(evt.Session.Root.World, evt.WindowHwnd, spawnTitle);
                            break;
                    }
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

                        if (session.SharedTextureSlot >= 0 && TextureBridgeChannel != null)
                        {
                            TextureBridgeChannel.UpdateTexture(
                                session.SharedTextureSlot,
                                streamerForResize.SharedTextureHandle,
                                streamerForResize.SharedTextureWidth,
                                streamerForResize.SharedTextureHeight);
                            RetriggerDesktopTexture(session.Texture);
                            world.RunInUpdates(2, () =>
                            {
                                if (!session.Cleaned && session.Texture != null && !session.Texture.IsDestroyed)
                                    RetriggerDesktopTexture(session.Texture);
                            });
                        }
                        else
                        {
                            ApplySessionVisualResize(session, sw, sh);
                        }

                        session.PendingResizeW = sw;
                        session.PendingResizeH = sh;
                        session.PendingVisualResizeW = sw;
                        session.PendingVisualResizeH = sh;
                        session.ResizeDebounceUntil = world.Time.WorldTime + 0.15;
                        Msg($"[UpdateLoop] Resize pending: visual={sw}x{sh} encoder debounce=150ms");
                        continue;
                    }
                }

                if (session.PendingVisualResizeW > 0 && session.PendingVisualResizeH > 0)
                {
                    int visualW = session.PendingVisualResizeW;
                    int visualH = session.PendingVisualResizeH;
                    bool bridgeReady = session.SharedTextureSlot < 0 ||
                        TextureBridgeChannel == null ||
                        TextureBridgeChannel.IsTextureRunning(session.SharedTextureSlot, visualW, visualH);

                    if (bridgeReady)
                    {
                        ApplySessionVisualResize(session, visualW, visualH);
                        session.PendingVisualResizeW = 0;
                        session.PendingVisualResizeH = 0;
                    }
                    else
                    {
                        if (_updateCount % 5 == 0)
                            RetriggerDesktopTexture(session.Texture);
                        if (_updateCount % 30 == 0)
                            Msg($"[UpdateLoop] Waiting for shared texture bind before visual resize {visualW}x{visualH}");
                    }
                }

                if (session.ResizeDebounceUntil > 0 && world.Time.WorldTime >= session.ResizeDebounceUntil)
                {
                    session.ResizeDebounceUntil = 0;
                    int rw = session.PendingResizeW;
                    int rh = session.PendingResizeH;

                    if (session.StreamId <= 0)
                    {
                        Msg($"[UpdateLoop] Resize debounce expired for local-only panel {rw}x{rh}, no encoder reinit");
                        continue;
                    }

                    Msg($"[UpdateLoop] Resize debounce expired, reiniting encoder for {rw}x{rh}");

                    if (session.Streamer != null) session.Streamer.OnGpuFrame = null;

                    var oldStreamId = session.StreamId;
                    int newStreamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                    bool useMediaMtx = IsMediaMtxEnabled;
                    FfmpegEncoder newEncoder;
                    Uri newUrl;

                    if (useMediaMtx)
                    {
                        var rtspUrl = GetMediaMtxRtspUrl(newStreamId);
                        newEncoder = new FfmpegEncoder(newStreamId, rtspUrl);
                        newUrl = new Uri(rtspUrl);
                    }
                    else
                    {
                        newEncoder = RemoteRtspServer?.CreateEncoder(newStreamId);
                        newUrl = RemoteRtspServer != null ? GetEmbeddedRtspUri(newStreamId) : null;
                    }
                    session.StreamId = newStreamId;

                    FfmpegEncoder oldEncoder = session.Encoder;
                    if (oldEncoder == null)
                    {
                        lock (_sharedStreams)
                        {
                            if (_sharedStreams.TryGetValue(session.Hwnd, out var oldShared))
                                oldEncoder = oldShared.Encoder;
                        }
                    }

                    var oldStreamer = session.Streamer;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            oldEncoder?.Stop();
                            oldStreamer?.FlushD3dContext();
                            if (!useMediaMtx)
                                RemoteRtspServer?.StopStream(oldStreamId);
                            oldEncoder?.Dispose();
                        }
                        catch (Exception ex) { Msg($"[Resize:BG] Old encoder cleanup error: {ex.Message}"); }
                    });

                    lock (_sharedStreams)
                    {
                        if (_sharedStreams.TryGetValue(session.Hwnd, out var shared))
                        {
                            shared.StreamId = newStreamId;
                            shared.Encoder = newEncoder;
                            if (newUrl != null)
                                shared.StreamUrl = newUrl;
                        }
                    }

                    session.Encoder = newEncoder;
                    ConnectEncoder(session, newEncoder);

                    if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && newUrl != null)
                    {
                        Msg($"[UpdateLoop] Updating VTP URL: {session.VideoTexture.URL.Value} -> {newUrl}");
                        session.VideoTexture.URL.Value = newUrl;
                    }

                    Msg($"[UpdateLoop] New encoder {newStreamId} created and connected for {rw}x{rh}");
                }

                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");
                    if (session.SharedTextureSlot >= 0 && _updateCount % 5 == 0)
                    {
                        RetriggerDesktopTexture(session.Texture);
                    }
                    continue;
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
        Msg($"[Cleanup] === START === hwnd={session.Hwnd} streamId={session.StreamId}");
        CleanupTrace($"CleanupSession ENTER hwnd={session.Hwnd} streamId={session.StreamId} rootDestroyed={session.Root?.IsDestroyed} texDestroyed={session.Texture?.IsDestroyed}");

        session.ActiveTouchIds.Clear();
        CleanupTrace($"ActiveTouchIds cleared hwnd={session.Hwnd} streamId={session.StreamId}");

        if (VMic != null && session.VMicListener != null)
        {
            Msg("[Cleanup] Disposing VMic (listener destroyed)");
            var ticks = TraceStart("VMic.Dispose");
            VMic.Dispose();
            VMic = null;
            TraceDone("VMic.Dispose", ticks);
        }

        if (session.OwnsAudioRedirect && session.ProcessId != 0)
        {
            CleanupTrace($"Audio redirect reset check START pid={session.ProcessId}");
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
                var ticks = TraceStart($"AudioRouter.ResetProcessToDefault pid={session.ProcessId}");
                AudioRouter.ResetProcessToDefault(session.ProcessId);
                TraceDone($"AudioRouter.ResetProcessToDefault pid={session.ProcessId}", ticks);
                Msg($"[Cleanup] Reset audio routing for PID {session.ProcessId}");
            }
            else
            {
                Msg($"[Cleanup] Keeping audio routing for PID {session.ProcessId} (other sessions still active)");
            }
        }

        session.SeenRelatedHwnds.Clear();

        Msg($"[Cleanup] Removing canvas ID");
        CleanupTrace($"Removing canvas/provider ids hwnd={session.Hwnd} streamId={session.StreamId}");
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        if (session.SharedTextureSlot >= 0 && TextureBridgeChannel != null)
        {
            var ticks = TraceStart($"TextureBridge.StopTexture slot={session.SharedTextureSlot}");
            TextureBridgeChannel.StopTexture(session.SharedTextureSlot);
            TraceDone($"TextureBridge.StopTexture slot={session.SharedTextureSlot}", ticks);
            Msg($"[Cleanup] Stopped shared texture slot {session.SharedTextureSlot}");
        }

        if (session.Texture != null)
        {
            OurProviders.Remove(session.Texture);
            CleanupTrace("DesktopTextureProvider removed from provider set");
        }

        Msg($"[Cleanup] Disconnecting encoder");
        CleanupTrace($"Disconnecting encoder START hwnd={session.Hwnd} streamId={session.StreamId}");
        var streamer = session.Streamer;
        if (streamer != null)
        {
            Msg("[Cleanup] Detaching capture frame callback");
            CleanupTrace("Detach OnGpuFrame START");
            streamer.OnGpuFrame = null;
            CleanupTrace("Detach OnGpuFrame DONE");
        }
        DesktopSession replacementDriver = null;
        FfmpegEncoder replacementEncoder = null;
        if (session.StreamId > 0)
        {
            CleanupTrace($"Shared stream driver-transfer lock ENTER stream={session.StreamId}");
            lock (_sharedStreams)
            {
                CleanupTrace($"Shared stream driver-transfer lock ACQUIRED stream={session.StreamId}");
                if (_sharedStreams.TryGetValue(session.Hwnd, out var shared) &&
                    shared.StreamId == session.StreamId &&
                    shared.DriverSession == session)
                {
                    shared.DriverSession = null;
                    foreach (var candidate in ActiveSessions)
                    {
                        if (candidate == session ||
                            candidate.Cleaned ||
                            candidate.Hwnd != session.Hwnd ||
                            candidate.StreamId != session.StreamId ||
                            candidate.Streamer == null)
                        {
                            continue;
                        }

                        replacementDriver = candidate;
                        replacementEncoder = shared.Encoder;
                        shared.DriverSession = candidate;
                        CleanupTrace($"Replacement driver selected hwnd={replacementDriver.Hwnd} stream={session.StreamId}");
                        break;
                    }
                }
            }
            CleanupTrace($"Shared stream driver-transfer lock EXIT stream={session.StreamId}");
        }
        if (replacementDriver != null && replacementEncoder != null)
        {
            var ticks = TraceStart($"ConnectEncoder replacement stream={session.StreamId}");
            ConnectEncoder(replacementDriver, replacementEncoder);
            TraceDone($"ConnectEncoder replacement stream={session.StreamId}", ticks);
            Msg($"[Cleanup] Transferred stream {session.StreamId} encoder driver to hwnd={replacementDriver.Hwnd}");
        }
        int streamId = session.StreamId;
        IntPtr hwnd = session.Hwnd;
        session.Streamer = null;
        CleanupTrace($"Session streamer nulled hwnd={hwnd} stream={streamId}");

        Msg($"[Cleanup] Queuing background dispose for stream {streamId}");
        CleanupTrace($"QueueUserWorkItem cleanup START stream={streamId}");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Msg($"[Cleanup:BG] === START === stream {streamId}");
                CleanupTrace($"BG cleanup ENTER stream={streamId} hwnd={hwnd}");

                AudioCapture audioToDispose = null;
                FfmpegEncoder encoderToDispose = null;
                bool shouldStopEncoder = false;
                if (streamId > 0)
                {
                    CleanupTrace($"BG shared stream lock ENTER stream={streamId}");
                    lock (_sharedStreams)
                    {
                        CleanupTrace($"BG shared stream lock ACQUIRED stream={streamId}");
                        if (_sharedStreams.TryGetValue(hwnd, out var shared) && shared.StreamId == streamId)
                        {
                            shared.RefCount--;
                            Msg($"[Cleanup:BG] Stream {shared.StreamId} refs now {shared.RefCount}");
                            if (shared.RefCount <= 0)
                            {
                                _sharedStreams.Remove(hwnd);
                                audioToDispose = shared.Audio;
                                encoderToDispose = shared.Encoder;
                                shouldStopEncoder = true;
                            }
                            else if (shared.DriverSession == session)
                            {
                                shared.DriverSession = null;
                            }
                        }
                        else
                        {
                            encoderToDispose = session.Encoder;
                            shouldStopEncoder = true;
                        }
                    }
                    CleanupTrace($"BG shared stream lock EXIT stream={streamId} shouldStopEncoder={shouldStopEncoder}");

                    if (shouldStopEncoder)
                    {
                        Msg($"[Cleanup:BG] Stopping encoder {streamId}...");
                        if (encoderToDispose != null)
                        {
                            var stopTicks = TraceStart($"FfmpegEncoder.Stop stream={streamId}");
                            encoderToDispose.Stop();
                            TraceDone($"FfmpegEncoder.Stop stream={streamId}", stopTicks);
                            var disposeTicks = TraceStart($"FfmpegEncoder.Dispose stream={streamId}");
                            encoderToDispose.Dispose();
                            TraceDone($"FfmpegEncoder.Dispose stream={streamId}", disposeTicks);
                        }
                        var serverTicks = TraceStart($"RtspServer.StopStream stream={streamId}");
                        RemoteRtspServer?.StopStream(streamId);
                        TraceDone($"RtspServer.StopStream stream={streamId}", serverTicks);
                        Msg($"[Cleanup:BG] Encoder {streamId} stopped");
                    }

                }

                if (streamer != null)
                {
                    Msg($"[Cleanup:BG] Stopping capture...");
                    var stopCaptureTicks = TraceStart($"DesktopStreamer.StopCapture stream={streamId}");
                    streamer.StopCapture();
                    TraceDone($"DesktopStreamer.StopCapture stream={streamId}", stopCaptureTicks);
                    Msg($"[Cleanup:BG] Capture stopped");

                    try
                    {
                        Msg($"[Cleanup:BG] Flushing D3D context...");
                        var flushTicks = TraceStart($"DesktopStreamer.FlushD3dContext stream={streamId}");
                        streamer.FlushD3dContext();
                        TraceDone($"DesktopStreamer.FlushD3dContext stream={streamId}", flushTicks);
                        Msg($"[Cleanup:BG] D3D context flushed");
                    }
                    catch (Exception ex)
                    {
                        Msg($"[Cleanup:BG] D3D flush error: {ex.Message}");
                    }
                }

                Msg($"[Cleanup:BG] Disposing streamer...");
                var streamerDisposeTicks = TraceStart($"DesktopStreamer.Dispose stream={streamId}");
                streamer?.Dispose();
                TraceDone($"DesktopStreamer.Dispose stream={streamId}", streamerDisposeTicks);
                Msg($"[Cleanup:BG] Streamer disposed");

                if (audioToDispose != null)
                {
                    Msg($"[Cleanup:BG] Disposing audio...");
                    var audioTicks = TraceStart($"AudioCapture.Dispose stream={streamId}");
                    audioToDispose.Dispose();
                    TraceDone($"AudioCapture.Dispose stream={streamId}", audioTicks);
                    Msg($"[Cleanup:BG] Audio disposed");
                }

                Msg($"[Cleanup:BG] === DONE === stream {streamId}");
                CleanupTrace($"BG cleanup DONE stream={streamId}");
            }
            catch (Exception ex)
            {
                Msg($"[Cleanup:BG] ERROR: {ex}");
                CleanupTrace($"BG cleanup ERROR stream={streamId}: {ex}");
            }
        });
        CleanupTrace($"QueueUserWorkItem cleanup DONE stream={streamId}");
        Msg($"[Cleanup] === END (bg queued) === stream {streamId}");
        CleanupTrace($"CleanupSession EXIT stream={streamId}");
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

    private static void ApplySessionVisualResize(DesktopSession session, int width, int height)
    {
        if (session == null || session.Cleaned || width <= 0 || height <= 0) return;

        if (session.Canvas != null && !session.Canvas.IsDestroyed)
            session.Canvas.Size.Value = new float2(width, height);

        session.OnResize?.Invoke(width, height);
        Msg($"[UpdateLoop] Visual resize applied to {width}x{height}");
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

        IntPtr latestTexture = session.Streamer.SharedTexture;
        int latestWidth = session.Streamer.SharedTextureWidth;
        int latestHeight = session.Streamer.SharedTextureHeight;
        IntPtr latestDevice = session.Streamer.D3dDevice;
        if (latestTexture != IntPtr.Zero && latestDevice != IntPtr.Zero && latestWidth > 0 && latestHeight > 0)
        {
            enc.StartInitializeAsync(latestDevice, (uint)latestWidth, (uint)latestHeight, contextLock, audioForEncoder);
            enc.QueueFrame(latestTexture, (uint)latestWidth, (uint)latestHeight);
            Msg($"[RemoteStream] Seeded encoder from latest captured frame {latestWidth}x{latestHeight}");
        }
    }
}

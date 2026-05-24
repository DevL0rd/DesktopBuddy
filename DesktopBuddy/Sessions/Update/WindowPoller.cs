using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private const int WindowPollIntervalMs = 250;

    private static void EnsureAutoSpawnWindowBaseline(IEnumerable<WindowEnumerator.WindowInfo> openWindows = null, HashSet<IntPtr> activeWindows = null)
    {
        lock (AutoSpawnSeenWindows)
        {
            if (AutoSpawnWindowBaselineInitialized)
                return;

            try
            {
                openWindows ??= WindowEnumerator.GetOpenWindows();
                foreach (var win in openWindows)
                {
                    if (win.Handle != IntPtr.Zero)
                        AutoSpawnSeenWindows.Add(win.Handle);
                }
            }
            catch (Exception ex)
            {
                Msg($"[WindowPoller] Error building open-window baseline: {ex.Message}");
            }

            if (activeWindows != null)
            {
                foreach (var hwnd in activeWindows)
                    AutoSpawnSeenWindows.Add(hwnd);
            }

            AutoSpawnWindowBaselineInitialized = true;
            Msg($"[WindowPoller] Baseline open windows for auto-spawn: {AutoSpawnSeenWindows.Count}");
        }
    }

    private static void WindowPollerLoop()
    {
        while (_windowPollerRunning)
        {
            Thread.Sleep(WindowPollIntervalMs);
            if (!_windowPollerRunning) break;

            DesktopSession[] snapshot;
            try { snapshot = ActiveSessions.ToArray(); }
            catch { continue; }

            if (snapshot.Length == 0)
            {
                lock (AutoSpawnSeenWindows)
                {
                    AutoSpawnSeenWindows.Clear();
                    AutoSpawnWindowBaselineInitialized = false;
                }
                continue;
            }

            var activeWindows = new HashSet<IntPtr>();
            DesktopSession spawnAnchor = null;
            foreach (var session in snapshot)
            {
                if (session.Cleaned || session.Root == null || session.Root.IsDestroyed)
                    continue;

                spawnAnchor ??= session;
                if (session.Hwnd != IntPtr.Zero)
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
                    }
                    catch (Exception ex)
                    {
                        Msg($"[WindowPoller] Error for session hwnd={session.Hwnd}: {ex.Message}");
                    }
                }
            }

            if (spawnAnchor == null)
                continue;

            bool spawnNewWindows = Config?.GetValue(SpawnNewWindowsInGame) ?? true;
            if (!spawnNewWindows)
            {
                lock (AutoSpawnSeenWindows)
                {
                    AutoSpawnSeenWindows.Clear();
                    AutoSpawnWindowBaselineInitialized = false;
                }
                continue;
            }

            List<WindowEnumerator.WindowInfo> openWindows;
            try
            {
                openWindows = WindowEnumerator.GetOpenWindows();
            }
            catch (Exception ex)
            {
                Msg($"[WindowPoller] Error enumerating open windows: {ex.Message}");
                continue;
            }

            lock (AutoSpawnSeenWindows)
            {
                if (!AutoSpawnWindowBaselineInitialized)
                {
                    EnsureAutoSpawnWindowBaseline(openWindows, activeWindows);
                    continue;
                }

                foreach (var win in openWindows)
                {
                    if (win.Handle == IntPtr.Zero) continue;
                    if (activeWindows.Contains(win.Handle)) continue;
                    if (WindowEnumerator.IsResoniteWindow(win.Title)) continue;
                    if (AutoSpawnSeenWindows.Contains(win.Handle)) continue;

                    AutoSpawnSeenWindows.Add(win.Handle);

                    _windowEvents.Enqueue(new WindowEvent
                    {
                        Session = spawnAnchor,
                        EventType = WindowEventType.NewTopLevelWindow,
                        WindowHwnd = win.Handle,
                        Title = win.Title
                    });
                }
            }
        }
    }

}

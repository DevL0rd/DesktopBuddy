using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

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

}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public static partial class WindowInput
{

    public static bool FocusWindow(IntPtr hWnd)
    {
        return RequestFocus(hWnd);
    }

    public static bool IsFocusedForInput(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return true;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == hWnd) return true;

        var targetRoot = FocusRoot(hWnd);
        var foregroundRoot = FocusRoot(foreground);
        return targetRoot != IntPtr.Zero && targetRoot == foregroundRoot;
    }

    public static bool EnsureFocusedForInput(IntPtr hWnd)
    {
        if (IsFocusedForInput(hWnd)) return true;
        if (hWnd == IntPtr.Zero) return true;

        return RequestFocus(hWnd) && IsFocusedForInput(hWnd);
    }

    public static void SendWhenFocused(IntPtr hWnd, Action sendInput, string description = "input")
    {
        if (sendInput == null) return;
        if (hWnd == IntPtr.Zero || IsFocusedForInput(hWnd))
        {
            sendInput();
            return;
        }

        StartFocusEventHook();
        DateTime now = DateTime.UtcNow;
        bool requestFocus = false;
        lock (_focusQueueLock)
        {
            DropExpiredQueuesLocked(now);

            IntPtr focusRoot = FocusRoot(hWnd);
            if (!_focusQueues.TryGetValue(focusRoot, out var queue))
            {
                queue = new FocusInputQueue
                {
                    Hwnd = hWnd,
                    FocusRoot = focusRoot,
                    StartedUtc = now,
                    LastFocusRequestUtc = DateTime.MinValue
                };
                _focusQueues.Add(focusRoot, queue);
                Log.Msg($"[WindowInput] Queueing {description} until focus hwnd={hWnd} root={focusRoot} foreground={GetForegroundWindow()}");
            }

            if (queue.Inputs.Count >= FocusQueueMaxEvents)
            {
                Log.Msg($"[WindowInput] Focus queue overflow hwnd={hWnd}; dropping {queue.Inputs.Count} queued events");
                queue.Inputs.Clear();
                queue.StartedUtc = now;
            }

            queue.Inputs.Add(new QueuedInput(sendInput, description));
            if ((now - queue.LastFocusRequestUtc).TotalMilliseconds >= 100)
            {
                queue.LastFocusRequestUtc = now;
                requestFocus = true;
            }
        }

        if (requestFocus) RequestFocus(hWnd);
        FlushFocusedQueues();
    }

    public static void SendAtPointWhenTargetAcceptable(
        IntPtr hWnd,
        float u,
        float v,
        int clientW,
        int clientH,
        IntPtr monitorHandle,
        Action sendInput,
        string description = "input")
    {
        if (sendInput == null) return;
        if (hWnd == IntPtr.Zero)
        {
            sendInput();
            return;
        }

        var screenPoint = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
        if (IsPointAcceptedForInput(hWnd, screenPoint, out var hitHwnd, out var reason))
        {
            if (!IsFocusedForInput(hWnd) && description.IndexOf("move", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Log.Msg($"[WindowInput] Sending {description} without parent refocus; point hit target popup/family hwnd={hitHwnd} reason={reason}");
            }
            sendInput();
            return;
        }

        SendWhenFocused(hWnd, sendInput, description);
    }

    private static bool IsPointAcceptedForInput(IntPtr hWnd, POINT screenPoint, out IntPtr hitHwnd, out string reason)
    {
        hitHwnd = WindowFromPoint(screenPoint);
        reason = "none";

        if (hitHwnd == IntPtr.Zero) return false;
        if (IsFocusedForInput(hWnd))
        {
            reason = "focused";
            return true;
        }

        if (IsTargetFamilyWindow(hWnd, hitHwnd))
        {
            reason = "target-family";
            return true;
        }

        if (IsTargetMenuActiveAtPoint(hWnd, hitHwnd))
        {
            reason = "target-menu";
            return true;
        }

        return false;
    }

    private static bool IsTargetFamilyWindow(IntPtr targetHwnd, IntPtr candidateHwnd)
    {
        if (targetHwnd == IntPtr.Zero || candidateHwnd == IntPtr.Zero) return false;
        if (candidateHwnd == targetHwnd) return true;

        var targetRoot = FocusRoot(targetHwnd);
        var candidateRoot = FocusRoot(candidateHwnd);
        if (targetRoot != IntPtr.Zero && candidateRoot == targetRoot) return true;

        return OwnerChainContains(candidateHwnd, targetHwnd) ||
               (targetRoot != IntPtr.Zero && OwnerChainContains(candidateHwnd, targetRoot));
    }

    private static bool OwnerChainContains(IntPtr hwnd, IntPtr target)
    {
        if (hwnd == IntPtr.Zero || target == IntPtr.Zero) return false;

        var current = GetWindow(hwnd, GW_OWNER);
        int guard = 0;
        while (current != IntPtr.Zero && guard++ < 32)
        {
            if (current == target) return true;
            current = GetWindow(current, GW_OWNER);
        }

        return false;
    }

    private static bool IsTargetMenuActiveAtPoint(IntPtr targetHwnd, IntPtr hitHwnd)
    {
        uint targetThread = GetWindowThreadProcessId(targetHwnd, out uint targetProcess);
        uint hitThread = GetWindowThreadProcessId(hitHwnd, out uint hitProcess);
        if (targetThread == 0 || hitThread == 0) return false;
        if (targetProcess != 0 && hitProcess != 0 && targetProcess != hitProcess) return false;

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(targetThread, ref info)) return false;

        bool inMenu = (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0;
        if (!inMenu) return false;

        if (IsTargetFamilyWindow(targetHwnd, info.hwndMenuOwner)) return true;
        if (info.hwndActive != IntPtr.Zero && IsTargetFamilyWindow(targetHwnd, info.hwndActive)) return true;
        if (info.hwndFocus != IntPtr.Zero && IsTargetFamilyWindow(targetHwnd, info.hwndFocus)) return true;
        if (OwnerChainContains(hitHwnd, info.hwndMenuOwner)) return true;

        return false;
    }

    private static bool RequestFocus(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return true;

        IntPtr focusHwnd = FocusRoot(hWnd);
        if (focusHwnd == IntPtr.Zero) return false;
        if (IsFocusedForInput(hWnd)) return true;

        RestoreIfMinimized(focusHwnd);

        IntPtr foregroundBefore = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(focusHwnd, out uint targetProcess);
        uint foregroundThread = foregroundBefore != IntPtr.Zero
            ? GetWindowThreadProcessId(foregroundBefore, out _)
            : 0;

        bool attachedForeground = false;
        bool attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }

            BringWindowToTop(focusHwnd);
            SetWindowPos(focusHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetActiveWindow(focusHwnd);
            SetFocus(focusHwnd);

            bool ok = SetForegroundWindow(focusHwnd);
            bool focused = IsFocusedForInput(hWnd);
            if (focused)
            {
                FlushFocusedQueues();
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            IntPtr foregroundAfter = GetForegroundWindow();
            Log.Msg(
                $"[WindowInput] Focus request failed ok={ok} err={err} hwnd={focusHwnd} original={hWnd} " +
                $"foregroundBefore={foregroundBefore} foregroundAfter={foregroundAfter} " +
                $"currentTid={currentThread} foregroundTid={foregroundThread} targetTid={targetThread} targetPid={targetProcess} " +
                $"attachForeground={attachedForeground} attachTarget={attachedTarget}");
            return false;
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void StartFocusEventHook()
    {
        if (_focusHookStarted) return;
        lock (_focusQueueLock)
        {
            if (_focusHookStarted) return;
            _focusHookStarted = true;
            _focusHookThread = new Thread(FocusHookThreadMain)
            {
                IsBackground = true,
                Name = "DesktopBuddy Focus Event Hook"
            };
            _focusHookThread.Start();
        }
    }

    private static void FocusHookThreadMain()
    {
        _focusHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundEventCallback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_focusHook == IntPtr.Zero)
        {
            Log.Msg($"[WindowInput] SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        Log.Msg("[WindowInput] Foreground event hook installed");
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void OnForegroundEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || idObject != OBJECTID_WINDOW)
            return;

        FlushFocusedQueues();
    }

    private static void FlushFocusedQueues()
    {
        List<QueuedInput> toSend = null;
        DateTime now = DateTime.UtcNow;

        lock (_focusQueueLock)
        {
            DropExpiredQueuesLocked(now);
            foreach (var kv in _focusQueues)
            {
                if (!IsFocusedForInput(kv.Value.Hwnd)) continue;

                toSend ??= new List<QueuedInput>();
                toSend.AddRange(kv.Value.Inputs);
                kv.Value.Inputs.Clear();
            }

            if (toSend != null)
            {
                var emptyKeys = new List<IntPtr>();
                foreach (var kv in _focusQueues)
                {
                    if (kv.Value.Inputs.Count == 0)
                        emptyKeys.Add(kv.Key);
                }
                foreach (var key in emptyKeys)
                    _focusQueues.Remove(key);
            }
        }

        if (toSend == null) return;

        foreach (var input in toSend)
        {
            try { input.Send(); }
            catch (Exception ex) { Log.Msg($"[WindowInput] Queued {input.Description} failed: {ex.Message}"); }
        }
        Log.Msg($"[WindowInput] Flushed {toSend.Count} queued inputs after focus");
    }

    private static void DropExpiredQueuesLocked(DateTime now)
    {
        List<IntPtr> expired = null;
        foreach (var kv in _focusQueues)
        {
            if ((now - kv.Value.StartedUtc).TotalMilliseconds < FocusQueueTimeoutMs)
                continue;

            expired ??= new List<IntPtr>();
            expired.Add(kv.Key);
            Log.Msg($"[WindowInput] Dropping {kv.Value.Inputs.Count} queued inputs after focus timeout hwnd={kv.Value.Hwnd}");
        }

        if (expired == null) return;
        foreach (var key in expired)
            _focusQueues.Remove(key);
    }

}

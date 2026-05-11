using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public static class WindowInput
{

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, IntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint GW_OWNER = 4;
    private const uint GA_ROOTOWNER = 3;
    private const uint GUI_INMENUMODE = 0x00000004;
    private const uint GUI_POPUPMENUMODE = 0x00000010;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJECTID_WINDOW = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    private const uint TOUCH_FEEDBACK_NONE = 0x3;

    private const uint POINTER_FLAG_INRANGE    = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT  = 0x00000004;
    private const uint POINTER_FLAG_DOWN       = 0x00010000;
    private const uint POINTER_FLAG_UPDATE     = 0x00020000;
    private const uint POINTER_FLAG_UP         = 0x00040000;

    private const uint PT_TOUCH = 0x00000002;

    private const uint TOUCH_FLAG_NONE = 0x00000000;

    private const uint TOUCH_MASK_CONTACTAREA = 0x00000004;
    private const uint TOUCH_MASK_ORIENTATION = 0x00000008;
    private const uint TOUCH_MASK_PRESSURE    = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private static bool _touchInitialized;
    private static readonly object _initLock = new();
    private const uint MAX_TOUCH_CONTACTS = 10;

    private static readonly POINT[] _lastPosition = new POINT[MAX_TOUCH_CONTACTS];
    private static readonly bool[] _moveFailLogged = new bool[MAX_TOUCH_CONTACTS];
    private static readonly POINTER_TOUCH_INFO[] _touchArr = new POINTER_TOUCH_INFO[1];
    private static readonly object _sendLock = new();
    private static readonly object _focusQueueLock = new();
    private static readonly Dictionary<IntPtr, FocusInputQueue> _focusQueues = new();
    private static readonly WinEventDelegate _foregroundEventCallback = OnForegroundEvent;
    private static Thread _focusHookThread;
    private static bool _focusHookStarted;
    private static IntPtr _focusHook;
    private const int FocusQueueTimeoutMs = 500;
    private const int FocusQueueMaxEvents = 64;

    private sealed class FocusInputQueue
    {
        public IntPtr Hwnd;
        public IntPtr FocusRoot;
        public DateTime StartedUtc;
        public DateTime LastFocusRequestUtc;
        public readonly List<QueuedInput> Inputs = new();
    }

    private readonly struct QueuedInput
    {
        public readonly Action Send;
        public readonly string Description;

        public QueuedInput(Action send, string description)
        {
            Send = send;
            Description = description;
        }
    }

    private static bool EnsureTouchInit()
    {
        if (_touchInitialized) return true;
        lock (_initLock)
        {
            if (_touchInitialized) return true;
            bool ok = InitializeTouchInjection(MAX_TOUCH_CONTACTS, TOUCH_FEEDBACK_NONE);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[WindowInput] InitializeTouchInjection FAILED err={err}");
                return false;
            }
            _touchInitialized = true;
            Log.Msg($"[WindowInput] Touch injection initialized (max={MAX_TOUCH_CONTACTS}, feedback=NONE)");
            return true;
        }
    }

    internal static bool PrewarmTouchInjection()
    {
        StartFocusEventHook();
        return EnsureTouchInit();
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void RestoreIfMinimized(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (IsIconic(hWnd))
        {
            Log.Msg($"[WindowInput] Restoring minimized window hwnd={hWnd}");
            ShowWindow(hWnd, SW_RESTORE);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private static int NormalizedToPixel(float value, int size)
    {
        if (size <= 1 || float.IsNaN(value) || float.IsInfinity(value))
            return 0;

        int max = size - 1;
        float scaled = value * size;
        if (scaled <= 0f) return 0;
        if (scaled >= max) return max;
        return (int)scaled;
    }

    private static bool TryGetWindowCaptureBounds(IntPtr hWnd, out RECT bounds)
    {
        bounds = default;
        if (hWnd == IntPtr.Zero)
            return false;

        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) == 0 &&
            bounds.Width > 0 && bounds.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hWnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    private static POINT UvToScreen(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        int px = NormalizedToPixel(u, clientW);
        int py = NormalizedToPixel(v, clientH);
        if (hWnd != IntPtr.Zero)
        {
            if (TryGetWindowCaptureBounds(hWnd, out var bounds))
            {
                return new POINT
                {
                    X = bounds.Left + NormalizedToPixel(u, bounds.Width),
                    Y = bounds.Top + NormalizedToPixel(v, bounds.Height)
                };
            }

            var pt = new POINT { X = px, Y = py };
            ClientToScreen(hWnd, ref pt);
            return pt;
        }

        var monitorPoint = new POINT { X = px, Y = py };
        if (monitorHandle != IntPtr.Zero)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitorHandle, ref mi))
            {
                monitorPoint.X += mi.rcMonitor.Left;
                monitorPoint.Y += mi.rcMonitor.Top;
            }
        }
        return monitorPoint;
    }

    private static IntPtr FocusRoot(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hWnd, GA_ROOTOWNER);
        return root != IntPtr.Zero ? root : hWnd;
    }

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

    public static void SendHover(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
        SetCursorPos(pt.X, pt.Y);
    }

    public static void SendTouchDown(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        lock (_sendLock)
        {
            if (!EnsureTouchInit()) return;
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            if (touchId < MAX_TOUCH_CONTACTS)
            {
                _lastPosition[touchId] = pt;
                _moveFailLogged[touchId] = false;
            }

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            contact.touchFlags = TOUCH_FLAG_NONE;
            contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
            contact.orientation = 90;
            contact.pressure = 32000;
            contact.rcContact.Top = pt.Y - 2;
            contact.rcContact.Bottom = pt.Y + 2;
            contact.rcContact.Left = pt.X - 2;
            contact.rcContact.Right = pt.X + 2;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[Touch] Down FAILED id={touchId} screen=({pt.X},{pt.Y}) err={err}");
            }
            else
            {
                Log.Msg($"[Touch] Down OK id={touchId} screen=({pt.X},{pt.Y})");
            }
        }
    }

    public static void SendTouchMove(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        lock (_sendLock)
        {
            if (!_touchInitialized) return;
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            if (touchId < MAX_TOUCH_CONTACTS) _lastPosition[touchId] = pt;

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            contact.touchFlags = TOUCH_FLAG_NONE;
            contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
            contact.orientation = 90;
            contact.pressure = 32000;
            contact.rcContact.Top = pt.Y - 2;
            contact.rcContact.Bottom = pt.Y + 2;
            contact.rcContact.Left = pt.X - 2;
            contact.rcContact.Right = pt.X + 2;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                if (touchId < MAX_TOUCH_CONTACTS && !_moveFailLogged[touchId])
                {
                    _moveFailLogged[touchId] = true;
                    int err = Marshal.GetLastWin32Error();
                    Log.Msg($"[Touch] Move FAILED id={touchId} err={err} (further move errors suppressed)");
                }
            }
        }
    }

    public static void SendTouchUp(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        lock (_sendLock)
        {
            if (!_touchInitialized) return;
            var pt = (touchId < MAX_TOUCH_CONTACTS) ? _lastPosition[touchId] : UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_UP;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[Touch] Up FAILED id={touchId} err={err}");
            }
            else
            {
                Log.Msg($"[Touch] Up OK id={touchId}");
            }
        }
    }

    public static void SendScroll(IntPtr hWnd, float u, float v, int clientW, int clientH, int wheelDelta, IntPtr monitorHandle = default)
    {
        lock (_sendLock)
        {
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            SetCursorPos(pt.X, pt.Y);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, IntPtr.Zero);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public static void SendString(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Log.Msg($"[Keyboard] SendString: \"{text}\"");
        var inputs = new INPUT[text.Length * 2];
        int idx = 0;
        foreach (char c in text)
        {
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                    }
                }
            };
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    }
                }
            };
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Msg($"[Keyboard] SendString FAILED sent={sent}/{inputs.Length} err={err}");
        }
    }

    public static void SendVirtualKey(ushort vk)
    {
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Msg($"[Keyboard] SendVirtualKey FAILED vk=0x{vk:X2} sent={sent}/{inputs.Length} err={err}");
        }
    }

    private static readonly HashSet<ushort> _heldModifiers = new();

    public static void SendVirtualKeyDown(ushort vk)
    {
        if (_heldModifiers.Contains(vk)) return;
        _heldModifiers.Add(vk);
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendPaste()
    {
        Log.Msg("[Keyboard] Sending Ctrl+V (paste)");
        const ushort VK_CONTROL = 0xA2;
        const ushort VK_V = 0x56;
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Msg($"[Keyboard] SendPaste FAILED sent={sent}/{inputs.Length} err={Marshal.GetLastWin32Error()}");
    }

    public static void ReleaseAllModifiers()
    {
        if (_heldModifiers.Count == 0) return;
        var inputs = new INPUT[_heldModifiers.Count];
        int i = 0;
        foreach (var vk in _heldModifiers)
        {
            inputs[i++] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Log.Msg($"[Keyboard] Released {_heldModifiers.Count} modifiers");
        _heldModifiers.Clear();
    }

}

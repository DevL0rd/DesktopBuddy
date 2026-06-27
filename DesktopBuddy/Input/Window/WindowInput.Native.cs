using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBuddy;

public static partial class WindowInput
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

}

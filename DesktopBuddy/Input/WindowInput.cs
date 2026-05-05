using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ResoniteModLoader;

namespace DesktopBuddy;

public static class WindowInput
{

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, IntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_WHEEL = 0x0800;

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
    }

    private static bool _touchInitialized;
    private static readonly object _initLock = new();
    private const uint MAX_TOUCH_CONTACTS = 10;

    private static readonly POINT[] _lastPosition = new POINT[MAX_TOUCH_CONTACTS];
    private static readonly bool[] _moveFailLogged = new bool[MAX_TOUCH_CONTACTS];
    private static readonly POINTER_TOUCH_INFO[] _touchArr = new POINTER_TOUCH_INFO[1];

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

    private static POINT UvToScreen(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        int px = (int)(u * clientW);
        int py = (int)(v * clientH);
        var pt = new POINT { X = px, Y = py };
        if (hWnd != IntPtr.Zero)
        {
            ClientToScreen(hWnd, ref pt);
        }
        else if (monitorHandle != IntPtr.Zero)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitorHandle, ref mi))
            {
                pt.X += mi.rcMonitor.Left;
                pt.Y += mi.rcMonitor.Top;
            }
        }
        return pt;
    }

    public static void FocusWindow(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
            SetForegroundWindow(hWnd);
    }

    public static void SendHover(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
        SetCursorPos(pt.X, pt.Y);
    }

    public static void SendTouchDown(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
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

    public static void SendTouchMove(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
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

    public static void SendTouchUp(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
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

    public static void SendScroll(IntPtr hWnd, float u, float v, int clientW, int clientH, int wheelDelta, IntPtr monitorHandle = default)
    {
        var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
        SetCursorPos(pt.X, pt.Y);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, IntPtr.Zero);
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

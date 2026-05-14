using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public static partial class WindowInput
{

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

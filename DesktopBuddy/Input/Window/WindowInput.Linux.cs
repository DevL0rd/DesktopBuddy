using System.Collections.Generic;

namespace DesktopBuddy;

public static partial class WindowInput
{

    private const int BtnLeft = 0x110;

    internal static ulong LinuxInputSession;

    private static LinuxNativeBridge _linuxInput;
    private static readonly object _linuxInputLock = new();

    private static LinuxNativeBridge LinuxInput()
    {
        if (_linuxInput != null) return _linuxInput;
        lock (_linuxInputLock)
            _linuxInput ??= new LinuxNativeBridge();
        return _linuxInput;
    }

    internal static void LinuxMove(float u, float v)
    {
        ulong s = LinuxInputSession;
        if (s != 0) LinuxInput().InputMotion(s, u, v);
    }

    internal static void LinuxButtonAt(float u, float v, bool pressed)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        var b = LinuxInput();
        b.InputMotion(s, u, v);
        b.InputButton(s, BtnLeft, pressed);
    }

    internal static void LinuxButton(bool pressed)
    {
        ulong s = LinuxInputSession;
        if (s != 0) LinuxInput().InputButton(s, BtnLeft, pressed);
    }

    internal static void LinuxScroll(int wheelDelta)
    {
        ulong s = LinuxInputSession;
        if (s == 0 || wheelDelta == 0) return;

        LinuxInput().InputScroll(s, wheelDelta > 0 ? 1 : -1);
    }

    internal static void LinuxTypeString(string text)
    {
        ulong s = LinuxInputSession;
        if (s == 0 || string.IsNullOrEmpty(text)) return;
        var b = LinuxInput();
        foreach (char c in text)
        {
            int keysym = CharToKeysym(c);
            b.InputKey(s, keysym, true);
            b.InputKey(s, keysym, false);
        }
    }

    internal static void LinuxKey(ushort vk, bool pressed)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        int keysym = VkToKeysym(vk);
        if (keysym != 0) LinuxInput().InputKey(s, keysym, pressed);
    }

    internal static void LinuxTapKey(ushort vk)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        int keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var b = LinuxInput();
        b.InputKey(s, keysym, true);
        b.InputKey(s, keysym, false);
    }

    private static int CharToKeysym(char c) => c <= 0xFF ? c : (0x01000000 + c);

    private static readonly Dictionary<ushort, int> _vkKeysyms = new()
    {
        [0x08] = 0xFF08,
        [0x09] = 0xFF09,
        [0x0D] = 0xFF0D,
        [0x1B] = 0xFF1B,
        [0x20] = 0x0020,
        [0x21] = 0xFF55,
        [0x22] = 0xFF56,
        [0x23] = 0xFF57,
        [0x24] = 0xFF50,
        [0x25] = 0xFF51,
        [0x26] = 0xFF52,
        [0x27] = 0xFF53,
        [0x28] = 0xFF54,
        [0x2E] = 0xFFFF,
        [0x10] = 0xFFE1,
        [0xA0] = 0xFFE1,
        [0xA1] = 0xFFE2,
        [0x11] = 0xFFE3,
        [0xA2] = 0xFFE3,
        [0xA3] = 0xFFE4,
        [0x12] = 0xFFE9,
        [0xA4] = 0xFFE9,
        [0xA5] = 0xFFEA,
    };

    private static int VkToKeysym(ushort vk)
    {
        if (_vkKeysyms.TryGetValue(vk, out int ks))
            return ks;

        if (vk >= 0x41 && vk <= 0x5A)
            return 0x61 + (vk - 0x41);

        if (vk >= 0x30 && vk <= 0x39)
            return vk;
        return 0;
    }
}

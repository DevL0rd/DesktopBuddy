using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using FrooxEngine;
using FrooxEngine.UIX;
using Key = Renderite.Shared.Key;

namespace DesktopBuddy;

[HarmonyPatch(typeof(InteractionHandler), nameof(InteractionHandler.BeforeInputUpdate))]
static class LocomotionSuppressionPatch
{
    private static readonly FieldInfo _inputsField = typeof(InteractionHandler)
        .GetField("_inputs", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Postfix(InteractionHandler __instance)
    {
        try
        {
            if (DesktopBuddyMod.DesktopCanvasIds.Count == 0) return;
            var touchable = __instance.Laser?.CurrentTouchable;
            if (touchable == null) return;

            if (touchable is Canvas canvas && DesktopBuddyMod.DesktopCanvasIds.Contains(canvas.ReferenceID))
            {
                if (_inputsField?.GetValue(__instance) is InteractionHandlerInputs inputs)
                    inputs.Axis.RegisterBlocks = true;
            }
        }
        catch
        {
        }
    }
}

static class KeyMapper
{
    public static readonly Dictionary<Key, ushort> KeyToVK = new()
    {
        { Key.Backspace, 0x08 }, { Key.Tab, 0x09 }, { Key.Return, 0x0D },
        { Key.Escape, 0x1B }, { Key.Space, 0x20 }, { Key.Delete, 0x2E },
        { Key.UpArrow, 0x26 }, { Key.DownArrow, 0x28 },
        { Key.LeftArrow, 0x25 }, { Key.RightArrow, 0x27 },
        { Key.Home, 0x24 }, { Key.End, 0x23 },
        { Key.PageUp, 0x21 }, { Key.PageDown, 0x22 },
        { Key.LeftShift, 0xA0 }, { Key.RightShift, 0xA1 },
        { Key.LeftControl, 0xA2 }, { Key.RightControl, 0xA3 },
        { Key.LeftAlt, 0xA4 }, { Key.RightAlt, 0xA5 },
        { Key.LeftWindows, 0x5B }, { Key.RightWindows, 0x5C },
        { Key.F1, 0x70 }, { Key.F2, 0x71 }, { Key.F3, 0x72 }, { Key.F4, 0x73 },
        { Key.F5, 0x74 }, { Key.F6, 0x75 }, { Key.F7, 0x76 }, { Key.F8, 0x77 },
        { Key.F9, 0x78 }, { Key.F10, 0x79 }, { Key.F11, 0x7A }, { Key.F12, 0x7B },
    };

    public static bool IsModifier(Key key) =>
        key == Key.LeftShift || key == Key.RightShift ||
        key == Key.LeftControl || key == Key.RightControl ||
        key == Key.LeftAlt || key == Key.RightAlt;
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.SimulatePress))]
static class SimulatePressPatch
{
    static bool Prefix(Key key, World origin)
    {
        for (int i = 0; i < DesktopBuddyMod.ActiveSessions.Count; i++)
        {
            var s = DesktopBuddyMod.ActiveSessions[i];
            if (s.Root?.World == origin && s.KeyboardSource != null && !s.KeyboardSource.IsDestroyed)
            {
                s.KeyboardSource.SendKey(key);
                return false;
            }
        }
        return true;
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.TypeAppend))]
static class TypeAppendPatch
{
    static bool Prefix(string typeDelta, World origin)
    {
        for (int i = 0; i < DesktopBuddyMod.ActiveSessions.Count; i++)
        {
            var s = DesktopBuddyMod.ActiveSessions[i];
            if (s.Root?.World == origin && s.KeyboardSource != null && !s.KeyboardSource.IsDestroyed)
            {
                s.KeyboardSource.TypeString(typeDelta);
                return false;
            }
        }
        return true;
    }
}

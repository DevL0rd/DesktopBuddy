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

            if (touchable is DesktopCurvedScreenInput ||
                (touchable is Canvas canvas && DesktopBuddyMod.DesktopCanvasIds.Contains(canvas.ReferenceID)))
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
        { Key.Clear, 0x0C }, { Key.Pause, 0x13 },
        { Key.UpArrow, 0x26 }, { Key.DownArrow, 0x28 },
        { Key.LeftArrow, 0x25 }, { Key.RightArrow, 0x27 },
        { Key.Insert, 0x2D }, { Key.Home, 0x24 }, { Key.End, 0x23 },
        { Key.PageUp, 0x21 }, { Key.PageDown, 0x22 },
        { Key.LeftShift, 0xA0 }, { Key.RightShift, 0xA1 },
        { Key.Shift, 0xA0 },
        { Key.LeftControl, 0xA2 }, { Key.RightControl, 0xA3 },
        { Key.Control, 0xA2 },
        { Key.LeftAlt, 0xA4 }, { Key.RightAlt, 0xA5 },
        { Key.Alt, 0xA4 }, { Key.AltGr, 0xA5 },
        { Key.LeftWindows, 0x5B }, { Key.RightWindows, 0x5C },
        { Key.Windows, 0x5B },
        { Key.F1, 0x70 }, { Key.F2, 0x71 }, { Key.F3, 0x72 }, { Key.F4, 0x73 },
        { Key.F5, 0x74 }, { Key.F6, 0x75 }, { Key.F7, 0x76 }, { Key.F8, 0x77 },
        { Key.F9, 0x78 }, { Key.F10, 0x79 }, { Key.F11, 0x7A }, { Key.F12, 0x7B },
        { Key.F13, 0x7C }, { Key.F14, 0x7D }, { Key.F15, 0x7E },
        { Key.Numlock, 0x90 }, { Key.CapsLock, 0x14 }, { Key.ScrollLock, 0x91 },
        { Key.Help, 0x2F }, { Key.Print, 0x2A },
        { Key.Comma, 0xBC }, { Key.Period, 0xBE }, { Key.Minus, 0xBD },
        { Key.Equals, 0xBB }, { Key.Semicolon, 0xBA }, { Key.Slash, 0xBF },
        { Key.BackQuote, 0xC0 }, { Key.LeftBracket, 0xDB },
        { Key.Backslash, 0xDC }, { Key.RightBracket, 0xDD }, { Key.Quote, 0xDE },
        { Key.KeypadPlus, 0x6B }, { Key.KeypadMinus, 0x6D },
        { Key.KeypadPeriod, 0x6E }, { Key.KeypadDivide, 0x6F },
        { Key.KeypadMultiply, 0x6A }, { Key.KeypadEnter, 0x0D },
    };

    public static bool IsModifier(Key key) =>
        key == Key.LeftShift || key == Key.RightShift ||
        key == Key.Shift ||
        key == Key.LeftControl || key == Key.RightControl ||
        key == Key.Control ||
        key == Key.LeftAlt || key == Key.RightAlt ||
        key == Key.Alt || key == Key.AltGr ||
        key == Key.LeftWindows || key == Key.RightWindows ||
        key == Key.Windows;

    public static bool TryGetVirtualKey(Key key, out ushort vk, out bool shift)
    {
        shift = false;
        if (key >= Key.Alpha0 && key <= Key.Alpha9)
        {
            vk = (ushort)(0x30 + (key - Key.Alpha0));
            return true;
        }
        if (key >= Key.A && key <= Key.Z)
        {
            vk = (ushort)(0x41 + (key - Key.A));
            return true;
        }
        if (key >= Key.Keypad0 && key <= Key.Keypad9)
        {
            vk = (ushort)(0x60 + (key - Key.Keypad0));
            return true;
        }
        if (KeyToVK.TryGetValue(key, out vk))
            return true;

        shift = true;
        vk = key switch
        {
            Key.Less => 0xBC,
            Key.Greater => 0xBE,
            Key.Underscore => 0xBD,
            Key.Exclaim => 0x31,
            Key.At => 0x32,
            Key.Hash => 0x33,
            Key.Dollar => 0x34,
            Key.Percent => 0x35,
            Key.Caret => 0x36,
            Key.Ampersand => 0x37,
            Key.Asterisk => 0x38,
            Key.LeftParenthesis => 0x39,
            Key.RightParenthesis => 0x30,
            Key.Colon => 0xBA,
            Key.Question => 0xBF,
            Key.Tilde => 0xC0,
            Key.LeftBrace => 0xDB,
            Key.VerticalBar => 0xDC,
            Key.RightBrace => 0xDD,
            Key.DoubleQuote => 0xDE,
            Key.Plus => 0xBB,
            _ => 0
        };
        return vk != 0;
    }
}

static class KeyboardInputRouter
{
    private static readonly Dictionary<IText, DesktopKeyboardSource> _targetSources = new();
    private static DesktopKeyboardSource _focusedSource;

    public static void RegisterTarget(DesktopKeyboardSource source, IText target)
    {
        if (source == null || target == null)
            return;

        _targetSources[target] = source;
    }

    public static void UnregisterTarget(DesktopKeyboardSource source, IText target)
    {
        if (target != null &&
            _targetSources.TryGetValue(target, out DesktopKeyboardSource registered) &&
            registered == source)
        {
            _targetSources.Remove(target);
        }

        ClearFocused(source);
    }

    public static void SetFocused(DesktopKeyboardSource source)
    {
        if (source != null && !source.IsDestroyed)
            _focusedSource = source;
    }

    public static void ClearFocused(DesktopKeyboardSource source)
    {
        if (_focusedSource == source)
            _focusedSource = null;
    }

    public static void KeyboardShown(IText target)
    {
        if (TryGetRegisteredSource(target) == null)
            _focusedSource = null;
    }

    public static void KeyboardHidden()
    {
        _focusedSource = null;
    }

    public static DesktopKeyboardSource Resolve(World origin, Slot pressedSlot = null)
    {
        try
        {
            return IsUsable(_focusedSource) ? _focusedSource : null;
        }
        catch (System.Exception ex)
        {
            Log.Msg($"[InputPatch] Keyboard resolve error: {ex}");
            return null;
        }
    }

    private static DesktopKeyboardSource TryGetRegisteredSource(IText target)
    {
        if (target == null || target.IsDestroyed)
            return null;

        if (!_targetSources.TryGetValue(target, out DesktopKeyboardSource source))
            return null;

        if (IsUsable(source))
            return source;

        _targetSources.Remove(target);
        return null;
    }

    private static bool IsUsable(DesktopKeyboardSource source) =>
        source != null && !source.IsDestroyed;
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.ShowKeyboard))]
static class ShowKeyboardPatch
{
    static void Prefix(IText targetText)
    {
        KeyboardInputRouter.KeyboardShown(targetText);
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.HideKeyboard))]
static class HideKeyboardPatch
{
    static void Postfix(InputInterface __instance)
    {
        if (!__instance.IsKeyboardActive)
            KeyboardInputRouter.KeyboardHidden();
    }
}

[HarmonyPatch(typeof(VirtualKey), nameof(VirtualKey.Press))]
static class VirtualKeyPressPatch
{
    static bool Prefix(VirtualKey __instance)
    {
        var source = KeyboardInputRouter.Resolve(__instance.World, __instance.Slot);
        if (source == null)
            return true;

        string text;
        Key key;
        if (__instance.UseModifier)
        {
            text = __instance.ModifiedAppendString.Value;
            key = __instance.ModifiedTargetKey.Value;
        }
        else if (__instance.IgnoreShift.Value ||
                 __instance.Keyboard.Target == null ||
                 !__instance.Keyboard.Target.ShiftActive.Value)
        {
            text = __instance.AppendString.Value;
            key = __instance.TargetKey.Value;
        }
        else
        {
            text = __instance.ShiftAppendString.Value;
            key = __instance.ShiftTargetKey.Value;
        }

        if (ShouldSendAsKey(text, key))
            source.SendKey(key);
        else if (!string.IsNullOrEmpty(text))
            source.TypeString(text);
        else if (key != Key.None)
            source.SendKey(key);

        var keyboard = __instance.Keyboard.Target;
        if (keyboard != null &&
            !__instance.IgnoreShift.Value &&
            keyboard.ShiftActive.Value &&
            !keyboard.HoldShift.Value)
        {
            keyboard.ShiftActive.Value = false;
            keyboard.HoldShift.Value = false;
        }

        return false;
    }

    private static bool ShouldSendAsKey(string text, Key key)
    {
        if (key == Key.None)
            return false;
        if (string.IsNullOrEmpty(text))
            return true;

        return key == Key.Backspace ||
               key == Key.Delete ||
               key == Key.Return ||
               key == Key.KeypadEnter ||
               key == Key.Tab ||
               key == Key.Escape ||
               key == Key.UpArrow ||
               key == Key.DownArrow ||
               key == Key.LeftArrow ||
               key == Key.RightArrow ||
               key == Key.Home ||
               key == Key.End ||
               key == Key.PageUp ||
               key == Key.PageDown ||
               key == Key.Insert;
    }
}

[HarmonyPatch(typeof(VirtualMultiKey), nameof(VirtualMultiKey.Pressed))]
static class VirtualMultiKeyPressedPatch
{
    static bool Prefix(VirtualMultiKey __instance, IButton button, ButtonEventData eventData)
    {
        if (eventData.source is not TouchSource { SafeTouchSource: not false })
            return false;

        var source = KeyboardInputRouter.Resolve(__instance.World, __instance.Slot);
        if (source == null)
            return true;

        foreach (Key key in __instance.Keys)
            source.SendKey(key);

        return false;
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.SimulatePress))]
static class SimulatePressPatch
{
    static bool Prefix(Key key, World origin)
    {
        try
        {
            if (origin != Userspace.UserspaceWorld && Userspace.HasFocus)
                return true;

            var source = KeyboardInputRouter.Resolve(origin);
            if (source != null)
            {
                source.SendKey(key);
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Log.Msg($"[InputPatch] SimulatePress error: {ex}");
        }
        return true;
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.TypeAppend))]
static class TypeAppendPatch
{
    static bool Prefix(string typeDelta, World origin)
    {
        try
        {
            if (origin != Userspace.UserspaceWorld && Userspace.HasFocus)
                return true;

            var source = KeyboardInputRouter.Resolve(origin);
            if (source != null)
            {
                source.TypeString(typeDelta);
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Log.Msg($"[InputPatch] TypeAppend error: {ex}");
        }
        return true;
    }
}

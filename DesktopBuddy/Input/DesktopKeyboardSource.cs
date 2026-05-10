using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Key = Renderite.Shared.Key;

namespace DesktopBuddy;

public class DesktopKeyboardSource : Component, IFocusable
{
    private Text _keyboardTargetText;

    public void Focus(User user)
    {
        if (user.IsLocalUser)
            KeyboardInputRouter.SetFocused(this);
    }

    public void Defocus(User user)
    {
        if (!user.IsLocalUser)
            return;

        KeyboardInputRouter.ClearFocused(this);
        InputInterface.HideKeyboard(null);
        ReleaseModifiers();
    }

    public void OpenKeyboard()
    {
        EnsureKeyboardTarget();

        float3 point = Slot.GlobalPosition;
        floatQ rotation = Slot.GlobalRotation;
        if (World != Userspace.UserspaceWorld)
        {
            point = WorldManager.TransferPoint(point, World, Userspace.UserspaceWorld);
            rotation = WorldManager.TransferRotation(rotation, World, Userspace.UserspaceWorld);
        }

        KeyboardInputRouter.SetFocused(this);
        InputInterface.ShowKeyboard(
            _keyboardTargetText,
            "",
            KeyboardType.Default,
            autocorrection: true,
            multiline: false,
            secure: false,
            textPlaceholder: "",
            characterLimit: 0,
            requestee: null,
            point: point,
            rotation: rotation);
    }

    public void CloseKeyboard()
    {
        KeyboardInputRouter.ClearFocused(this);
        InputInterface.HideKeyboard(null);
        ReleaseModifiers();
    }

    private void EnsureKeyboardTarget()
    {
        if (_keyboardTargetText != null && !_keyboardTargetText.IsDestroyed)
            return;

        _keyboardTargetText = Slot.AttachComponent<Text>();
        _keyboardTargetText.Content.Value = "";
        _keyboardTargetText.CaretPosition.Value = 0;
        _keyboardTargetText.SelectionStart.Value = -1;
        _keyboardTargetText.CaretColor.Value = colorX.Clear;
        _keyboardTargetText.SelectionColor.Value = colorX.Clear;
    }

    public void SendKey(Key key)
    {
        if (KeyMapper.TryGetVirtualKey(key, out ushort vk, out bool shift))
        {
            if (KeyMapper.IsModifier(key))
            {
                WindowInput.SendVirtualKeyDown(vk);
            }
            else
            {
                if (shift)
                    WindowInput.SendVirtualKeyDown(0xA0);
                WindowInput.SendVirtualKey(vk);
                WindowInput.ReleaseAllModifiers();
            }
        }
    }

    public void TypeString(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            WindowInput.SendString(text);
            WindowInput.ReleaseAllModifiers();
        }
    }

    public void ReleaseModifiers() => WindowInput.ReleaseAllModifiers();
}

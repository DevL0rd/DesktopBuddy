using FrooxEngine;
using Key = Renderite.Shared.Key;

namespace DesktopBuddy;

public class DesktopKeyboardSource : Component
{
    public void SendKey(Key key)
    {
        if (KeyMapper.KeyToVK.TryGetValue(key, out ushort vk))
        {
            if (KeyMapper.IsModifier(key))
                WindowInput.SendVirtualKeyDown(vk);
            else
            {
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

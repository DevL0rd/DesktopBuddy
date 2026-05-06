using System;
using System.Reflection;
using Renderite.Shared;

namespace DesktopBuddy;

internal static class TextureProviderSettings
{
    private static readonly string[] WrapMemberNames =
    {
        "WrapMode",
        "WrapModeU",
        "WrapModeV",
        "WrapModeW",
        "AddressMode",
        "AddressModeU",
        "AddressModeV",
        "AddressModeW",
    };

    internal static T ClampWrap<T>(T provider) where T : class
    {
        if (provider == null) return null;

        foreach (var name in WrapMemberNames)
            TrySetMember(provider, name);

        return provider;
    }

    private static void TrySetMember(object provider, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = provider.GetType();
        var field = type.GetField(name, flags);
        if (field != null)
        {
            if (field.FieldType.IsEnum)
                field.SetValue(provider, GetClampValue(field.FieldType));
            else
                SetValueOrSyncValue(field.GetValue(provider), null);
            return;
        }

        var prop = type.GetProperty(name, flags);
        if (prop == null) return;

        if (prop.CanWrite && prop.PropertyType.IsEnum)
            prop.SetValue(provider, GetClampValue(prop.PropertyType));
        else
            SetValueOrSyncValue(prop.GetValue(provider), prop);
    }

    private static void SetValueOrSyncValue(object target, PropertyInfo sourceProperty)
    {
        if (target == null) return;

        var targetType = target.GetType();
        if (targetType.IsEnum)
            return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var valueProp = targetType.GetProperty("Value", flags);
        if (valueProp == null || !valueProp.CanWrite) return;

        var valueType = valueProp.PropertyType;
        if (!valueType.IsEnum) return;

        try
        {
            valueProp.SetValue(target, GetClampValue(valueType));
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Texture] Could not clamp {sourceProperty?.Name ?? targetType.Name}: {ex.Message}");
        }
    }

    private static object GetClampValue(Type enumType)
    {
        if (Enum.IsDefined(enumType, "Clamp"))
            return Enum.Parse(enumType, "Clamp");

        return Enum.ToObject(enumType, (int)TextureWrapMode.Clamp);
    }
}

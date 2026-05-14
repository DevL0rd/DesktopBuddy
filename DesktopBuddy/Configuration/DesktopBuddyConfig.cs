using System;
using BepInEx.Configuration;

namespace DesktopBuddy;

internal sealed class DesktopBuddyConfig
{
    private const string Section = "DesktopBuddy";
    private readonly ConfigFile _file;

    internal DesktopBuddyConfig(ConfigFile file)
    {
        _file = file;
    }

    internal void Bind<T>(DesktopBuddyConfigKey<T> key)
    {
        key.Entry ??= _file.Bind(Section, key.Name, key.DefaultValue, key.Description);
    }

    internal T GetValue<T>(DesktopBuddyConfigKey<T> key)
    {
        Bind(key);
        return key.Entry.Value;
    }

    internal void Set<T>(DesktopBuddyConfigKey<T> key, T value)
    {
        Bind(key);
        key.Entry.Value = value;
    }

    internal void Save()
    {
        _file.Save();
    }
}

internal sealed class DesktopBuddyConfigKey<T>
{
    private readonly Func<T> _defaultValue;

    internal string Name { get; }
    internal string Description { get; }
    internal ConfigEntry<T> Entry { get; set; }
    internal T DefaultValue => _defaultValue();

    internal DesktopBuddyConfigKey(string name, string description, Func<T> defaultValue)
    {
        Name = name;
        Description = description;
        _defaultValue = defaultValue;
    }
}

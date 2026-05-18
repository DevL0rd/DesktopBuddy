using System;
using System.IO;

namespace DesktopBuddy;

internal static class DesktopBuddyRuntimePaths
{
    internal const string DirectoryName = "DesktopBuddyRuntime";
    private const string LegacyDirectoryName = "DesktopBuddyNative";

    internal static string GetDirectory()
    {
        string pluginDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        string current = Path.Combine(pluginDir, DirectoryName);
        if (Directory.Exists(current))
            return current;

        string legacy = Path.Combine(pluginDir, LegacyDirectoryName);
        if (Directory.Exists(legacy))
        {
            Log.Msg($"[Runtime] Using legacy runtime path: {legacy}");
            return legacy;
        }

        return current;
    }

    internal static string FindFile(string fileName)
    {
        string current = Path.Combine(GetPluginDirectory(), DirectoryName, fileName);
        if (File.Exists(current))
            return Path.GetFullPath(current);

        string legacy = Path.Combine(GetPluginDirectory(), LegacyDirectoryName, fileName);
        if (File.Exists(legacy))
        {
            Log.Msg($"[Runtime] Using legacy runtime file: {legacy}");
            return Path.GetFullPath(legacy);
        }

        return Path.GetFullPath(current);
    }

    private static string GetPluginDirectory()
    {
        return Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
    }
}

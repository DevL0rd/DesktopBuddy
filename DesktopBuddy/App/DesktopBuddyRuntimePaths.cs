using System;
using System.IO;

namespace DesktopBuddy;

internal static class DesktopBuddyRuntimePaths
{
    internal const string DirectoryName = "DesktopBuddyRuntime";
    private const string RuntimePackageDirectoryName = "DevL0rd-DesktopBuddyRuntime";
    private const string PluginDirectoryName = "DesktopBuddy";

    internal static string GetDirectory()
    {
        string pluginsRoot = FindBepInExPluginsRoot();
        if (string.IsNullOrWhiteSpace(pluginsRoot))
            throw new DirectoryNotFoundException("DesktopBuddy requires the Gale-style BepInEx\\plugins package layout.");

        return Path.GetFullPath(Path.Combine(pluginsRoot, RuntimePackageDirectoryName, PluginDirectoryName, DirectoryName));
    }

    internal static string FindFile(string fileName)
    {
        return Path.GetFullPath(Path.Combine(GetDirectory(), fileName));
    }

    private static string FindBepInExPluginsRoot()
    {
        string pluginDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        var dir = new DirectoryInfo(pluginDir);

        while (dir != null)
        {
            if (dir.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase) &&
                dir.Parent?.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) == true)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

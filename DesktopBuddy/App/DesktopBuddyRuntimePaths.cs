using System;
using System.IO;

namespace DesktopBuddy;

internal static class DesktopBuddyRuntimePaths
{
    internal const string DirectoryName = "DesktopBuddyRuntime";

    internal static string GetDirectory()
    {
        string pluginDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        return Path.Combine(pluginDir, DirectoryName);
    }

    internal static string FindFile(string fileName)
    {
        return Path.GetFullPath(Path.Combine(GetDirectory(), fileName));
    }
}

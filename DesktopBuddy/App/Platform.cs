using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal static class DesktopBuddyPlatform
{
    private static bool? _isLinux;

    internal static bool IsLinux
    {
        get
        {
            _isLinux ??= RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            return _isLinux.Value;
        }
    }

    internal static string RuntimeLabel =>
        IsLinux ? "Linux" : RuntimeInformation.OSDescription;
}

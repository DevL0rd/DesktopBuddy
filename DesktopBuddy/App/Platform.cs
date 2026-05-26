using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal static class DesktopBuddyPlatform
{
    private static bool? _isWine;
    private static bool? _isLinuxProton;

    internal static bool IsWine
    {
        get
        {
            if (_isWine.HasValue) return _isWine.Value;
            _isWine = TryGetWineVersion() != null;
            return _isWine.Value;
        }
    }

    internal static bool IsLinuxProton
    {
        get
        {
            if (_isLinuxProton.HasValue) return _isLinuxProton.Value;

            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool hasSteamCompat =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamGameId")) ||
                RuntimeInformation.OSDescription.Contains("Steam Runtime", StringComparison.OrdinalIgnoreCase);

            _isLinuxProton = isLinux && (IsWine || hasSteamCompat);
            return _isLinuxProton.Value;
        }
    }

    internal static string RuntimeLabel
    {
        get
        {
            if (IsLinuxProton)
            {
                var wineVersion = TryGetWineVersion();
                return string.IsNullOrWhiteSpace(wineVersion) ? "Linux Proton" : $"Linux Proton/Wine {wineVersion}";
            }

            return RuntimeInformation.OSDescription;
        }
    }

    private static string TryGetWineVersion()
    {
        try
        {
            IntPtr ptr = wine_get_version();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ntdll.dll", EntryPoint = "wine_get_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wine_get_version();
}

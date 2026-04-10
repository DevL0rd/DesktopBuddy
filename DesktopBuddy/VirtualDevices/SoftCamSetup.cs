using Microsoft.Win32;

namespace DesktopBuddy;

internal static class SoftCamSetup
{
    private const string FilterClsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";

    internal static bool IsRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{FilterClsid}");
            return key != null;
        }
        catch { return false; }
    }
}

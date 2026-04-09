using System;
using System.Diagnostics;
using System.IO;
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

    internal static bool Register()
    {
        var modDir = Path.GetDirectoryName(typeof(SoftCamSetup).Assembly.Location) ?? "";
        string[] searchDirs =
        {
            Path.Combine(modDir, "..", "softcam"),
            Path.Combine(modDir, "softcam"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "softcam"),
        };

        string softcamDir = null;
        foreach (var d in searchDirs)
        {
            if (File.Exists(Path.Combine(d, "softcam64.dll")))
            {
                softcamDir = Path.GetFullPath(d);
                break;
            }
        }
        if (softcamDir == null)
        {
            Log.Msg("[SoftCam] DLLs not found, cannot register");
            return false;
        }

        bool ok = true;
        string dll64 = Path.Combine(softcamDir, "softcam64.dll");
        string dll32 = Path.Combine(softcamDir, "softcam.dll");

        if (File.Exists(dll64))
            ok &= RunRegsvr32(dll64);
        if (File.Exists(dll32))
            ok &= RunRegsvr32(dll32);

        return ok;
    }

    private static bool RunRegsvr32(string dllPath)
    {
        try
        {
            Log.Msg($"[SoftCam] Registering {Path.GetFileName(dllPath)}...");
            var psi = new ProcessStartInfo
            {
                FileName = "regsvr32",
                Arguments = $"/s \"{dllPath}\"",
                UseShellExecute = true,
                Verb = "runas"
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            bool success = proc?.ExitCode == 0;
            Log.Msg($"[SoftCam] regsvr32 {Path.GetFileName(dllPath)}: {(success ? "OK" : $"failed (exit {proc?.ExitCode})")}");
            return success;
        }
        catch (Exception ex)
        {
            Log.Msg($"[SoftCam] regsvr32 error: {ex.Message}");
            return false;
        }
    }
}

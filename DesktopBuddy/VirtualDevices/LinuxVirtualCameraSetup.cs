using System;
using System.Diagnostics;
using System.IO;

namespace DesktopBuddy;

internal static class LinuxVirtualCameraSetup
{
    internal const string CameraLabel = "DesktopBuddy - Camera";

    private const string SetupScript = @"#!/bin/sh
set -e
LABEL='DesktopBuddy - Camera'
if ! modinfo v4l2loopback >/dev/null 2>&1; then
  echo 'The v4l2loopback kernel module is not installed.' >&2
  echo 'Install it with your package manager, then run this setup again:' >&2
  echo '  Arch / CachyOS : sudo pacman -S v4l2loopback-dkms' >&2
  echo '  Debian / Ubuntu: sudo apt install v4l2loopback-dkms' >&2
  echo '  Fedora         : sudo dnf install v4l2loopback' >&2
  exit 1
fi
printf 'options v4l2loopback exclusive_caps=1 card_label=\""%s\""\n' ""$LABEL"" > /etc/modprobe.d/desktopbuddy-camera.conf
printf 'v4l2loopback\n' > /etc/modules-load.d/desktopbuddy-camera.conf
modprobe -r v4l2loopback 2>/dev/null || true
modprobe v4l2loopback exclusive_caps=1 card_label=""$LABEL""
echo 'DesktopBuddy virtual camera configured'
";

    private static int _running;

    internal static void Run()
    {
        if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1)
        {
            Log.Msg("[VirtualCamera] Setup already running");
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try { RunSetup(); }
            catch (Exception ex) { Log.Msg($"[VirtualCamera] Setup error: {ex.Message}"); }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        });
    }

    private static void RunSetup()
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "desktopbuddy-vcam-setup.sh");
        File.WriteAllText(scriptPath, SetupScript.Replace("\r\n", "\n"));

        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(scriptPath);

        Log.Msg("[VirtualCamera] Requesting admin rights to load and configure v4l2loopback (DesktopBuddy - Camera)");
        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Msg("[VirtualCamera] Setup failed: pkexec did not start (is polkit installed?)");
                return;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Log.Msg("[VirtualCamera] Virtual camera setup complete. Re-enable the camera or restart Resonite.");
                if (!string.IsNullOrWhiteSpace(stdout)) Log.Msg($"[VirtualCamera] {stdout.Trim()}");
            }
            else
            {
                Log.Msg($"[VirtualCamera] Setup failed exit={process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim())}");
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] Setup failed to launch pkexec: {ex.Message}");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}

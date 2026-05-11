using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private static SafeFileHandle _tunnelJob;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    private static string FindCloudflared()
    {
        var modDir = System.IO.Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        string[] candidates = {
            System.IO.Path.Combine(modDir, "..", "DesktopBuddyNative", "cloudflared.exe"),
            System.IO.Path.Combine(modDir, "DesktopBuddyNative", "cloudflared.exe"),
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopBuddyNative", "cloudflared.exe"),
            "cloudflared"
        };
        foreach (var c in candidates)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "version",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                });
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) { Msg($"[Tunnel] Found cloudflared: {c}"); return c; }
            }
            catch (Exception ex) { Msg($"[Tunnel] cloudflared probe failed for {c}: {ex.Message}"); }
        }
        return null;
    }

    private static void KillTunnel()
    {
        try { if (_tunnelProcess != null && !_tunnelProcess.HasExited) _tunnelProcess.Kill(); }
        catch (Exception ex) { Msg($"[Tunnel] Kill failed: {ex.Message}"); }
        _tunnelProcess = null;
        try { _tunnelJob?.Dispose(); }
        catch (Exception ex) { Msg($"[Tunnel] Job dispose failed: {ex.Message}"); }
        _tunnelJob = null;
    }

    private static void OnTunnelError(string data)
    {
    }

    private static void UpdateSessionTunnelUrls()
    {
        if (TunnelUrl == null) return;
        foreach (var session in ActiveSessions)
        {
            if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed && session.StreamId > 0)
            {
                var newUrl = new Uri($"{TunnelUrl}/stream/{session.StreamId}");
                var vtp = session.VideoTexture;
                vtp.World.RunInUpdates(0, () =>
                {
                    try
                    {
                        if (vtp != null && !vtp.IsDestroyed)
                        {
                            Msg($"[Tunnel] Updating session VTP: {vtp.URL.Value} -> {newUrl}");
                            vtp.URL.Value = newUrl;
                        }
                    }
                    catch (Exception ex)
                    {
                        Msg($"[Tunnel] VTP update callback error: {ex}");
                    }
                });
            }
        }
    }

    private static void RestartTunnel()
    {
        if (_tunnelRestarting) return;
        _tunnelRestarting = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Msg("[Tunnel] === RESTART ===");
                KillTunnel();
                TunnelUrl = null;
                Thread.Sleep(2000);
                StartTunnel();
            }
            catch (Exception ex)
            {
                Msg($"[Tunnel] Restart task error: {ex}");
            }
            finally { _tunnelRestarting = false; }
        });
    }

    private static void StartTunnel()
    {
        try
        {
            if (_cfPath == null)
            {
                _cfPath = FindCloudflared();
                if (_cfPath == null)
                {
                    Msg("[Tunnel] cloudflared not found - tunnel unavailable");
                    return;
                }
            }
            Msg($"[Tunnel] Starting cloudflared tunnel: {_cfPath}");
            var psi = new ProcessStartInfo
            {
                FileName = _cfPath,
                Arguments = $"tunnel --config NUL" +
                    $" --url http://localhost:{STREAM_PORT}" +
                    $" --proxy-keepalive-timeout 5m" +
                    $" --proxy-keepalive-connections 100" +
                    $" --proxy-tcp-keepalive 15s" +
                    $" --proxy-connect-timeout 30s" +
                    $" --compression-quality 0" +
                    $" --grace-period 30s" +
                    $" --no-autoupdate" +
                    $" --edge-ip-version 4",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _tunnelProcess = Process.Start(psi);
            if (_tunnelProcess == null) { Msg("[Tunnel] Failed to start cloudflared"); return; }
            var proc = _tunnelProcess;
            AttachTunnelToKillJob(proc);
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                Msg($"[Tunnel] cloudflared exited (code={proc.ExitCode}), restarting");
                try { _tunnelJob?.Dispose(); } catch { }
                _tunnelJob = null;
                RestartTunnel();
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel/stderr] {e.Data}");
                if (e.Data.Contains("https://") && e.Data.Contains(".trycloudflare.com"))
                {
                    int idx = e.Data.IndexOf("https://");
                    string url = e.Data.Substring(idx).Trim();
                    int space = url.IndexOf(' ');
                    if (space > 0) url = url.Substring(0, space);
                    try { url = new Uri(url).GetLeftPart(UriPartial.Authority); } catch (Exception ex) { Msg($"[Tunnel] URL parse error: {ex.Message}"); }
                    string oldUrl = TunnelUrl;
                    TunnelUrl = url;
                    Msg($"[Tunnel] PUBLIC URL: {TunnelUrl}");
                    if (oldUrl != url)
                        UpdateSessionTunnelUrls();
                }
                OnTunnelError(e.Data);
            };
            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel/stdout] {e.Data}");
            };
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            Msg($"[Tunnel] Error: {ex.Message}");
        }
    }

    private static void AttachTunnelToKillJob(Process proc)
    {
        try
        {
            _tunnelJob?.Dispose();
            _tunnelJob = CreateJobObject(IntPtr.Zero, null);
            if (_tunnelJob == null || _tunnelJob.IsInvalid)
            {
                Msg($"[Tunnel] CreateJobObject failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_tunnelJob, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    Msg($"[Tunnel] SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(_tunnelJob, proc.Handle))
            {
                Msg($"[Tunnel] AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            Msg($"[Tunnel] cloudflared attached to kill-on-close job pid={proc.Id}");
        }
        catch (Exception ex)
        {
            Msg($"[Tunnel] Job attach failed: {ex.Message}");
        }
    }
}

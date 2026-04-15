using System;
using System.Diagnostics;
using System.Threading;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static string FindCloudflared()
    {
        var modDir = System.IO.Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        string[] candidates = {
            System.IO.Path.Combine(modDir, "..", "rml_libs", "cloudflared.exe"),
            System.IO.Path.Combine(modDir, "rml_libs", "cloudflared.exe"),
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rml_libs", "cloudflared.exe"),
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
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg($"[Tunnel] Updating session VTP: {vtp.URL.Value} -> {newUrl}");
                        vtp.URL.Value = newUrl;
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
                    Msg("[Tunnel] cloudflared not found — tunnel unavailable");
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
                    $" --no-chunked-encoding" +
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
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                Msg($"[Tunnel] cloudflared exited (code={proc.ExitCode}), restarting");
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
}

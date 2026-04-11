using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using DesktopBuddy.Shared;

namespace DesktopBuddyRenderer
{
    /// <summary>
    /// Manages a cloudflared quick-tunnel process for the renderer's MjpegServer.
    /// Writes the public tunnel URL to a shared MMF so the game process can read it.
    /// Auto-restarts on crash/exit with exponential backoff (capped at 30s).
    /// </summary>
    public sealed class CloudflareTunnel : IDisposable
    {
        private static ManualLogSource Log => DesktopBuddyRendererPlugin.Log;

        private readonly int _port;
        private readonly string _queuePrefix;

        private Process _process;
        private string _cfPath;
        private volatile string _tunnelUrl;
        private volatile bool _disposed;
        private volatile bool _restarting;
        private int _consecutiveFailures;

        private MemoryMappedFile _tunnelMmf;
        private MemoryMappedViewAccessor _tunnelAccessor;

        private const int MaxRestartDelay = 30_000;  // 30s cap
        private const int BaseRestartDelay = 2_000;   // 2s initial
        private const int ProbeTimeout = 5_000;       // 5s for version probe

        public string TunnelUrl => _tunnelUrl;

        public CloudflareTunnel(string queuePrefix, int port)
        {
            _queuePrefix = queuePrefix;
            _port = port;
        }

        public void Start()
        {
            _cfPath = FindCloudflared();
            if (_cfPath == null)
            {
                Log.LogWarning("[Tunnel] cloudflared not found — tunnel unavailable");
                return;
            }

            OpenTunnelMmf();
            StartProcess();
        }

        private void OpenTunnelMmf()
        {
            var name = CaptureSessionProtocol.GetTunnelMmfName(_queuePrefix + "Primary");
            _tunnelMmf = MemoryMappedFile.CreateOrOpen(name, CaptureSessionProtocol.TunnelMmfSize);
            _tunnelAccessor = _tunnelMmf.CreateViewAccessor(0, CaptureSessionProtocol.TunnelMmfSize, MemoryMappedFileAccess.ReadWrite);
            // Clear it
            _tunnelAccessor.Write(0, 0);
            Log.LogInfo($"[Tunnel] Opened tunnel MMF: {name}");
        }

        private void WriteTunnelUrl(string url)
        {
            if (_tunnelAccessor == null) return;
            if (url == null)
            {
                _tunnelAccessor.Write(0, 0);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(url);
            int len = Math.Min(bytes.Length, CaptureSessionProtocol.TunnelMmfSize - 4);
            _tunnelAccessor.Write(0, len);
            _tunnelAccessor.WriteArray(4, bytes, 0, len);
        }

        private void StartProcess()
        {
            if (_disposed) return;

            try
            {
                Log.LogInfo($"[Tunnel] Starting cloudflared: {_cfPath} → localhost:{_port}");
                var psi = new ProcessStartInfo
                {
                    FileName = _cfPath,
                    Arguments = "tunnel --config NUL"
                        + $" --url http://localhost:{_port}"
                        + " --proxy-keepalive-timeout 2m"
                        + " --proxy-keepalive-connections 50"
                        + " --proxy-tcp-keepalive 15s"
                        + " --proxy-connect-timeout 10s"
                        + " --no-chunked-encoding"
                        + " --compression-quality 0"
                        + " --grace-period 15s"
                        + " --no-autoupdate"
                        + " --edge-ip-version 4",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                _process = Process.Start(psi);
                if (_process == null)
                {
                    Log.LogError("[Tunnel] Failed to start cloudflared process");
                    ScheduleRestart();
                    return;
                }

                _process.EnableRaisingEvents = true;
                var proc = _process;

                proc.Exited += (s, e) =>
                {
                    if (_disposed) return;
                    int code = -1;
                    try { code = proc.ExitCode; } catch { }
                    Log.LogWarning($"[Tunnel] cloudflared exited (code={code}), scheduling restart");
                    _tunnelUrl = null;
                    WriteTunnelUrl(null);
                    ScheduleRestart();
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    Log.LogInfo($"[Tunnel/stderr] {e.Data}");
                    TryParseUrl(e.Data);
                };

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    Log.LogInfo($"[Tunnel/stdout] {e.Data}");
                };

                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                Log.LogError($"[Tunnel] Start error: {ex.Message}");
                ScheduleRestart();
            }
        }

        private void TryParseUrl(string line)
        {
            if (!line.Contains("https://") || !line.Contains(".trycloudflare.com"))
                return;

            int idx = line.IndexOf("https://");
            string url = line.Substring(idx).Trim();
            int space = url.IndexOf(' ');
            if (space > 0) url = url.Substring(0, space);

            try
            {
                url = new Uri(url).GetLeftPart(UriPartial.Authority);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Tunnel] URL parse error: {ex.Message}");
                return;
            }

            string oldUrl = _tunnelUrl;
            _tunnelUrl = url;
            _consecutiveFailures = 0; // Successfully connected
            WriteTunnelUrl(url);

            if (oldUrl != url)
                Log.LogInfo($"[Tunnel] PUBLIC URL: {url}");
        }

        private void ScheduleRestart()
        {
            if (_disposed || _restarting) return;
            _restarting = true;

            _consecutiveFailures++;
            int delay = Math.Min(BaseRestartDelay * _consecutiveFailures, MaxRestartDelay);
            Log.LogInfo($"[Tunnel] Restart in {delay}ms (attempt #{_consecutiveFailures})");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(delay);
                _restarting = false;
                if (!_disposed)
                {
                    KillProcess();
                    StartProcess();
                }
            });
        }

        private void KillProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Tunnel] Kill failed: {ex.Message}");
            }
            _process = null;
        }

        private static string FindCloudflared()
        {
            // Renderer DLL is in Renderer/BepInEx/plugins
            // cloudflared is at Resonite/cloudflared/cloudflared.exe
            var pluginDir = Path.GetDirectoryName(typeof(CloudflareTunnel).Assembly.Location) ?? "";
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "cloudflared", "cloudflared.exe")),
                Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "cloudflared", "cloudflared.exe")),
                Path.GetFullPath(Path.Combine(pluginDir, "..", "cloudflared", "cloudflared.exe")),
                "cloudflared"
            };

            foreach (var c in candidates)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = c,
                        Arguments = "version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (p != null && p.WaitForExit(ProbeTimeout) && p.ExitCode == 0)
                    {
                        Log.LogInfo($"[Tunnel] Found cloudflared: {c}");
                        return c;
                    }
                    if (p != null && !p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _tunnelUrl = null;
            WriteTunnelUrl(null);

            KillProcess();

            _tunnelAccessor?.Dispose();
            _tunnelMmf?.Dispose();
            _tunnelAccessor = null;
            _tunnelMmf = null;

            Log.LogInfo("[Tunnel] Disposed");
        }
    }
}

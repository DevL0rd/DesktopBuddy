using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class PinggyTunnelManager : IDisposable
{
    private static readonly Regex UrlEndpointRegex = new(
        @"(?:tcp|rtsp)://(?<host>[A-Za-z0-9.-]+):(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PinggyHostPortRegex = new(
        @"(?<host>[A-Za-z0-9.-]*pinggy[A-Za-z0-9.-]*):(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly int _localPort;
    private readonly PinggyTunnelOptions _options;
    private readonly Action<string, int> _endpointAvailable;
    private readonly object _lock = new();
    private Process _process;
    private bool _disposed;
    private bool _endpointSeen;
    private int _restartScheduled;
    private int _restartAttempts;

    public PinggyTunnelManager(int localPort, PinggyTunnelOptions options, Action<string, int> endpointAvailable)
    {
        _localPort = localPort;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _endpointAvailable = endpointAvailable ?? throw new ArgumentNullException(nameof(endpointAvailable));
    }

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PinggyTunnelManager));

        StartProcess();
    }

    private void StartProcess()
    {
        string executable = string.IsNullOrWhiteSpace(_options.SshPath) ? "ssh" : _options.SshPath.Trim();
        string arguments = BuildSshArguments();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleOutput(e.Data, false);
        process.ErrorDataReceived += (_, e) => HandleOutput(e.Data, true);
        process.Exited += (_, _) =>
        {
            if (_disposed) return;
            string code = SafeExitCode(process);
            Log.Msg($"[Pinggy] Tunnel process exited code={code} endpointSeen={_endpointSeen}; scheduling reconnect");
            ScheduleReconnect(process);
        };

        lock (_lock)
        {
            if (_disposed)
            {
                process.Dispose();
                return;
            }

            _process = process;
        }

        Log.Msg($"[Pinggy] Starting TCP tunnel: local=127.0.0.1:{_localPort} server={_options.Server} remote={DescribeRemote()} ssh={executable}");
        if (!process.Start())
            throw new InvalidOperationException("Failed to start ssh process for Pinggy tunnel");

        Interlocked.Exchange(ref _restartScheduled, 0);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public void Dispose()
    {
        _disposed = true;
        Process process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                Log.Msg("[Pinggy] Tunnel process stopped");
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[Pinggy] Failed to stop tunnel process: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ScheduleReconnect(Process exitedProcess)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_process, exitedProcess))
                _process = null;
        }

        try { exitedProcess.Dispose(); } catch { }

        if (_disposed)
            return;
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            int attempt = Interlocked.Increment(ref _restartAttempts);
            int delayMs = Math.Min(30_000, 2_000 + Math.Max(0, attempt - 1) * 2_000);
            Log.Msg($"[Pinggy] Reconnecting tunnel in {delayMs}ms (attempt {attempt})");
            Thread.Sleep(delayMs);

            if (_disposed)
                return;

            try
            {
                StartProcess();
            }
            catch (Exception ex)
            {
                Log.Msg($"[Pinggy] Tunnel reconnect failed: {ex.Message}");
                Interlocked.Exchange(ref _restartScheduled, 0);
                ScheduleReconnect(null);
            }
        });
    }

    private string BuildSshArguments()
    {
        var args = new StringBuilder();
        AppendArg(args, "-p");
        AppendArg(args, _options.SshPort.ToString());
        AppendArg(args, "-o");
        AppendArg(args, "ExitOnForwardFailure=yes");
        AppendArg(args, "-o");
        AppendArg(args, "ServerAliveInterval=30");
        AppendArg(args, "-o");
        AppendArg(args, "ServerAliveCountMax=2");
        AppendArg(args, "-o");
        AppendArg(args, "StrictHostKeyChecking=accept-new");
        AppendArg(args, "-R");
        AppendArg(args, BuildRemoteForward());
        AppendArg(args, BuildLogin());
        return args.ToString().Trim();
    }

    private string BuildRemoteForward()
    {
        string listenAddress = _options.ListenAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(listenAddress))
            return $"{listenAddress}:1:127.0.0.1:{_localPort}";

        int remotePort = Math.Clamp(_options.RemotePort, 0, 65535);
        return $"{remotePort}:127.0.0.1:{_localPort}";
    }

    private string BuildLogin()
    {
        string server = string.IsNullOrWhiteSpace(_options.Server) ? "a.pinggy.io" : _options.Server.Trim();
        string user = BuildUser();
        return $"{user}@{server}";
    }

    private string BuildUser()
    {
        string mode = string.IsNullOrWhiteSpace(_options.Mode) ? "tcp" : _options.Mode.Trim();
        string token = _options.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return mode;

        if (_options.ForceExisting)
            return $"{token}+force+{mode}";

        return $"{token}+{mode}";
    }

    private string DescribeRemote()
    {
        string listenAddress = _options.ListenAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(listenAddress))
            return listenAddress;

        return _options.RemotePort > 0 ? _options.RemotePort.ToString() : "auto";
    }

    private void HandleOutput(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string prefix = isError ? "[Pinggy:err]" : "[Pinggy]";
        Log.Msg($"{prefix} {line}");

        if (TryExtractEndpoint(line, out string host, out int port))
        {
            _endpointSeen = true;
            Interlocked.Exchange(ref _restartAttempts, 0);
            _endpointAvailable(host, port);
        }
    }

    private static bool TryExtractEndpoint(string line, out string host, out int port)
    {
        host = null;
        port = 0;

        Match match = UrlEndpointRegex.Match(line);
        if (!match.Success)
            match = PinggyHostPortRegex.Match(line);

        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["port"].Value, out port) || port <= 0 || port > 65535)
            return false;

        host = match.Groups["host"].Value;
        return !string.IsNullOrWhiteSpace(host);
    }

    private static void AppendArg(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
            builder.Append(' ');

        builder.Append(Quote(value));
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch { return "unknown"; }
    }
}

public sealed class PinggyTunnelOptions
{
    public string SshPath { get; init; } = "ssh";
    public int SshPort { get; init; } = 443;
    public string Server { get; init; } = "a.pinggy.io";
    public string Token { get; init; } = "";
    public string Mode { get; init; } = "tcp";
    public bool ForceExisting { get; init; } = true;
    public int RemotePort { get; init; }
    public string ListenAddress { get; init; } = "";
}

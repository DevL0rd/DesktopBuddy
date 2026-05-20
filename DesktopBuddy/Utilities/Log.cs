using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace DesktopBuddy;

internal static class Log
{
    internal static readonly string FilePath;

    private static readonly BlockingCollection<LogEntry> _queue = new(4096);
    private static readonly Thread _writerThread;
    private static readonly object _fileWriteLock = new();
    private static ManualLogSource _logger;

    private struct LogEntry
    {
        public string Timestamp;
        public string Message;
        public bool IsError;
    }

    static Log()
    {
        var resoniteDir = ResolveResoniteRoot();
        var logsDir = Path.Combine(resoniteDir, "Logs");
        if (!Directory.Exists(logsDir))
            logsDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        var machineName = Environment.MachineName;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        FilePath = Path.Combine(logsDir, $"DesktopBuddy_{machineName}_{timestamp}.log");

        _writerThread = new Thread(WriterLoop) { Name = "DesktopBuddy:Log", IsBackground = true };
        _writerThread.Start();
    }

    internal static void Msg(string msg)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _queue.TryAdd(new LogEntry { Timestamp = ts, Message = msg, IsError = false });
    }

    internal static void Error(string msg)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _queue.TryAdd(new LogEntry { Timestamp = ts, Message = msg, IsError = true });
    }

    internal static void MsgImmediate(string msg)
    {
        if (msg != null && msg.StartsWith("[CleanupTrace]", StringComparison.Ordinal))
            return;

        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        try { _logger?.LogInfo(msg); } catch { }
        WriteLine(ts, msg, false);
    }

    internal static void SetLogger(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static void StartSession()
    {
        try
        {
            lock (_fileWriteLock)
                File.WriteAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DesktopBuddy session started\n");
        }
        catch { }
    }

    internal static string[] GetRecentLines(int maxLines = 100)
    {
        maxLines = Math.Clamp(maxLines, 1, 100);
        try
        {
            lock (_fileWriteLock)
            {
                if (!File.Exists(FilePath))
                    return Array.Empty<string>();

                var queue = new Queue<string>(maxLines);
                foreach (string line in File.ReadLines(FilePath))
                {
                    if (queue.Count == maxLines)
                        queue.Dequeue();
                    queue.Enqueue(line);
                }
                return queue.ToArray();
            }
        }
        catch (Exception ex)
        {
            return new[] { $"[DesktopBuddy] Failed to read log tail: {ex.Message}" };
        }
    }

    internal static string ExportCombinedLog()
    {
        string logsDir = Path.GetDirectoryName(FilePath) ?? Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";

        string exportPath = Path.Combine(logsDir, $"DesktopBuddy_Combined_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        var builder = new StringBuilder();

        AppendLogSection(builder, "DesktopBuddy", FilePath);
        AppendLogSection(builder, "Renderer BepInEx", ResolveRendererBepInExLogPath());

        File.WriteAllText(exportPath, builder.ToString());
        Msg($"[Log] Exported combined log: {exportPath}");
        return exportPath;
    }

    private static string ResolveResoniteRoot()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        var dir = new DirectoryInfo(assemblyDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Resonite.exe")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return assemblyDir;
    }

    private static string ResolveRendererBepInExLogPath()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        string profileRoot = ResolveBepInExProfileRoot(assemblyDir);
        string resoniteRoot = ResolveResoniteRoot();

        foreach (string root in new[] { profileRoot, resoniteRoot, assemblyDir }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(root, "Renderer", "BepInEx", "LogOutput.log");
            if (File.Exists(path))
                return path;
        }

        return !string.IsNullOrWhiteSpace(profileRoot)
            ? Path.Combine(profileRoot, "Renderer", "BepInEx", "LogOutput.log")
            : Path.Combine(resoniteRoot, "Renderer", "BepInEx", "LogOutput.log");
    }

    private static string ResolveBepInExProfileRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (dir.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(dir.FullName, "plugins")))
            {
                return dir.Parent?.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void AppendLogSection(StringBuilder builder, string title, string path)
    {
        builder.AppendLine($"===== {title} =====");
        builder.AppendLine(path);

        try
        {
            if (File.Exists(path))
                builder.AppendLine(File.ReadAllText(path));
            else
                builder.AppendLine("(missing)");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"(failed to read: {ex.Message})");
        }

        builder.AppendLine();
    }

    private static void WriterLoop()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (entry.IsError)
                    _logger?.LogError(entry.Message);
                else
                    _logger?.LogInfo(entry.Message);
            }
            catch { }

            var prefix = entry.IsError ? "ERROR: " : "";
            WriteLine(entry.Timestamp, entry.Message, entry.IsError);
        }
    }

    private static void WriteLine(string timestamp, string message, bool isError)
    {
        var prefix = isError ? "ERROR: " : "";
        try
        {
            lock (_fileWriteLock)
                File.AppendAllText(FilePath, $"[{timestamp}] {prefix}{message}\n");
        }
        catch { }
    }
}

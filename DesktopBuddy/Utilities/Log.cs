using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace DesktopBuddy;

internal static class Log
{
    private const int RecentLineLimit = 500;
    private static readonly object RecentLinesLock = new();
    private static readonly Queue<string> RecentLines = new(RecentLineLimit);
    private static ManualLogSource _logger;

    internal static string FilePath => ResolveGameBepInExLogPath();

    internal static void Msg(string msg)
    {
        AddRecentLine(msg, false);
        try { _logger?.LogInfo(msg); } catch { }
    }

    internal static void Error(string msg)
    {
        AddRecentLine(msg, true);
        try { _logger?.LogError(msg); } catch { }
    }

    internal static void SetLogger(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static void StartSession()
    {
        Msg("DesktopBuddy session started");
    }

    internal static string[] GetRecentLines(int maxLines = 100)
    {
        maxLines = Math.Clamp(maxLines, 1, 100);
        lock (RecentLinesLock)
            return RecentLines.Skip(Math.Max(0, RecentLines.Count - maxLines)).ToArray();
    }

    internal static string ExportCombinedLog()
    {
        string exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (!Directory.Exists(exportDir))
            exportDir = ResolveBepInExProfileRoot(Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".")
                        ?? Path.GetDirectoryName(typeof(Log).Assembly.Location)
                        ?? ".";

        string exportPath = Path.Combine(exportDir, $"DesktopBuddy_Combined_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        var builder = new StringBuilder();

        AppendLogSection(builder, "Game BepInEx", ResolveGameBepInExLogPath());
        AppendLogSection(builder, "Renderer BepInEx", ResolveRendererBepInExLogPath());

        File.WriteAllText(exportPath, builder.ToString());
        Msg($"[Log] Exported combined BepInEx log: {exportPath}");
        return exportPath;
    }

    internal static void ExportDiagnosticsBundle()
    {
        _ = Task.Run(RunDiagnosticsBundleExport);
        Msg("[Log] Started DesktopBuddy diagnostics bundle export");
    }

    private static void RunDiagnosticsBundleExport()
    {
        string scriptPath = Path.Combine(
            Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".",
            "CollectDesktopBuddyDiagnostics.ps1");

        if (!File.Exists(scriptPath))
        {
            Error($"[Log] Diagnostics export failed: collector was not found at {scriptPath}");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Error("[Log] Diagnostics export failed: PowerShell did not start");
                return;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string exportPath = ExtractDiagnosticsPath(stdout);
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(exportPath))
            {
                Msg($"[Log] Diagnostics bundle exported: {exportPath}");
                OpenContainingFolder(exportPath);
                return;
            }

            if (process.ExitCode == 0)
            {
                Msg("[Log] Diagnostics bundle export finished, but no output path was reported");
                if (!string.IsNullOrWhiteSpace(stdout))
                    Msg($"[Log] Diagnostics output: {stdout.Trim()}");
                return;
            }

            Error($"[Log] Diagnostics export failed exit={process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim())}");
        }
        catch (Exception ex)
        {
            Error($"[Log] Diagnostics export failed: {ex}");
        }
    }

    private static string ExtractDiagnosticsPath(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string trimmed = line.Trim();
            if (trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
                return trimmed;
        }

        return null;
    }

    private static void OpenContainingFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Msg($"[Log] Diagnostics bundle exported, but folder open failed: {ex.Message}");
        }
    }

    private static string ResolveGameBepInExLogPath()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        string profileRoot = ResolveBepInExProfileRoot(assemblyDir);
        string resoniteRoot = ResolveResoniteRoot();

        foreach (string root in new[] { profileRoot, resoniteRoot, assemblyDir }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(root, "BepInEx", "LogOutput.log");
            if (File.Exists(path))
                return path;
        }

        return !string.IsNullOrWhiteSpace(profileRoot)
            ? Path.Combine(profileRoot, "BepInEx", "LogOutput.log")
            : Path.Combine(resoniteRoot, "BepInEx", "LogOutput.log");
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
                builder.AppendLine(ReadAllTextShared(path));
            else
                builder.AppendLine("(missing)");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"(failed to read: {ex.Message})");
        }

        builder.AppendLine();
    }

    private static void AddRecentLine(string message, bool isError)
    {
        string prefix = isError ? "ERROR: " : "";
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {prefix}{message}";
        lock (RecentLinesLock)
        {
            if (RecentLines.Count >= RecentLineLimit)
                RecentLines.Dequeue();
            RecentLines.Enqueue(line);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

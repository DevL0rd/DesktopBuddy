using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DesktopBuddyManager;

internal sealed class SupportReportService
{
    private static readonly string[] EventProviders = [".NET Runtime", "Application Error", "Application Hang", "Windows Error Reporting"];
    private static readonly TimeSpan ArtifactLookback = TimeSpan.FromDays(14);

    internal async Task<string> GenerateReportAsync(string? resonitePath, string description, string managerBuildSha)
    {
        var timestamp = DateTime.Now;
        var reportsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DesktopBuddyReports");
        Directory.CreateDirectory(reportsRoot);

        var slug = timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var reportDir = Path.Combine(reportsRoot, $"DesktopBuddySupportReport_{slug}");
        if (Directory.Exists(reportDir))
            Directory.Delete(reportDir, recursive: true);
        Directory.CreateDirectory(reportDir);

        // ── Build single consolidated text report ─────────────────────────────
        var report = new StringBuilder();
        var sep = new string('=', 80);

        report.AppendLine(sep);
        report.AppendLine("  DESKTOPBUDDY SUPPORT REPORT");
        report.AppendLine($"  Generated: {timestamp:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(sep);
        report.AppendLine();

        // User description
        report.AppendLine(sep);
        report.AppendLine("  USER DESCRIPTION");
        report.AppendLine(sep);
        report.AppendLine(string.IsNullOrWhiteSpace(description) ? "No description provided." : description.Trim());
        report.AppendLine();

        // Environment
        report.AppendLine(sep);
        report.AppendLine("  ENVIRONMENT");
        report.AppendLine(sep);
        report.AppendLine(BuildEnvironmentInfo(resonitePath, managerBuildSha, timestamp));

        // Log file contents (last 5, inlined)
        report.AppendLine(sep);
        report.AppendLine("  DESKTOPBUDDY LOG FILES (last 5)");
        report.AppendLine(sep);
        var logCount = await Task.Run(() => AppendLogContents(report, resonitePath));
        if (logCount == 0)
            report.AppendLine("No DesktopBuddy log files found.");
        report.AppendLine();

        // Renderer BepInEx log
        report.AppendLine(sep);
        report.AppendLine("  RENDERER BEPINEX LOG");
        report.AppendLine(sep);
        await Task.Run(() => AppendSingleLogFile(report, resonitePath, Path.Combine("Renderer", "BepInEx", "LogOutput.log")));
        report.AppendLine();

        // Renderite player log
        report.AppendLine(sep);
        report.AppendLine("  RENDERITE PLAYER LOG (last 500 lines)");
        report.AppendLine(sep);
        await Task.Run(() =>
        {
            var playerLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                "Yellow Dog Man Studios", "Renderite.Renderer", "Player.log");
            if (File.Exists(playerLog))
            {
                try
                {
                    var lines = File.ReadAllLines(playerLog, Encoding.UTF8);
                    var start = Math.Max(0, lines.Length - 500);
                    for (int i = start; i < lines.Length; i++)
                        report.AppendLine(lines[i]);
                }
                catch (Exception ex) { report.AppendLine($"(could not read: {ex.Message})"); }
            }
            else
            {
                report.AppendLine("Player.log not found.");
            }
        });
        report.AppendLine();

        // Windows event log
        report.AppendLine(sep);
        report.AppendLine("  WINDOWS APPLICATION EVENT LOG (last 7 days)");
        report.AppendLine(sep);
        var eventCount = await Task.Run(() => AppendEventLogEntries(report));
        report.AppendLine();

        await File.WriteAllTextAsync(Path.Combine(reportDir, "report.txt"), report.ToString(), Encoding.UTF8);

        // ── Binary / non-text crash artifacts in their own subfolder ──────────
        var crashDir = Path.Combine(reportDir, "crash-artifacts");
        Directory.CreateDirectory(crashDir);
        var copiedArtifacts = CopyCrashArtifacts(crashDir);
        var crashCount = copiedArtifacts.Count;
        if (crashCount == 0)
            Directory.Delete(crashDir);   // remove empty folder from zip
        else
        {
            report.AppendLine(sep);
            report.AppendLine("  CRASH ARTIFACTS INCLUDED");
            report.AppendLine(sep);
            foreach (var artifact in copiedArtifacts.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                report.AppendLine(artifact);
            report.AppendLine();

            await File.WriteAllTextAsync(Path.Combine(reportDir, "report.txt"), report.ToString(), Encoding.UTF8);
        }

        // ── Zip ───────────────────────────────────────────────────────────────
        var zipPath = reportDir + ".zip";
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(reportDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
        Directory.Delete(reportDir, recursive: true);
        return zipPath;
    }

    private static string BuildEnvironmentInfo(string? resonitePath, string managerBuildSha, DateTime timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated: {timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET runtime: {Environment.Version}");
        sb.AppendLine($"Manager build SHA: {managerBuildSha}");
        sb.AppendLine($"Selected Resonite path: {resonitePath ?? "(not set)"}");
        return sb.ToString();
    }

    private static int AppendLogContents(StringBuilder sb, string? resonitePath)
    {
        var sourceDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(resonitePath))
        {
            sourceDirs.Add(Path.Combine(resonitePath, "Logs"));
            sourceDirs.Add(Path.Combine(resonitePath, "rml_mods"));
        }

        var files = sourceDirs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(dir => new DirectoryInfo(dir)
                .EnumerateFiles("DesktopBuddy_*.log", SearchOption.TopDirectoryOnly))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(5)
            .ToList();

        foreach (var file in files)
        {
            sb.AppendLine($"--- {file.Name} ---");
            try
            {
                sb.AppendLine(File.ReadAllText(file.FullName, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(could not read: {ex.Message})");
            }
            sb.AppendLine();
        }

        return files.Count;
    }

    private static void AppendSingleLogFile(StringBuilder sb, string? resonitePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(resonitePath))
        {
            sb.AppendLine("(Resonite path not set)");
            return;
        }
        var path = Path.Combine(resonitePath, relativePath);
        if (!File.Exists(path))
        {
            sb.AppendLine($"{relativePath} not found.");
            return;
        }
        try
        {
            sb.AppendLine($"--- {relativePath} ---");
            sb.AppendLine(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not read: {ex.Message})");
        }
    }

    private static List<string> CopyCrashArtifacts(string destinationDir)
    {
        var copied = new List<string>();

        foreach (var dumpDir in EnumerateCrashDumpDirectories())
        {
            if (!Directory.Exists(dumpDir))
                continue;

            foreach (var file in new DirectoryInfo(dumpDir)
                         .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                         .Where(file => file.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                                        file.Extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase) ||
                                        file.Extension.Equals(".wer", StringComparison.OrdinalIgnoreCase))
                         .Where(IsRecentArtifact)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(20))
            {
                var destinationPath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(destinationPath, overwrite: true);
                copied.Add(file.Name);
            }
        }

        foreach (var werDir in EnumerateWerDirectories())
        {
            if (!Directory.Exists(werDir))
                continue;

            foreach (var directory in new DirectoryInfo(werDir)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                         .Where(IsRecentArtifact)
                         .OrderByDescending(dir => dir.LastWriteTimeUtc)
                         .Take(10))
            {
                var targetDir = Path.Combine(destinationDir, directory.Name);
                CopyDirectory(directory.FullName, targetDir);
                copied.Add(directory.Name + Path.DirectorySeparatorChar);
            }
        }

        return copied;
    }

    private static IEnumerable<string> EnumerateCrashDumpDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "CrashDumps");
    }

    private static IEnumerable<string> EnumerateWerDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue");
        yield return Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        yield return Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue");
    }

    private static bool IsRecentArtifact(FileSystemInfo info)
    {
        return DateTime.UtcNow - info.LastWriteTimeUtc <= ArtifactLookback;
    }

    private static int AppendEventLogEntries(StringBuilder builder)
    {
        var count = 0;
        var sevenDaysMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;
        var providerFilter = string.Join(" or ", EventProviders.Select(provider => $"Provider[@Name='{provider}']"));
        var query = $"*[System[TimeCreated[timediff(@SystemTime) <= {sevenDaysMs}] and ({providerFilter})]]";

        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query))
        {
            BatchSize = 64,
        };

        for (EventRecord? record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
        {
            count++;
            builder.AppendLine($"[{record.TimeCreated:yyyy-MM-dd HH:mm:ss}] [{record.ProviderName}] [{record.LevelDisplayName}] ID:{record.Id}");
            string message;
            try   { message = record.FormatDescription() ?? "(no description available)"; }
            catch { message = "(message unavailable)"; }
            builder.AppendLine(message.Trim());
            builder.AppendLine();
            record.Dispose();
        }

        if (count == 0)
            builder.AppendLine("No relevant Application event log entries found in the last 7 days.");

        return count;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

}

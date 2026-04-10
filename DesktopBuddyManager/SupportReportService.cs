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
    private static readonly string[] CrashKeywords = ["desktopbuddy", "resonite", "dotnet", "coreclr", "clr"];
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

        var summaryLines = new List<string>();

        await WriteTextFileAsync(
            Path.Combine(reportDir, "user-description.txt"),
            string.IsNullOrWhiteSpace(description) ? "No description provided." : description.Trim());

        var environmentInfo = BuildEnvironmentInfo(resonitePath, managerBuildSha, timestamp);
        await WriteTextFileAsync(Path.Combine(reportDir, "environment.txt"), environmentInfo);

        var desktopBuddyLogsDir = Path.Combine(reportDir, "desktopbuddy-logs");
        Directory.CreateDirectory(desktopBuddyLogsDir);
        var copiedLogFiles = CopyDesktopBuddyLogs(resonitePath, desktopBuddyLogsDir);
        summaryLines.Add($"DesktopBuddy logs copied: {copiedLogFiles}");

        var crashDir = Path.Combine(reportDir, "crash-artifacts");
        Directory.CreateDirectory(crashDir);
        var copiedCrashArtifacts = CopyCrashArtifacts(crashDir);
        summaryLines.Add($"Crash artifacts copied: {copiedCrashArtifacts}");

        var eventLogPath = Path.Combine(reportDir, "windows-event-log.txt");
        var eventCount = await Task.Run(() => WriteRelevantEventLogEntries(eventLogPath));
        summaryLines.Add($"Event log entries written: {eventCount}");

        await WriteTextFileAsync(Path.Combine(reportDir, "report-summary.txt"), string.Join(Environment.NewLine, summaryLines));

        var zipPath = reportDir + ".zip";
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(reportDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
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

    private static int CopyDesktopBuddyLogs(string? resonitePath, string destinationDir)
    {
        var sourceDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(resonitePath))
        {
            sourceDirs.Add(Path.Combine(resonitePath, "Logs"));
            sourceDirs.Add(Path.Combine(resonitePath, "rml_mods"));
        }

        var copied = 0;
        foreach (var sourceDir in sourceDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(sourceDir))
                continue;

            var files = new DirectoryInfo(sourceDir)
                .EnumerateFiles("DesktopBuddy_*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(20)
                .ToList();

            foreach (var file in files)
            {
                var destinationPath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(destinationPath, overwrite: true);
                copied++;
            }
        }

        return copied;
    }

    private static int CopyCrashArtifacts(string destinationDir)
    {
        var copied = 0;

        foreach (var dumpDir in EnumerateCrashDumpDirectories())
        {
            if (!Directory.Exists(dumpDir))
                continue;

            foreach (var file in new DirectoryInfo(dumpDir)
                         .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                         .Where(file => file.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                                        file.Extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase) ||
                                        file.Extension.Equals(".wer", StringComparison.OrdinalIgnoreCase))
                         .Where(IsRelevantArtifact)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(20))
            {
                var destinationPath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(destinationPath, overwrite: true);
                copied++;
            }
        }

        foreach (var werDir in EnumerateWerDirectories())
        {
            if (!Directory.Exists(werDir))
                continue;

            foreach (var directory in new DirectoryInfo(werDir)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                         .Where(IsRelevantArtifact)
                         .OrderByDescending(dir => dir.LastWriteTimeUtc)
                         .Take(10))
            {
                var targetDir = Path.Combine(destinationDir, directory.Name);
                CopyDirectory(directory.FullName, targetDir);
                copied++;
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

    private static bool IsRelevantArtifact(FileSystemInfo info)
    {
        if (DateTime.UtcNow - info.LastWriteTimeUtc > ArtifactLookback)
            return false;

        var candidate = info.FullName.ToLowerInvariant();
        return CrashKeywords.Any(candidate.Contains);
    }

    private static int WriteRelevantEventLogEntries(string destinationPath)
    {
        var builder = new StringBuilder();
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
            builder.AppendLine(new string('=', 80));
            builder.AppendLine($"Time: {record.TimeCreated:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Provider: {record.ProviderName}");
            builder.AppendLine($"Level: {record.LevelDisplayName}");
            builder.AppendLine($"Event ID: {record.Id}");
            builder.AppendLine($"Machine: {record.MachineName}");
            builder.AppendLine("Message:");

            string message;
            try
            {
                message = record.FormatDescription() ?? "(no description available)";
            }
            catch
            {
                message = "(message unavailable)";
            }

            builder.AppendLine(message.Trim());
            builder.AppendLine();
            record.Dispose();
        }

        if (count == 0)
            builder.AppendLine("No relevant Application event log entries were found in the last 7 days.");

        File.WriteAllText(destinationPath, builder.ToString(), Encoding.UTF8);
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

    private static Task WriteTextFileAsync(string path, string contents) =>
        File.WriteAllTextAsync(path, contents, Encoding.UTF8);
}

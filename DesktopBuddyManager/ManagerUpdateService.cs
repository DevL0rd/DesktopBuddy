using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopBuddyManager;

internal sealed class ManagerUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/DevL0rd/DesktopBuddy/releases/latest";

    internal static string CurrentBuildSha => NormalizeSha(BuildInfo.GitSha);

    internal async Task<ManagerUpdateResult> CheckForUpdateAsync()
    {
        using var http = CreateHttpClient();
        using var response = await http.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);

        if (!document.RootElement.TryGetProperty("tag_name", out var tagProperty))
            return ManagerUpdateResult.NoUpdate("Latest release tag was not present.");

        var tag = tagProperty.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
            return ManagerUpdateResult.NoUpdate("Latest release tag was empty.");

        var latestSha = NormalizeSha(ExtractReleaseSha(tag));
        var asset = FindManagerAsset(document.RootElement, tag);
        if (asset == null)
            return ManagerUpdateResult.NoUpdate("No manager executable was attached to the latest release.");

        if (latestSha == "unknown")
            return ManagerUpdateResult.NoUpdate($"Latest release {tag} did not expose a recognizable build SHA.");

        if (string.Equals(CurrentBuildSha, latestSha, StringComparison.OrdinalIgnoreCase))
            return ManagerUpdateResult.NoUpdate($"Manager is already on the latest release ({tag}).", latestSha, tag);

        return ManagerUpdateResult.UpdateAvailable(tag, latestSha, asset.Value.Name, asset.Value.DownloadUrl);
    }

    internal async Task<string> DownloadUpdateAsync(ManagerUpdateResult update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!update.HasUpdate)
            throw new InvalidOperationException("No update is available to download.");

        using var http = CreateHttpClient();
        using var response = await http.GetAsync(update.DownloadUrl!, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tempDir = Path.Combine(Path.GetTempPath(), "DesktopBuddyManager", update.Tag!);
        Directory.CreateDirectory(tempDir);

        var finalPath = Path.Combine(tempDir, Path.GetFileName(update.AssetName!));
        var tempPath = finalPath + ".download";

        try
        {
            await using (var downloadStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await downloadStream.CopyToAsync(fileStream);
                await fileStream.FlushAsync();
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);

            File.Move(tempPath, finalPath);
            return finalPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal static string GetInstalledModSha(string? resonitePath)
    {
        if (string.IsNullOrWhiteSpace(resonitePath))
            return "unknown";
        var shaFile = Path.Combine(resonitePath.Trim(), "rml_mods", "DesktopBuddy.sha");
        if (!File.Exists(shaFile))
            return "not installed";
        return NormalizeSha(File.ReadAllText(shaFile).Trim());
    }

    internal static void LaunchManager(string managerPath)
    {
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = managerPath,
            WorkingDirectory = Path.GetDirectoryName(managerPath),
            UseShellExecute = true,
        });

        if (started == null)
            throw new InvalidOperationException("The updated manager could not be started.");
    }

    private static (string Name, string DownloadUrl)? FindManagerAsset(JsonElement root, string tag)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        (string Name, string DownloadUrl)? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = (name, downloadUrl);
            if (name.Equals($"{tag}.exe", StringComparison.OrdinalIgnoreCase))
                return candidate;

            fallback ??= candidate;
        }

        return fallback;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "DesktopBuddyManager");
        return http;
    }

    private static string ExtractReleaseSha(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "unknown";

        if (tag.StartsWith("build-", StringComparison.OrdinalIgnoreCase))
            return tag[6..];

        var match = Regex.Match(tag, @"_([0-9a-fA-F]{7,40})$");
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    private static string NormalizeSha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "unknown";

        return sha.Trim().ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class ManagerUpdateResult
{
    private ManagerUpdateResult(bool hasUpdate, string message, string? tag, string? sha, string? assetName, string? downloadUrl, string latestSha, string latestTag)
    {
        HasUpdate = hasUpdate;
        Message = message;
        Tag = tag;
        Sha = sha;
        AssetName = assetName;
        DownloadUrl = downloadUrl;
        LatestSha = latestSha;
        LatestTag = latestTag;
    }

    internal bool HasUpdate { get; }
    internal string Message { get; }
    internal string? Tag { get; }
    internal string? Sha { get; }
    internal string? AssetName { get; }
    internal string? DownloadUrl { get; }
    internal string LatestSha { get; }
    internal string LatestTag { get; }

    internal static ManagerUpdateResult NoUpdate(string message, string latestSha = "unknown", string latestTag = "unknown") =>
        new(false, message, null, null, null, null, latestSha, latestTag);

    internal static ManagerUpdateResult UpdateAvailable(string tag, string sha, string assetName, string downloadUrl) =>
        new(true, $"Manager update available: {tag} ({sha}).", tag, sha, assetName, downloadUrl, sha, tag);
}

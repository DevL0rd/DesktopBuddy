using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopBuddyManager;

internal sealed class RendererDepsService
{
    private const string RenderiteHookReleasesApi = "https://api.github.com/repos/ResoniteModding/RenderiteHook/releases/latest";
    private const string BepInExRendererReleasesApi = "https://api.github.com/repos/ResoniteModding/BepInEx.Renderer/releases/latest";

    internal record DepsStatus(bool RenderiteHookInstalled, bool BepInExRendererInstalled, bool RendererPluginInstalled);

    internal static DepsStatus Check(string resonitePath)
    {
        var renderiteHook = File.Exists(Path.Combine(resonitePath, "rml_mods", "RenderiteHook.dll"));
        var bepInEx = Directory.Exists(Path.Combine(resonitePath, "Renderer", "BepInEx", "core"));
        var plugin = File.Exists(Path.Combine(resonitePath, "Renderer", "BepInEx", "plugins", "DesktopBuddyRenderer.dll"));
        return new DepsStatus(renderiteHook, bepInEx, plugin);
    }

    internal async Task InstallAllAsync(string resonitePath, Action<string> log)
    {
        using var http = CreateHttpClient();

        // 1. RenderiteHook
        var rmlModsDir = Path.Combine(resonitePath, "rml_mods");
        var renderiteHookPath = Path.Combine(rmlModsDir, "RenderiteHook.dll");
        if (!File.Exists(renderiteHookPath))
        {
            log("RenderiteHook: not found — downloading...");
            await InstallRenderiteHookAsync(http, resonitePath, log);
        }
        else
        {
            log($"RenderiteHook: already installed ({renderiteHookPath})");
        }

        // 2. BepInEx.Renderer
        var bepInExCorePath = Path.Combine(resonitePath, "Renderer", "BepInEx", "core");
        if (!Directory.Exists(bepInExCorePath))
        {
            log("BepInEx.Renderer: not found — downloading...");
            await InstallBepInExRendererAsync(http, resonitePath, log);
        }
        else
        {
            log($"BepInEx.Renderer: already installed ({bepInExCorePath})");
        }

        // 3. DesktopBuddyRenderer plugin
        InstallRendererPlugin(resonitePath, log);
    }

    private async Task InstallRenderiteHookAsync(HttpClient http, string resonitePath, Action<string> log)
    {
        var (zipUrl, _) = await GetLatestReleaseZipAsync(http, RenderiteHookReleasesApi);
        if (zipUrl == null)
        {
            log("RenderiteHook: no release asset found");
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await DownloadFileAsync(http, zipUrl, tempFile);

            var rmlModsDir = Path.Combine(resonitePath, "rml_mods");
            Directory.CreateDirectory(rmlModsDir);

            var rendererDir = Path.Combine(resonitePath, "Renderer");
            Directory.CreateDirectory(rendererDir);

            // RML mod zip structure: plugins/<modname>/<modname>.dll  →  rml_mods/<modname>.dll
            //                        plugins/<modname>/Doorstop/*       →  Renderer/<filename>
            using var archive = ZipFile.OpenRead(tempFile);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var parts = entry.FullName.Replace('\\', '/').Split('/');
                // parts: ["plugins", "<modname>", ...rest]
                if (parts.Length < 3 || parts[0] != "plugins")
                    continue;

                string destPath;
                if (parts.Length >= 4 && parts[2].Equals("Doorstop", StringComparison.OrdinalIgnoreCase))
                {
                    // Doorstop proxy files go into Renderer/
                    destPath = Path.Combine(rendererDir, entry.Name);
                }
                else if (parts.Length == 3)
                {
                    // Direct files under plugins/<modname>/ (e.g. the mod DLL) go into rml_mods/
                    destPath = Path.Combine(rmlModsDir, entry.Name);
                }
                else
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                log($"  {entry.FullName} → {destPath}");
            }

            log("RenderiteHook: installed");
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private async Task InstallBepInExRendererAsync(HttpClient http, string resonitePath, Action<string> log)
    {
        var (zipUrl, _) = await GetLatestReleaseZipAsync(http, BepInExRendererReleasesApi);
        if (zipUrl == null)
        {
            log("BepInEx.Renderer: no release asset found");
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await DownloadFileAsync(http, zipUrl, tempFile);

            var rendererDir = Path.Combine(resonitePath, "Renderer");
            Directory.CreateDirectory(rendererDir);

            // BepInEx.Renderer zip structure: BepInExPack/Renderer/BepInEx/core/...  →  Renderer/BepInEx/core/...
            const string zipPrefix = "BepInExPack/Renderer/";

            using var archive = ZipFile.OpenRead(tempFile);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var normalizedPath = entry.FullName.Replace('\\', '/');
                if (!normalizedPath.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = normalizedPath.Substring(zipPrefix.Length);
                var destPath = Path.Combine(rendererDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                log($"  {relativePath}");
            }

            // Ensure plugins dir exists
            Directory.CreateDirectory(Path.Combine(rendererDir, "BepInEx", "plugins"));

            log("BepInEx.Renderer: installed");
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private static void InstallRendererPlugin(string resonitePath, Action<string> log)
    {
        // After the relay-install copy, DesktopBuddyRenderer.dll is already on disk
        // at Renderer/BepInEx/plugins/DesktopBuddyRenderer.dll inside resonitePath.
        var destDir  = Path.Combine(resonitePath, "Renderer", "BepInEx", "plugins");
        var destPath = Path.Combine(destDir, "DesktopBuddyRenderer.dll");

        if (File.Exists(destPath))
        {
            log($"DesktopBuddyRenderer: already present at {destPath}");
            return;
        }

        log($"DesktopBuddyRenderer: not found at {destPath}");
        log("DesktopBuddyRenderer: skipped (re-run after a fresh install/update to populate it)");
    }

    private static async Task<(string? url, string? name)> GetLatestReleaseZipAsync(HttpClient http, string apiUrl)
    {
        using var response = await http.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var np) ? np.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var up) ? up.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (url, name);
        }

        return (null, null);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destPath)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var downloadStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await downloadStream.CopyToAsync(fileStream);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "DesktopBuddyManager");
        return http;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

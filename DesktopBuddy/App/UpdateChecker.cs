using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private const string ThunderstoreNamespace = "DesktopBuddy";
    private const string ThunderstorePackage = "DesktopBuddy";
    private const string ThunderstorePackageUrl = "https://thunderstore.io/c/resonite/p/DesktopBuddy/DesktopBuddy/";
    private const string ThunderstorePackageApiUrl = "https://thunderstore.io/api/experimental/package/DesktopBuddy/DesktopBuddy/";
    private const string ChangelogUrl = "https://raw.githubusercontent.com/DevL0rd/DesktopBuddy/main/CHANGELOG.md";

    private static void CheckForUpdate()
    {
        try
        {
            _updateCheckError = null;
            var buildSha = BuildInfo.GitSha;
            var currentVersion = DesktopBuddyVersion;
            Msg($"[Update] Current version: {currentVersion} ({buildSha})");

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            http.DefaultRequestHeaders.Add("User-Agent", "DesktopBuddy");
            var json = http.GetStringAsync(ThunderstorePackageApiUrl).Result;
            using var doc = JsonDocument.Parse(json);
            var package = doc.RootElement;
            if (!package.TryGetProperty("latest", out var latest))
                return;
            if (!latest.TryGetProperty("version_number", out var versionElement))
                return;

            var latestVersion = versionElement.GetString() ?? "";
            _remoteVersion = latestVersion;
            _remoteSha = latest.TryGetProperty("full_name", out var fullNameElement)
                ? fullNameElement.GetString() ?? $"{ThunderstoreNamespace}-{ThunderstorePackage}-{latestVersion}"
                : $"{ThunderstoreNamespace}-{ThunderstorePackage}-{latestVersion}";
            _remoteChangelog = FetchChangelog(http);
            _lastUpdateCheckUtc = DateTime.UtcNow;
            Msg($"[Update] Latest Thunderstore version: {latestVersion}");
            _latestVersion = IsRemoteVersionNewer(latestVersion, currentVersion) ? latestVersion : null;
        }
        catch (Exception ex) when (IsThunderstoreNotFound(ex))
        {
            _updateCheckError = "Thunderstore package is not published yet.";
            _lastUpdateCheckUtc = DateTime.UtcNow;
            Msg("[Update] Thunderstore package is not published yet");
        }
        catch (Exception ex)
        {
            _updateCheckError = ex.Message;
            _lastUpdateCheckUtc = DateTime.UtcNow;
            Msg($"[Update] Check failed: {ex.Message}");
        }
    }

    private static bool IsThunderstoreNotFound(Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException httpEx &&
            httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
            return true;

        if (ex is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Any(IsThunderstoreNotFound);

        return ex.InnerException != null && IsThunderstoreNotFound(ex.InnerException);
    }

    private static string FetchChangelog(System.Net.Http.HttpClient http)
    {
        try
        {
            string changelog = http.GetStringAsync(ChangelogUrl).Result;
            if (!string.IsNullOrWhiteSpace(changelog))
                return TrimChangelog(changelog);
        }
        catch (Exception ex)
        {
            Msg($"[Update] Changelog fetch failed: {ex.Message}");
        }

        return "";
    }

    private static bool IsRemoteVersionNewer(string remoteVersion, string currentVersion)
    {
        if (TryParseSemanticVersion(remoteVersion, out var remote) &&
            TryParseSemanticVersion(currentVersion, out var current))
        {
            return remote.CompareTo(current) > 0;
        }

        return !string.Equals(remoteVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSemanticVersion(string version, out Version parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var match = Regex.Match(version.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        parsed = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
        return true;
    }

    private static string TrimChangelog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Take(80)
            .ToArray();
        string trimmed = string.Join("\n", lines).Trim();
        return trimmed.Length <= 5000 ? trimmed : trimmed.Substring(0, 5000).TrimEnd() + "\n...";
    }

    private static void QueueUpdateInfoCheck(SettingsPanelState state)
    {
        if (_updateCheckInProgress)
            return;
        if (_lastUpdateCheckUtc != default && DateTime.UtcNow - _lastUpdateCheckUtc < TimeSpan.FromMinutes(5))
            return;

        _updateCheckInProgress = true;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                CheckForUpdate();
            }
            finally
            {
                _updateCheckInProgress = false;
                var world = state?.OwnerRoot?.World;
                if (world != null)
                {
                    world.RunInUpdates(1, () =>
                    {
                        if (state.SurfaceSlot != null && !state.SurfaceSlot.IsDestroyed && state.SurfaceSlot.ActiveSelf && state.ActiveTab == SettingsPanelTab.UpdateInfo)
                            RebuildSettingsPanel(state, state.Session);
                    });
                }
            }
        });
    }

    private static void ShowUpdatePopup(Slot root, float w, float canvasScale)
    {
        Msg($"[Update] Showing update popup: {_latestVersion}");

        var updateSlot = root.AddSlot("UpdateNotice");
        updateSlot.LocalPosition = new float3(0f, 0f, -0.002f);
        updateSlot.LocalScale = float3.One * canvasScale;

        var updateCanvas = updateSlot.AttachComponent<Canvas>();
        float popupW = Math.Min(w * 0.6f, 400f);
        updateCanvas.Size.Value = new float2(popupW, 160f);
        var updateUi = new UIBuilder(updateCanvas);

        var bg = updateUi.Image(new colorX(0.12f, 0.12f, 0.15f, 0.95f));
        updateUi.NestInto(bg.RectTransform);
        updateUi.VerticalLayout(8f, childAlignment: Alignment.MiddleCenter);
        updateUi.Style.FlexibleWidth = 1f;

        updateUi.Style.MinHeight = 32f;
        var msg = updateUi.Text("Update available!", bestFit: false, alignment: Alignment.MiddleCenter);
        msg.Size.Value = 22f;
        msg.Color.Value = new colorX(0.95f, 0.85f, 0.3f, 1f);

        updateUi.Style.MinHeight = 36f;
        var dlBtn = updateUi.Button("Download");
        var dlTxt = dlBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (dlTxt != null) { dlTxt.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f); dlTxt.Size.Value = 18f; }
        if (dlBtn.ColorDrivers.Count > 0)
        {
            var cd = dlBtn.ColorDrivers[0];
            cd.NormalColor.Value = new colorX(0.2f, 0.4f, 0.6f, 1f);
            cd.HighlightColor.Value = new colorX(0.25f, 0.5f, 0.75f, 1f);
            cd.PressColor.Value = new colorX(0.15f, 0.3f, 0.45f, 1f);
        }
        dlBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            try
            {
                Msg("[Update] Opening releases page");
                try { Process.Start(new ProcessStartInfo(ThunderstorePackageUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Msg($"[Update] Failed: {ex.Message}"); }
                if (!updateSlot.IsDestroyed) updateSlot.Destroy();
            }
            catch (Exception ex)
            {
                Msg($"[Update] Download button error: {ex}");
            }
        };

        updateUi.Style.MinHeight = 30f;
        var dismissBtn = updateUi.Button("Dismiss");
        var dismissTxt = dismissBtn.Slot.GetComponentInChildren<TextRenderer>();
        if (dismissTxt != null) { dismissTxt.Color.Value = new colorX(0.7f, 0.7f, 0.7f, 1f); dismissTxt.Size.Value = 14f; }
        if (dismissBtn.ColorDrivers.Count > 0)
        {
            var cd = dismissBtn.ColorDrivers[0];
            cd.NormalColor.Value = new colorX(0.2f, 0.2f, 0.25f, 1f);
            cd.HighlightColor.Value = new colorX(0.3f, 0.3f, 0.35f, 1f);
            cd.PressColor.Value = new colorX(0.15f, 0.15f, 0.18f, 1f);
        }
        dismissBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            try
            {
                if (!updateSlot.IsDestroyed) updateSlot.Destroy();
            }
            catch (Exception ex)
            {
                Msg($"[Update] Dismiss button error: {ex}");
            }
        };

        root.World.RunInUpdates(15 * 60, () =>
        {
            try
            {
                if (!updateSlot.IsDestroyed) updateSlot.Destroy();
            }
            catch (Exception ex)
            {
                Msg($"[Update] Auto-dismiss error: {ex}");
            }
        });
    }
}

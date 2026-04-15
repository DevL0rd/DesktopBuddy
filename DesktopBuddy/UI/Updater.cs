using System;
using System.Diagnostics;
using FrooxEngine;
using FrooxEngine.UIX;
using Elements.Core;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void CheckForUpdate()
    {
        try
        {
            var buildSha = BuildInfo.GitSha;
            Msg($"[Update] Current build: {buildSha}");

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "DesktopBuddy");
            var json = http.GetStringAsync("https://api.github.com/repos/DevL0rd/DesktopBuddy/releases/latest").Result;
            var match = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (match.Success)
            {
                var tag = match.Groups[1].Value;
                // Extract SHA from tag tail: "DesktopBuddy-Alpha-2026.04.14_10.20.41_7cb92c7" → "7cb92c7"
                // Also handles legacy "build-<sha>" format.
                string remoteSha;
                if (tag.StartsWith("build-", StringComparison.OrdinalIgnoreCase))
                {
                    remoteSha = tag.Substring(6);
                }
                else
                {
                    var shaMatch = System.Text.RegularExpressions.Regex.Match(tag, @"_([0-9a-fA-F]{7,40})$");
                    remoteSha = shaMatch.Success ? shaMatch.Groups[1].Value : tag;
                }
                Msg($"[Update] Latest release: {tag} (sha: {remoteSha})");
                if (buildSha != "unknown" && remoteSha != buildSha)
                    _latestVersion = tag;
            }
        }
        catch (Exception ex)
        {
            Msg($"[Update] Check failed: {ex.Message}");
        }
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
            Msg("[Update] Opening releases page");
            try { Process.Start(new ProcessStartInfo("https://github.com/DevL0rd/DesktopBuddy/releases") { UseShellExecute = true }); }
            catch (Exception ex) { Msg($"[Update] Failed: {ex.Message}"); }
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
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
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        };

        root.World.RunInUpdates(15 * 60, () =>
        {
            if (!updateSlot.IsDestroyed) updateSlot.Destroy();
        });
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static string _mediaMtxStreamBase;

    internal static string GetMediaMtxRtspUrl(int streamId)
    {
        string host = Config!.GetValue(MediaMtxHost).Trim();
        int port = Config.GetValue(MediaMtxPort);
        string name = Config.GetValue(MediaMtxStreamName)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (_mediaMtxStreamBase == null)
                _mediaMtxStreamBase = "desktopbuddy-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            name = _mediaMtxStreamBase;
        }
        return $"rtsp://{host}:{port}/{name}_{streamId}";
    }

    internal static string GetPanelCurvePreferenceKey(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "desktop";

        string exePath = WindowIconExtractor.GetExecutablePath(hwnd);
        if (!string.IsNullOrWhiteSpace(exePath))
            return "app:" + exePath.ToLowerInvariant();

        WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId != 0 ? $"pid:{processId}" : $"hwnd:{hwnd.ToInt64():X}";
    }

    internal static float GetPanelCurvePreference(string key, float fallback)
    {
        try
        {
            var prefs = ParsePanelCurvePreferences(Config?.GetValue(PanelCurvePreferences));
            return prefs.TryGetValue(key, out float value) ? Math.Clamp(value, 0f, 1f) : fallback;
        }
        catch (Exception ex)
        {
            Msg($"[Curve] Failed to load preference: {ex.Message}");
            return fallback;
        }
    }

    internal static void SetPanelCurvePreference(string key, float value)
    {
        try
        {
            if (Config == null || string.IsNullOrWhiteSpace(key)) return;

            var prefs = ParsePanelCurvePreferences(Config.GetValue(PanelCurvePreferences));
            prefs[key] = Math.Clamp(value, 0f, 1f);
            Config.Set(PanelCurvePreferences, SerializePanelCurvePreferences(prefs));
            Config.Save();
        }
        catch (Exception ex)
        {
            Msg($"[Curve] Failed to save preference: {ex.Message}");
        }
    }

    private static Dictionary<string, float> ParsePanelCurvePreferences(string serialized)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(serialized))
            return result;

        foreach (string line in serialized.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string key = Uri.UnescapeDataString(line[..split]);
            string rawValue = line[(split + 1)..];
            if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                result[key] = Math.Clamp(value, 0f, 1f);
        }
        return result;
    }

    private static string SerializePanelCurvePreferences(Dictionary<string, float> prefs)
    {
        var lines = new List<string>();
        foreach (var pair in prefs)
        {
            string key = Uri.EscapeDataString(pair.Key);
            string value = Math.Clamp(pair.Value, 0f, 1f).ToString("R", CultureInfo.InvariantCulture);
            lines.Add($"{key}={value}");
        }
        return string.Join("\n", lines);
    }

}

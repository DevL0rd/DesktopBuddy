using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{


    private static void SaveCurrentConfigDefaults()
    {
        try
        {
            if (Config == null) return;

            if (_configResetForNewDefaults)
            {
                ApplyFreshConfigDefaults();
                Msg("[Config] Applied fresh defaults: spatialAudio=false bitrate=10 streamFps=60 maxStreamResolution=2560 network=cloudflare");
            }
            else
            {
                Config.Set(SpatialAudioEnabled, Config.GetValue(SpatialAudioEnabled));
                Config.Set(Bitrate, Config.GetValue(Bitrate));
                Config.Set(StreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
                Config.Set(MaxStreamResolution, Math.Clamp(Config.GetValue(MaxStreamResolution), 128, 8192));
            }

            Config.Set(CheckForUpdates, Config.GetValue(CheckForUpdates));
            Config.Set(StreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
            Config.Set(UseMediaMtx, Config.GetValue(UseMediaMtx));
            Config.Set(MediaMtxHost, Config.GetValue(MediaMtxHost));
            Config.Set(MediaMtxPort, Config.GetValue(MediaMtxPort));
            Config.Set(MediaMtxStreamName, Config.GetValue(MediaMtxStreamName));
            Config.Set(StreamNetworkMode, NormalizeStreamNetworkMode(Config.GetValue(StreamNetworkMode)));
            Config.Set(PortForwardHostMode, NormalizePortForwardHostMode(Config.GetValue(PortForwardHostMode)));
            Config.Set(PortForwardAutoIpMode, NormalizePortForwardAutoIpMode(Config.GetValue(PortForwardAutoIpMode)));
            Config.Set(PortForwardHost, Config.GetValue(PortForwardHost)?.Trim() ?? "");
            Config.Set(PortForwardUseNat, Config.GetValue(PortForwardUseNat));
            Config.Set(PanelCurvePreferences, Config.GetValue(PanelCurvePreferences) ?? "");
            Config.Set(ViewerCullingMode, NormalizeViewerCullingMode(Config.GetValue(ViewerCullingMode)));
            Config.Set(ViewerCullingPreview, Config.GetValue(ViewerCullingPreview));
            float viewerFrustumAngle = Config.GetValue(ViewerFrustumWidth);
            Config.Set(ViewerFrustumWidth, viewerFrustumAngle < 5f ? 120f : Math.Clamp(viewerFrustumAngle, 30f, 170f));
            Config.Set(ViewerFrustumDepth, Math.Clamp(Config.GetValue(ViewerFrustumDepth), 1f, 10f));
            Config.Set(ViewerDistance, Math.Clamp(Config.GetValue(ViewerDistance), 1f, 10f));
            Config.Set(EncoderPreference, NormalizeEncoderPreference(Config.GetValue(EncoderPreference)));
            Config.Set(PreferredGpuLuid, Config.GetValue(PreferredGpuLuid)?.Trim() ?? "");
            Config.Set(StreamAudioOutputVolume, NormalizeStreamAudioOutputVolume(Config.GetValue(StreamAudioOutputVolume)));
            Config.Set(StreamAudioGlobalMode, NormalizeStreamAudioGlobalMode(Config.GetValue(StreamAudioGlobalMode)));
            Config.Set(StreamAudioSpatialize, Config.GetValue(StreamAudioSpatialize));
            Config.Set(StreamAudioSpatialBlend, Math.Clamp(Config.GetValue(StreamAudioSpatialBlend), 0f, 1f));
            Config.Set(StreamAudioDistanceSpace, NormalizeStreamAudioDistanceSpace(Config.GetValue(StreamAudioDistanceSpace)));
            Config.Set(StreamAudioDopplerLevel, Math.Clamp(Config.GetValue(StreamAudioDopplerLevel), 0f, 1f));
            Config.Set(StreamAudioPitch, Math.Clamp(Config.GetValue(StreamAudioPitch), 0.5f, 2f));
            Config.Set(StreamAudioIgnoreAudioEffects, Config.GetValue(StreamAudioIgnoreAudioEffects));
            Config.Set(StreamAudioTypeGroup, NormalizeStreamAudioTypeGroup(Config.GetValue(StreamAudioTypeGroup)));
            Config.Set(StreamAudioRolloffMode, NormalizeStreamAudioRolloffMode(Config.GetValue(StreamAudioRolloffMode)));
            Config.Set(StreamAudioMinDistance, Math.Clamp(Config.GetValue(StreamAudioMinDistance), 0f, 10f));
            Config.Set(StreamAudioMaxDistance, Math.Clamp(Config.GetValue(StreamAudioMaxDistance), 1f, 50f));
            Config.Set(StreamAudioSpatializationStartDistance, Math.Clamp(Config.GetValue(StreamAudioSpatializationStartDistance), 0f, 10f));
            Config.Set(StreamAudioSpatializationTransitionRange, Math.Clamp(Config.GetValue(StreamAudioSpatializationTransitionRange), 0f, 10f));
            Config.Set(StreamAudioPriority, Math.Clamp(Config.GetValue(StreamAudioPriority), 0, 256));
            Config.Set(StreamAudioMinScale, Math.Clamp(Config.GetValue(StreamAudioMinScale), 0f, 1000f));
            Config.Set(StreamAudioMaxScale, Math.Clamp(Config.GetValue(StreamAudioMaxScale), 0f, 1000f));
            Config.Save();
            _configResetForNewDefaults = false;
            RefreshRuntimeStreamSettingsFromConfig();
        }
        catch (Exception ex)
        {
            Msg($"[Config] Failed to save current defaults: {ex.Message}");
        }
    }

    private static void ApplyFreshConfigDefaults()
    {
        Config.Set(SpatialAudioEnabled, false);
        Config.Set(CheckForUpdates, true);
        Config.Set(Bitrate, 10);
        Config.Set(StreamFps, 60);
        Config.Set(MaxStreamResolution, 2560);
        Config.Set(UseMediaMtx, false);
        Config.Set(MediaMtxHost, "");
        Config.Set(MediaMtxPort, 8554);
        Config.Set(MediaMtxStreamName, "");
        Config.Set(StreamNetworkMode, "cloudflare");
        Config.Set(PortForwardHostMode, "auto");
        Config.Set(PortForwardAutoIpMode, "external");
        Config.Set(PortForwardHost, "");
        Config.Set(PortForwardUseNat, false);
        Config.Set(PanelCurvePreferences, "");
        Config.Set(ViewerCullingMode, "frustum");
        Config.Set(ViewerCullingPreview, false);
        Config.Set(ViewerFrustumWidth, 120.0f);
        Config.Set(ViewerFrustumDepth, 3.0f);
        Config.Set(ViewerDistance, 3.0f);
        Config.Set(EncoderPreference, "auto");
        Config.Set(PreferredGpuLuid, "");
        Config.Set(StreamAudioOutputVolume, 1.0f);
        Config.Set(StreamAudioGlobalMode, "positional");
        Config.Set(StreamAudioSpatialize, true);
        Config.Set(StreamAudioSpatialBlend, 1.0f);
        Config.Set(StreamAudioDistanceSpace, "global");
        Config.Set(StreamAudioDopplerLevel, 0.0f);
        Config.Set(StreamAudioPitch, 1.0f);
        Config.Set(StreamAudioIgnoreAudioEffects, true);
        Config.Set(StreamAudioTypeGroup, "multimedia");
        Config.Set(StreamAudioRolloffMode, "linear");
        Config.Set(StreamAudioMinDistance, 1.0f);
        Config.Set(StreamAudioMaxDistance, 30.0f);
        Config.Set(StreamAudioSpatializationStartDistance, 0.01f);
        Config.Set(StreamAudioSpatializationTransitionRange, 0.01f);
        Config.Set(StreamAudioPriority, 128);
        Config.Set(StreamAudioMinScale, 0.0f);
        Config.Set(StreamAudioMaxScale, 1000.0f);
    }


    private static void RefreshRuntimeStreamSettingsFromConfig()
    {
        try
        {
            if (Config == null) return;
            Volatile.Write(ref _runtimeBitrateMbps, Math.Clamp(Config.GetValue(Bitrate), 1, 200));
            Volatile.Write(ref _runtimeStreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
            Volatile.Write(ref _runtimeMaxStreamResolution, Math.Clamp(Config.GetValue(MaxStreamResolution), 128, 8192));
            _runtimeEncoderPreference = NormalizeEncoderPreference(Config.GetValue(EncoderPreference));
        }
        catch (Exception ex)
        {
            Msg($"[Config] Failed to refresh runtime stream settings: {ex.Message}");
        }
    }

    private static void ApplyRuntimeSetting<T>(ModConfigurationKey<T> key, T value)
    {
        if (ReferenceEquals(key, Bitrate) && value is int bitrate)
            Volatile.Write(ref _runtimeBitrateMbps, Math.Clamp(bitrate, 1, 200));
        else if (ReferenceEquals(key, StreamFps) && value is int fps)
            Volatile.Write(ref _runtimeStreamFps, Math.Clamp(fps, 1, 240));
        else if (ReferenceEquals(key, MaxStreamResolution) && value is int resolution)
            Volatile.Write(ref _runtimeMaxStreamResolution, Math.Clamp(resolution, 128, 8192));
        else if (ReferenceEquals(key, EncoderPreference) && value is string encoder)
            _runtimeEncoderPreference = NormalizeEncoderPreference(encoder);
    }

    internal static void SaveConfigValue<T>(ModConfigurationKey<T> key, T value)
    {
        try
        {
            if (Config == null) return;
            ApplyRuntimeSetting(key, value);
            Config.Set(key, value);
            _settingsConfigDirty = true;
        }
        catch (Exception ex)
        {
            Msg($"[Config] Failed to set {key?.Name}: {ex.Message}");
        }
    }

    internal static void FlushSettingsConfig()
    {
        try
        {
            if (Config == null || !_settingsConfigDirty) return;
            _settingsConfigDirty = false;
            Config.Save();
        }
        catch (Exception ex)
        {
            Msg($"[Config] Failed to save settings: {ex.Message}");
            _settingsConfigDirty = true;
        }
    }

    private static void DetectStoredConfigVersionMismatch()
    {
        try
        {
            string path = FindRmlConfigPath();
            if (path == null || !File.Exists(path)) return;

            string json = File.ReadAllText(path);
            var match = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            if (!match.Success || !System.Version.TryParse(match.Groups[1].Value, out var storedVersion))
            {
                Msg($"[Config] Existing config has no readable version; applying fresh defaults for schema {CurrentConfigSchemaVersion}");
                _configResetForNewDefaults = true;
                return;
            }

            if (storedVersion != CurrentConfigSchemaVersion)
            {
                Msg($"[Config] Existing config version {storedVersion} differs from schema {CurrentConfigSchemaVersion}; applying fresh defaults");
                _configResetForNewDefaults = true;
            }
        }
        catch (Exception ex)
        {
            Msg($"[Config] Could not inspect existing config version: {ex.Message}");
        }
    }

    private static string FindRmlConfigPath()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
        string current = Directory.GetCurrentDirectory() ?? "";

        string[] candidates =
        {
            Path.Combine(assemblyDir, "..", "rml_config", "DesktopBuddy.json"),
            Path.Combine(assemblyDir, "..", "..", "rml_config", "DesktopBuddy.json"),
            Path.Combine(baseDir, "rml_config", "DesktopBuddy.json"),
            Path.Combine(baseDir, "..", "rml_config", "DesktopBuddy.json"),
            Path.Combine(current, "rml_config", "DesktopBuddy.json"),
        };

        foreach (string candidate in candidates)
        {
            try
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return null;
    }

}

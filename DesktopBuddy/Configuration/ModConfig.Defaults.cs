using System;
using System.Threading;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void SaveCurrentConfigDefaults()
    {
        try
        {
            if (Config == null) return;

            Config.Set(SpatialAudioEnabled, Config.GetValue(SpatialAudioEnabled));
            Config.Set(Bitrate, Config.GetValue(Bitrate));
            Config.Set(StreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
            Config.Set(MaxStreamResolution, Math.Clamp(Config.GetValue(MaxStreamResolution), 128, 8192));
            Config.Set(CheckForUpdates, Config.GetValue(CheckForUpdates));
            Config.Set(ShowContextMenuItem, Config.GetValue(ShowContextMenuItem));
            Config.Set(ThrowToDestroy, Config.GetValue(ThrowToDestroy));
            Config.Set(SpawnNewWindowsInGame, Config.GetValue(SpawnNewWindowsInGame));
            Config.Set(SpawnNewWindowsPrivate, Config.GetValue(SpawnNewWindowsPrivate));
            Config.Set(NewWindowsStartPrivate, Config.GetValue(NewWindowsStartPrivate));
            Config.Set(DynamicLightsEnabled, Config.GetValue(DynamicLightsEnabled));
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
            Config.Set(LinuxSharedSources, Config.GetValue(LinuxSharedSources) ?? "");
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
            RefreshRuntimeStreamSettingsFromConfig();
        }
        catch (Exception ex)
        {
            Msg($"[Config] Failed to save current defaults: {ex.Message}");
        }
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

    private static void ApplyFreshConfigDefaults()
    {
        Config.Set(SpatialAudioEnabled, SpatialAudioEnabled.DefaultValue);
        Config.Set(CheckForUpdates, CheckForUpdates.DefaultValue);
        Config.Set(ShowContextMenuItem, ShowContextMenuItem.DefaultValue);
        Config.Set(ThrowToDestroy, ThrowToDestroy.DefaultValue);
        Config.Set(SpawnNewWindowsInGame, SpawnNewWindowsInGame.DefaultValue);
        Config.Set(SpawnNewWindowsPrivate, SpawnNewWindowsPrivate.DefaultValue);
        Config.Set(NewWindowsStartPrivate, NewWindowsStartPrivate.DefaultValue);
        Config.Set(DynamicLightsEnabled, DynamicLightsEnabled.DefaultValue);
        Config.Set(Bitrate, Bitrate.DefaultValue);
        Config.Set(StreamFps, StreamFps.DefaultValue);
        Config.Set(MaxStreamResolution, MaxStreamResolution.DefaultValue);
        Config.Set(UseMediaMtx, UseMediaMtx.DefaultValue);
        Config.Set(MediaMtxHost, MediaMtxHost.DefaultValue);
        Config.Set(MediaMtxPort, MediaMtxPort.DefaultValue);
        Config.Set(MediaMtxStreamName, MediaMtxStreamName.DefaultValue);
        Config.Set(StreamNetworkMode, StreamNetworkMode.DefaultValue);
        Config.Set(PortForwardHostMode, PortForwardHostMode.DefaultValue);
        Config.Set(PortForwardAutoIpMode, PortForwardAutoIpMode.DefaultValue);
        Config.Set(PortForwardHost, PortForwardHost.DefaultValue);
        Config.Set(PortForwardUseNat, PortForwardUseNat.DefaultValue);
        Config.Set(PanelCurvePreferences, PanelCurvePreferences.DefaultValue);
        Config.Set(LinuxSharedSources, LinuxSharedSources.DefaultValue);
        Config.Set(ViewerCullingMode, ViewerCullingMode.DefaultValue);
        Config.Set(ViewerCullingPreview, ViewerCullingPreview.DefaultValue);
        Config.Set(ViewerFrustumWidth, ViewerFrustumWidth.DefaultValue);
        Config.Set(ViewerFrustumDepth, ViewerFrustumDepth.DefaultValue);
        Config.Set(ViewerDistance, ViewerDistance.DefaultValue);
        Config.Set(EncoderPreference, EncoderPreference.DefaultValue);
        Config.Set(PreferredGpuLuid, PreferredGpuLuid.DefaultValue);
        Config.Set(StreamAudioOutputVolume, StreamAudioOutputVolume.DefaultValue);
        Config.Set(StreamAudioGlobalMode, StreamAudioGlobalMode.DefaultValue);
        Config.Set(StreamAudioSpatialize, StreamAudioSpatialize.DefaultValue);
        Config.Set(StreamAudioSpatialBlend, StreamAudioSpatialBlend.DefaultValue);
        Config.Set(StreamAudioDistanceSpace, StreamAudioDistanceSpace.DefaultValue);
        Config.Set(StreamAudioDopplerLevel, StreamAudioDopplerLevel.DefaultValue);
        Config.Set(StreamAudioPitch, StreamAudioPitch.DefaultValue);
        Config.Set(StreamAudioIgnoreAudioEffects, StreamAudioIgnoreAudioEffects.DefaultValue);
        Config.Set(StreamAudioTypeGroup, StreamAudioTypeGroup.DefaultValue);
        Config.Set(StreamAudioRolloffMode, StreamAudioRolloffMode.DefaultValue);
        Config.Set(StreamAudioMinDistance, StreamAudioMinDistance.DefaultValue);
        Config.Set(StreamAudioMaxDistance, StreamAudioMaxDistance.DefaultValue);
        Config.Set(StreamAudioSpatializationStartDistance, StreamAudioSpatializationStartDistance.DefaultValue);
        Config.Set(StreamAudioSpatializationTransitionRange, StreamAudioSpatializationTransitionRange.DefaultValue);
        Config.Set(StreamAudioPriority, StreamAudioPriority.DefaultValue);
        Config.Set(StreamAudioMinScale, StreamAudioMinScale.DefaultValue);
        Config.Set(StreamAudioMaxScale, StreamAudioMaxScale.DefaultValue);
    }

    private static void ApplyRuntimeSetting<T>(DesktopBuddyConfigKey<T> key, T value)
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

    internal static void SaveConfigValue<T>(DesktopBuddyConfigKey<T> key, T value)
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
}

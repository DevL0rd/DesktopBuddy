using System;
using ResoniteModLoader;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static readonly Version CurrentConfigSchemaVersion = new(1, 0, 13);
    internal static ModConfiguration Config;
    private static bool _configResetForNewDefaults;
    private static int _runtimeBitrateMbps = 10;
    private static int _runtimeStreamFps = 60;
    private static int _runtimeMaxStreamResolution = 2560;
    private static string _runtimeEncoderPreference = "auto";

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> SpatialAudioEnabled =
        new("spatialAudio", "Enable spatial in-game audio (redirects window audio to VB-Cable). When off, use Windows volume slider instead.", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> CheckForUpdates =
        new("checkForUpdates", "Check for updates and show a notification when a new version is available on startup.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> Bitrate =
        new("bitrate", "Video encoding bitrate in Mbps.", () => 10);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> StreamFps =
        new("streamFps", "Nominal stream FPS for encoder timing. Capture remains event-driven and is not frame-capped.", () => 60);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> MaxStreamResolution =
        new("maxStreamResolution", "Maximum encoded stream long-edge resolution. 2560 is 2K/QHD.", () => 2560);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> UseMediaMtx =
        new("useMediaMtx", "Use an external MediaMTX server for streaming instead of the built-in Cloudflare HTTP stream.", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> MediaMtxHost =
        new("mediaMtxHost", "MediaMTX server address (IP or hostname).", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> MediaMtxPort =
        new("mediaMtxPort", "MediaMTX RTSP port.", () => 8554);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> MediaMtxStreamName =
        new("mediaMtxStreamName", "MediaMTX stream name (path component of the RTSP URL). Leave blank to auto-generate a random name per session.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> StreamNetworkMode =
        new("streamNetworkMode", "Built-in stream access mode: cloudflare or port_forward.", () => "cloudflare");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PortForwardHostMode =
        new("portForwardHostMode", "Port-forward host mode: auto or manual.", () => "auto");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PortForwardAutoIpMode =
        new("portForwardAutoIpMode", "Auto port-forward host IP source. External public IPv4 is always used.", () => "external");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PortForwardHost =
        new("portForwardHost", "Manual public hostname or IP for port-forwarded built-in streams.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> PortForwardUseNat =
        new("portForwardUseNat", "Automatically create a UPnP/NAT TCP port mapping for the built-in stream port.", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PanelCurvePreferences =
        new("panelCurvePreferences", "Saved DesktopBuddy panel curve values, keyed by application executable path or shared desktop capture.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> ViewerCullingMode =
        new("viewerCullingMode", "Viewer culling mode for remote streams: frustum or distance.", () => "frustum");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ViewerCullingPreview =
        new("viewerCullingPreview", "Show the viewer culling preview guide on DesktopBuddy panels.", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> ViewerFrustumWidth =
        new("viewerFrustumWidth", "Viewer frustum culling preview angle in degrees.", () => 120.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> ViewerFrustumDepth =
        new("viewerFrustumDepth", "Viewer frustum culling depth in meters.", () => 3.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> ViewerDistance =
        new("viewerDistance", "Viewer distance culling radius in meters.", () => 3.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> EncoderPreference =
        new("encoderPreference", "Explicit stream encoder preference, or auto.", () => "auto");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PreferredGpuLuid =
        new("preferredGpuLuid", "Preferred DXGI adapter LUID for DesktopBuddy capture/encoding, or blank for auto.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioOutputVolume =
        new("streamAudioOutputVolume", "Default local stream AudioOutput volume.", () => 1.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> StreamAudioGlobalMode =
        new("streamAudioGlobalMode", "Stream AudioOutput global mode: auto, global, or positional.", () => "positional");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> StreamAudioSpatialize =
        new("streamAudioSpatialize", "Enable stream AudioOutput spatialization.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioSpatialBlend =
        new("streamAudioSpatialBlend", "Stream AudioOutput spatial blend.", () => 1.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> StreamAudioDistanceSpace =
        new("streamAudioDistanceSpace", "Stream AudioOutput distance space: local or global.", () => "global");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioDopplerLevel =
        new("streamAudioDopplerLevel", "Stream AudioOutput doppler level.", () => 0.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioPitch =
        new("streamAudioPitch", "Stream AudioOutput pitch.", () => 1.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> StreamAudioIgnoreAudioEffects =
        new("streamAudioIgnoreAudioEffects", "Bypass Resonite audio effects for stream playback.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> StreamAudioTypeGroup =
        new("streamAudioTypeGroup", "Stream AudioOutput type group: multimedia, sound_effect, voice, or ui.", () => "multimedia");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> StreamAudioRolloffMode =
        new("streamAudioRolloffMode", "Stream AudioOutput rolloff mode: logarithmic_fade_off or linear.", () => "linear");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioMinDistance =
        new("streamAudioMinDistance", "Stream AudioOutput minimum distance.", () => 1.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioMaxDistance =
        new("streamAudioMaxDistance", "Stream AudioOutput maximum distance.", () => 30.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioSpatializationStartDistance =
        new("streamAudioSpatializationStartDistance", "Stream AudioOutput spatialization start distance.", () => 0.01f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioSpatializationTransitionRange =
        new("streamAudioSpatializationTransitionRange", "Stream AudioOutput spatialization transition range.", () => 0.01f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> StreamAudioPriority =
        new("streamAudioPriority", "Stream AudioOutput priority.", () => 128);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioMinScale =
        new("streamAudioMinScale", "Stream AudioOutput local-distance minimum scale clamp.", () => 0.0f);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<float> StreamAudioMaxScale =
        new("streamAudioMaxScale", "Stream AudioOutput local-distance maximum scale clamp.", () => 1000.0f);

    internal static bool IsMediaMtxEnabled =>
        Config?.GetValue(UseMediaMtx) == true && !string.IsNullOrWhiteSpace(Config?.GetValue(MediaMtxHost));
}

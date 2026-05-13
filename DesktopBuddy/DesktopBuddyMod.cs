using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using Renderite.Shared;
using Elements.Core;
using SkyFrost.Base;

namespace DesktopBuddy;

public partial class DesktopBuddyMod : ResoniteMod
{
    public override string Name => "DesktopBuddy";
    public override string Author => "DevL0rd";
    internal const string DesktopBuddyVersion = "1.0.12";
    public override string Version => DesktopBuddyVersion;
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

    private static readonly Version CurrentConfigSchemaVersion = new(1, 0, 12);
    internal static ModConfiguration? Config;
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

    internal static bool IsMediaMtxEnabled =>
        Config?.GetValue(UseMediaMtx) == true && !string.IsNullOrWhiteSpace(Config?.GetValue(MediaMtxHost));

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

    internal static bool IsDesktopMode(World world)
    {
        try { return world?.LocalUser?.HeadDevice == HeadOutputDevice.Screen; }
        catch { return false; }
    }

    internal static readonly List<DesktopSession> ActiveSessions = new();
    private static int _nextStreamId;

    internal static readonly HashSet<RefID> DesktopCanvasIds = new();
    private static readonly object TopBarRaycastGate = new();
    private static readonly Dictionary<RefID, Slot> TopBarRaycastTargets = new();

    private static readonly Dictionary<IntPtr, SharedStream> _sharedStreams = new();

    internal class SharedStream
    {
        public int StreamId;
        public FfmpegEncoder Encoder;
        public AudioCapture Audio;
        public Uri StreamUrl;
        public int RefCount;
        public DesktopSession DriverSession;
    }

    internal static MjpegServer? StreamServer;
    internal static VirtualCamera VCam;
    internal static VirtualMic VMic;
    private const int STREAM_PORT = 48080;
    internal static string? TunnelUrl;
    private static Process _tunnelProcess;
    private static string _cfPath;
    private static volatile bool _tunnelRestarting;
    internal static readonly PerfTimer Perf = new();

    internal static string NormalizeStreamNetworkMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value == "port_forward" ? "port_forward" : "cloudflare";
    }

    internal static string NormalizePortForwardHostMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "manual" ? "manual" : "auto";
    }

    internal static string NormalizePortForwardAutoIpMode(string value)
    {
        return "external";
    }

    internal static bool UseCloudflareTunnel =>
        Config == null || NormalizeStreamNetworkMode(Config.GetValue(StreamNetworkMode)) == "cloudflare";

    internal static Uri GetBuiltInStreamUrl(int streamId)
    {
        string baseUrl = GetBuiltInStreamBaseUrl();
        return string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri($"{baseUrl}/stream/{streamId}");
    }

    internal static string GetBuiltInStreamBaseUrl()
    {
        if (NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "cloudflare")
            return TunnelUrl;

        string host = ResolvePortForwardHost();
        return string.IsNullOrWhiteSpace(host) ? null : $"http://{host}:{STREAM_PORT}";
    }

    internal static string ResolvePortForwardHost()
    {
        if (Config != null && NormalizePortForwardHostMode(Config.GetValue(PortForwardHostMode)) == "manual")
            return Config.GetValue(PortForwardHost)?.Trim();

        return GetAutoExternalIPv4Address();
    }

    internal static string GetBestLocalIPv4Address()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        return address.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Msg($"[Network] Failed to auto-detect local IP: {ex.Message}");
        }

        return "";
    }

    internal static SharedTextureBridgeChannel? TextureBridgeChannel;
    private static bool _textureBridgeOpened;

    internal static readonly System.Collections.Generic.HashSet<DesktopTextureProvider> OurProviders = new();

    private static Thread _windowPollerThread;
    private static volatile bool _windowPollerRunning;
    internal static readonly ConcurrentQueue<WindowEvent> _windowEvents = new();

    internal struct WindowEvent
    {
        public DesktopSession Session;
        public IntPtr WindowHwnd;
        public string Title;
        public WindowEventType EventType;
    }
    internal enum WindowEventType { NewTopLevelWindow, TitleChanged }

    private static string _latestVersion;
    private static string _remoteVersion;
    private static string _remoteSha;
    private static string _remoteChangelog;
    private static string _updateCheckError;
    private static DateTime _lastUpdateCheckUtc;
    private static volatile bool _updateCheckInProgress;
    private static bool _updateShown;
    private static volatile bool _settingsConfigDirty;

    public override void DefineConfiguration(ModConfigurationDefinitionBuilder builder)
    {
        builder.Version(CurrentConfigSchemaVersion);
    }

    public override IncompatibleConfigurationHandlingOption HandleIncompatibleConfigurationVersions(Version serializedVersion, Version definedVersion)
    {
        if (serializedVersion != definedVersion)
        {
            Msg($"[Config] Resetting config {serializedVersion} for config schema {definedVersion}");
            _configResetForNewDefaults = true;
            return IncompatibleConfigurationHandlingOption.CLOBBER;
        }

        return IncompatibleConfigurationHandlingOption.ERROR;
    }

    public override void OnEngineInit()
    {
        Log.StartSession();
        DetectStoredConfigVersionMismatch();
        Config = GetConfiguration();
        SaveCurrentConfigDefaults();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Msg($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}):\n{e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            if (e.Exception.ToString().Contains("ResoniteModLoader.ModConfiguration.SaveInternal"))
            {
                Log.Msg($"[Config] RML config save task failed and was marked observed: {e.Exception.GetBaseException().GetType().Name}: {e.Exception.GetBaseException().Message}");
                e.SetObserved();
                return;
            }

            Log.Msg($"UNOBSERVED TASK EXCEPTION:\n{e.Exception}");
            e.SetObserved();
        };

        InstallNativeCrashHandler();

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();
        TopBarRaycastPortalPatch.Install(harmony);

        AudioCapture.LogHandler = Msg;
        PrewarmSharedResources();

        if (IsMediaMtxEnabled)
        {
            Msg("[MediaMTX] Explicit RTSP mode enabled; built-in Cloudflare HTTP stream will not start");
        }
        else
        {
            try
            {
                StreamServer = new MjpegServer(STREAM_PORT);
                StreamServer.Start();
                Msg($"Stream server started on port {STREAM_PORT}");
                if (UseCloudflareTunnel)
                    System.Threading.Tasks.Task.Run(() => StartTunnel());
                else
                {
                    Msg($"[PortForward] Built-in stream available at {GetBuiltInStreamBaseUrl() ?? "(no host configured)"}");
                    ApplyPortForwardNatMapping();
                }
            }
            catch (Exception ex)
            {
                Msg($"Stream server failed to start: {ex.Message}");
                StreamServer = null;
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            var resetPids = new HashSet<uint>();
            foreach (var session in ActiveSessions)
            {
                if (session.OwnsAudioRedirect && session.ProcessId != 0 && resetPids.Add(session.ProcessId))
                    AudioRouter.ResetProcessToDefault(session.ProcessId);
            }
            KillTunnel();
            RemovePortForwardNatMapping();
            try { StreamServer?.Dispose(); } catch { }
        };

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (SoftCamSetup.IsRegistered())
                {
                    VCam = new VirtualCamera();
                    VCam.StartIdle();
                }
                else
                {
                    Msg("[VirtualCamera] DirectShow filter not registered, virtual camera unavailable");
                }
            }
            catch (Exception ex) { Msg($"[VirtualCamera] Setup error: {ex.Message}"); }

            try
            {
                if (!VBCableSetup.IsInstalled())
                    Msg("[VirtualMic] VB-Cable not installed, virtual mic unavailable");
            }
            catch (Exception ex) { Msg($"[VirtualMic] Setup error: {ex.Message}"); }
        });

        _windowPollerRunning = true;
        _windowPollerThread = new Thread(WindowPollerLoop)
        { Name = "DesktopBuddy:WindowPoller", IsBackground = true };
        _windowPollerThread.Start();

        Msg("DesktopBuddy initialized!");

        OpenSharedTextureBridge();
    }

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
    }

    internal static string NormalizeViewerCullingMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "distance" ? "distance" : "frustum";
    }

    internal static string NormalizeEncoderPreference(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value switch
        {
            "hevc_nvenc" or "h264_nvenc" or
            "hevc_amf" or "h264_amf" or
            "hevc_qsv" or "h264_qsv" or
            "libx264" or "libx265" => value,
            _ => "auto"
        };
    }

    internal static int RuntimeBitrateMbps => Math.Clamp(Volatile.Read(ref _runtimeBitrateMbps), 1, 200);
    internal static int RuntimeStreamFps => Math.Clamp(Volatile.Read(ref _runtimeStreamFps), 1, 240);
    internal static int RuntimeMaxStreamResolution => Math.Clamp(Volatile.Read(ref _runtimeMaxStreamResolution), 128, 8192);
    internal static string RuntimeEncoderPreference => NormalizeEncoderPreference(_runtimeEncoderPreference);

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

    private static void PrewarmSharedResources()
    {
        try { WgcCapture.PrewarmSharedDevice(); }
        catch (Exception ex) { Msg($"[Startup] WGC shared device prewarm failed: {ex.Message}"); }

        try { WgcCapture.PrewarmCaptureFactory(); }
        catch (Exception ex) { Msg($"[Startup] WGC capture factory prewarm failed: {ex.Message}"); }

        try { FfmpegEncoder.SetFfmpegPath(); }
        catch (Exception ex) { Msg($"[Startup] FFmpeg prewarm failed: {ex.Message}"); }

        try { FfmpegEncoder.PrewarmHardwareEncoder(WgcCapture.SharedD3dDevice, WgcCapture.SharedD3dContextLock); }
        catch (Exception ex) { Msg($"[Startup] FFmpeg hardware encoder prewarm failed: {ex.Message}"); }

        try { WindowInput.PrewarmTouchInjection(); }
        catch (Exception ex) { Msg($"[Startup] Touch injection prewarm failed: {ex.Message}"); }

        try { DesktopTextureProviderPatch.PrewarmReflection(); }
        catch (Exception ex) { Msg($"[Startup] DesktopTexture reflection prewarm failed: {ex.Message}"); }

        try { AudioRouter.PrewarmFactory(); }
        catch (Exception ex) { Msg($"[Startup] Audio router prewarm failed: {ex.Message}"); }
    }

    private static void OpenSharedTextureBridge()
    {
        if (_textureBridgeOpened) return;

        try
        {
            TextureBridgeChannel = new SharedTextureBridgeChannel();
            TextureBridgeChannel.Open();
            _textureBridgeOpened = true;
            Msg("[SharedTextureBridge] Opened successfully");
        }
        catch (Exception ex)
        {
            Msg($"[SharedTextureBridge] Error: {ex}");
        }
    }

    internal new static void Msg(string msg) => Log.Msg(msg);
    internal new static void Error(string msg) => Log.Error(msg);

    internal static void RegisterTopBarRaycastPortal(Slot portalSlot, Slot targetRoot)
    {
        if (portalSlot == null || targetRoot == null)
            return;

        RefID portalId = portalSlot.ReferenceID;
        lock (TopBarRaycastGate)
            TopBarRaycastTargets[portalId] = targetRoot;

        portalSlot.Destroyed += _ => UnregisterTopBarRaycastPortal(portalId);
        targetRoot.Destroyed += _ => UnregisterTopBarRaycastPortal(portalId);
        Msg($"[TopBarRaycast] Registered portal={portalId} target={targetRoot.ReferenceID}");
    }

    internal static Slot GetTopBarRaycastTarget(Slot portalSlot)
    {
        if (portalSlot == null)
            return null;

        RefID portalId = portalSlot.ReferenceID;
        lock (TopBarRaycastGate)
        {
            if (!TopBarRaycastTargets.TryGetValue(portalId, out Slot target) || target == null || target.IsDestroyed)
            {
                TopBarRaycastTargets.Remove(portalId);
                return null;
            }

            return target;
        }
    }

    private static void UnregisterTopBarRaycastPortal(RefID portalId)
    {
        lock (TopBarRaycastGate)
            TopBarRaycastTargets.Remove(portalId);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnhandledExceptionFilterDelegate(IntPtr exceptionPointers);

    private static UnhandledExceptionFilterDelegate _nativeCrashDelegate;
    private static IntPtr _previousFilter;

    private static void InstallNativeCrashHandler()
    {
        try
        {
            _nativeCrashDelegate = NativeCrashFilter;
            IntPtr fp = Marshal.GetFunctionPointerForDelegate(_nativeCrashDelegate);
            _previousFilter = SetUnhandledExceptionFilter(fp);
            Log.Msg("[NativeCrash] Handler installed");
        }
        catch (Exception ex)
        {
            Log.Msg($"[NativeCrash] Failed to install handler: {ex.Message}");
        }
    }

    private static int NativeCrashFilter(IntPtr exceptionPointersPtr)
    {
        try
        {
            IntPtr recordPtr = Marshal.ReadIntPtr(exceptionPointersPtr, 0);
            uint code = (uint)Marshal.ReadInt32(recordPtr, 0);
            IntPtr address = Marshal.ReadIntPtr(recordPtr, IntPtr.Size == 8 ? 24 : 12);

            string msg = $"[NativeCrash] FATAL: code=0x{code:X8} addr=0x{address:X}\n";

            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule mod in proc.Modules)
                {
                    long modBase = mod.BaseAddress.ToInt64();
                    long modEnd = modBase + mod.ModuleMemorySize;
                    if (address.ToInt64() >= modBase && address.ToInt64() < modEnd)
                    {
                        long offset = address.ToInt64() - modBase;
                        msg += $"[NativeCrash] Faulting module: {mod.ModuleName}+0x{offset:X} ({mod.FileName})\n";
                        break;
                    }
                }
            }
            catch { }

            try
            {
                msg += $"[NativeCrash] Managed stack:\n{Environment.StackTrace}\n";
            }
            catch { }

            Log.Msg(msg);
        }
        catch
        {
            try { Log.Msg("[NativeCrash] FATAL: crash handler failed to log details"); } catch { }
        }

        return 0;
    }
}

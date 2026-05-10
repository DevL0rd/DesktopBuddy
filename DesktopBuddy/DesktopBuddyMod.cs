using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
using DesktopBuddy.Networking.Rtsp;

namespace DesktopBuddy;

public partial class DesktopBuddyMod : ResoniteMod
{
    public override string Name => "DesktopBuddy";
    public override string Author => "DevL0rd";
    public override string Version => "1.0.10";
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

    private static readonly Version CurrentConfigSchemaVersion = new(1, 0, 10);
    internal static ModConfiguration? Config;
    private static bool _configResetForNewDefaults;

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
    internal static readonly ModConfigurationKey<int> KeyframeIntervalMs =
        new("keyframeIntervalMs", "Maximum time between forced video keyframes in milliseconds. Lower reduces stream startup/catch-up latency but costs bitrate/quality.", () => 1000);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> StreamFps =
        new("streamFps", "Nominal stream FPS for encoder timing. Capture remains event-driven and is not frame-capped.", () => 60);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> MaxStreamResolution =
        new("maxStreamResolution", "Maximum encoded stream long-edge resolution. 2560 is 2K/QHD.", () => 2560);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> LibVlcNetworkCachingMs =
        new("libVlcNetworkCachingMs", "libVLC network cache in milliseconds for DesktopBuddy streams.", () => 200);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> LibVlcLiveCachingMs =
        new("libVlcLiveCachingMs", "libVLC live cache in milliseconds for DesktopBuddy streams.", () => 200);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> LibVlcFileCachingMs =
        new("libVlcFileCachingMs", "libVLC file cache in milliseconds for DesktopBuddy streams.", () => 100);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> IpAddress =
        new("ip_address", "Public IP address or hostname used in RTSP stream URLs. Use auto to detect it.", () => "auto");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> RtspPort =
        new("rtsp_port", "Embedded DesktopBuddy RTSP server TCP port.", () => 8554);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> AutoPortForward =
        new("auto_port_forward", "Try to automatically forward the RTSP TCP port on supported routers.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> UseTunnel =
        new("use_tunnel", "Use a TCP tunnel instead of advertising the direct public IP/port-forward endpoint.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> TunnelProvider =
        new("tunnel_provider", "TCP tunnel provider. Currently supported: pinggy.", () => "pinggy");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PinggySshPath =
        new("pinggy_ssh_path", "OpenSSH executable used for Pinggy tunnels.", () => "ssh");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PinggyServer =
        new("pinggy_server", "Pinggy SSH server host. Free/default is a.pinggy.io; pro accounts may use pro.pinggy.io.", () => "a.pinggy.io");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PinggyToken =
        new("pinggy_token", "Optional Pinggy account token for reserved ports, custom domains, and pro features.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> PinggyRemotePort =
        new("pinggy_remote_port", "Pinggy remote TCP port. Use 0 for Pinggy to assign a free/random port.", () => 0);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> PinggyListenAddress =
        new("pinggy_listen_address", "Advanced Pinggy listen address override, e.g. tcp//example.com/34567. Leave blank for normal TCP forwarding.", () => "");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> PinggyForceExisting =
        new("pinggy_force_existing", "When using a token, ask Pinggy to disconnect an existing tunnel with the same token before connecting.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<string> RtspTransport =
        new("rtsp_transport", "RTSP transport mode. DesktopBuddy currently serves TCP interleaved RTP.", () => "tcp");

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> UseMediaMtx =
        new("useMediaMtx", "Use an external MediaMTX server for streaming instead of DesktopBuddy's embedded RTSP server.", () => false);

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
    internal static readonly ModConfigurationKey<string> PanelCurvePreferences =
        new("panelCurvePreferences", "Saved DesktopBuddy panel curve values, keyed by application executable path or shared desktop capture.", () => "");

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

    internal static Uri GetEmbeddedRtspUri(int streamId)
    {
        if (RemoteRtspServer == null)
            throw new InvalidOperationException("Embedded RTSP server is not running");

        return RemoteRtspServer.GetStreamUri(streamId);
    }

    private static void StartConfiguredTunnel(int rtspPort)
    {
        string provider = Config.GetValue(TunnelProvider)?.Trim();
        if (!string.Equals(provider, "pinggy", StringComparison.OrdinalIgnoreCase))
        {
            Msg($"[Tunnel] Unsupported tunnel_provider '{provider}'. Embedded RTSP will keep using the direct endpoint.");
            return;
        }

        try
        {
            var options = new PinggyTunnelOptions
            {
                SshPath = Config.GetValue(PinggySshPath),
                Server = Config.GetValue(PinggyServer),
                Token = Config.GetValue(PinggyToken),
                RemotePort = Math.Clamp(Config.GetValue(PinggyRemotePort), 0, 65535),
                ListenAddress = Config.GetValue(PinggyListenAddress),
                ForceExisting = Config.GetValue(PinggyForceExisting),
                Mode = "tcp",
                SshPort = 443
            };

            _pinggyTunnel?.Dispose();
            _pinggyTunnel = new PinggyTunnelManager(rtspPort, options, (host, port) =>
            {
                try
                {
                    HandleRtspPublicEndpointUpdated(host, port, "Pinggy");
                }
                catch (Exception ex)
                {
                    Msg($"[Pinggy] Failed to apply tunnel endpoint {host}:{port}: {ex.Message}");
                }
            });
            _pinggyTunnel.Start();
            Msg("[Tunnel] Pinggy mode enabled. Waiting for Pinggy to report the public TCP endpoint.");
        }
        catch (Exception ex)
        {
            Msg($"[Tunnel] Failed to start Pinggy tunnel: {ex.Message}");
        }
    }

    private static void HandleRtspPublicEndpointUpdated(string host, int port, string source)
    {
        RemoteRtspServer?.UpdatePublicEndpoint(host, port, source);
        RefreshEmbeddedRtspUrls($"{source} endpoint update");
    }

    private static void RefreshEmbeddedRtspUrls(string reason)
    {
        DesktopSession[] sessions;
        try { sessions = ActiveSessions.ToArray(); }
        catch (Exception ex)
        {
            Msg($"[RTSP] Could not snapshot sessions for URL refresh: {ex.Message}");
            return;
        }

        lock (_sharedStreams)
        {
            foreach (var shared in _sharedStreams.Values)
            {
                if (shared.StreamId <= 0) continue;
                try { shared.StreamUrl = GetEmbeddedRtspUri(shared.StreamId); }
                catch { }
            }
        }

        int refreshed = 0;
        foreach (var session in sessions)
        {
            if (session == null || session.Cleaned || session.StreamId <= 0)
                continue;

            VideoTextureProvider videoTexture = session.VideoTexture;
            Slot root = session.Root;
            if (videoTexture == null || videoTexture.IsDestroyed || root == null || root.IsDestroyed)
                continue;

            Uri newUrl;
            try { newUrl = GetEmbeddedRtspUri(session.StreamId); }
            catch (Exception ex)
            {
                Msg($"[RTSP] Could not build refreshed URL for stream {session.StreamId}: {ex.Message}");
                continue;
            }

            var oldUrl = videoTexture.URL.Value;
            if (oldUrl == null)
            {
                Msg($"[RTSP] Stream {session.StreamId} URL refresh skipped because provider is disconnected/private ({reason})");
                continue;
            }

            if (string.Equals(oldUrl.ToString(), newUrl.ToString(), StringComparison.OrdinalIgnoreCase))
                continue;

            refreshed++;
            root.World.RunInUpdates(0, () =>
            {
                if (videoTexture == null || videoTexture.IsDestroyed || root == null || root.IsDestroyed)
                    return;

                Uri currentUrl = videoTexture.URL.Value;
                if (currentUrl == null)
                    return;

                if (string.Equals(currentUrl.ToString(), newUrl.ToString(), StringComparison.OrdinalIgnoreCase))
                    return;

                Msg($"[RTSP] Refreshing stream {session.StreamId} URL after {reason}: {currentUrl} -> {newUrl}");
                videoTexture.URL.Value = null;
                try { videoTexture.Stop(); } catch { }

                root.World.RunInUpdates(10, () =>
                {
                    if (videoTexture == null || videoTexture.IsDestroyed || root == null || root.IsDestroyed)
                        return;

                    videoTexture.URL.Value = newUrl;
                    videoTexture.Play();
                    Msg($"[RTSP] Stream {session.StreamId} URL refreshed: {newUrl}");
                });
            });
        }

        if (refreshed > 0)
            Msg($"[RTSP] Scheduled URL refresh for {refreshed} active stream(s) after {reason}");
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

    internal static RtspServer? RemoteRtspServer;
    private static PinggyTunnelManager? _pinggyTunnel;
    internal static VirtualCamera VCam;
    internal static VirtualMic VMic;
    internal static readonly PerfTimer Perf = new();

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
    private static bool _updateShown;

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

        AudioCapture.LogHandler = Msg;
        PrewarmSharedResources();

        if (IsMediaMtxEnabled)
        {
            Msg($"[MediaMTX] Explicit RTSP mode enabled; embedded RTSP server will not start");
        }
        else
        {
            try
            {
                int rtspPort = Math.Clamp(Config.GetValue(RtspPort), 1, 65535);
                string publicHost = RtspEndpointResolver.ResolvePublicHost(Config.GetValue(IpAddress));
                RemoteRtspServer = new RtspServer(rtspPort, publicHost);
                RemoteRtspServer.Start();
                if (Config.GetValue(UseTunnel))
                {
                    StartConfiguredTunnel(rtspPort);
                }
                else if (Config.GetValue(AutoPortForward))
                {
                    RtspEndpointResolver.TryAutoPortForward(rtspPort);
                }
                Msg($"[RTSP] Embedded server ready: {RemoteRtspServer.GetStreamUri(0).ToString().Replace("/stream/0", "/stream/{streamId}")} transport={Config.GetValue(RtspTransport)} tunnel={Config.GetValue(UseTunnel)}");
            }
            catch (Exception ex)
            {
                Msg($"[RTSP] Embedded server failed to start: {ex.Message}");
                try { _pinggyTunnel?.Dispose(); } catch { }
                _pinggyTunnel = null;
                RemoteRtspServer = null;
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
            try { _pinggyTunnel?.Dispose(); } catch { }
            try { RemoteRtspServer?.Dispose(); } catch { }
            RtspEndpointResolver.ReleaseAutoPortForward();
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
                Msg("[Config] Applied 1.0.10 fresh defaults: spatialAudio=false bitrate=10 streamFps=60 keyframeIntervalMs=1000 maxStreamResolution=2560 rtsp_port=8554 use_tunnel=true");
            }
            else
            {
                Config.Set(SpatialAudioEnabled, Config.GetValue(SpatialAudioEnabled));
                Config.Set(Bitrate, Config.GetValue(Bitrate));
                Config.Set(KeyframeIntervalMs, Config.GetValue(KeyframeIntervalMs));
                Config.Set(StreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
                Config.Set(MaxStreamResolution, Math.Clamp(Config.GetValue(MaxStreamResolution), 128, 8192));
            }

            Config.Set(CheckForUpdates, Config.GetValue(CheckForUpdates));
            Config.Set(StreamFps, Math.Clamp(Config.GetValue(StreamFps), 1, 240));
            Config.Set(LibVlcNetworkCachingMs, Math.Max(200, Config.GetValue(LibVlcNetworkCachingMs)));
            Config.Set(LibVlcLiveCachingMs, Math.Max(200, Config.GetValue(LibVlcLiveCachingMs)));
            Config.Set(LibVlcFileCachingMs, Config.GetValue(LibVlcFileCachingMs));
            Config.Set(IpAddress, string.IsNullOrWhiteSpace(Config.GetValue(IpAddress)) ? "auto" : Config.GetValue(IpAddress));
            Config.Set(RtspPort, Math.Clamp(Config.GetValue(RtspPort), 1, 65535));
            Config.Set(AutoPortForward, Config.GetValue(AutoPortForward));
            Config.Set(UseTunnel, Config.GetValue(UseTunnel));
            Config.Set(TunnelProvider, string.IsNullOrWhiteSpace(Config.GetValue(TunnelProvider)) ? "pinggy" : Config.GetValue(TunnelProvider));
            Config.Set(PinggySshPath, string.IsNullOrWhiteSpace(Config.GetValue(PinggySshPath)) ? "ssh" : Config.GetValue(PinggySshPath));
            Config.Set(PinggyServer, string.IsNullOrWhiteSpace(Config.GetValue(PinggyServer)) ? "a.pinggy.io" : Config.GetValue(PinggyServer));
            Config.Set(PinggyToken, Config.GetValue(PinggyToken) ?? "");
            Config.Set(PinggyRemotePort, Math.Clamp(Config.GetValue(PinggyRemotePort), 0, 65535));
            Config.Set(PinggyListenAddress, Config.GetValue(PinggyListenAddress) ?? "");
            Config.Set(PinggyForceExisting, Config.GetValue(PinggyForceExisting));
            Config.Set(RtspTransport, string.IsNullOrWhiteSpace(Config.GetValue(RtspTransport)) ? "tcp" : Config.GetValue(RtspTransport));
            Config.Set(UseMediaMtx, Config.GetValue(UseMediaMtx));
            Config.Set(MediaMtxHost, Config.GetValue(MediaMtxHost));
            Config.Set(MediaMtxPort, Config.GetValue(MediaMtxPort));
            Config.Set(MediaMtxStreamName, Config.GetValue(MediaMtxStreamName));
            Config.Set(PanelCurvePreferences, Config.GetValue(PanelCurvePreferences) ?? "");
            Config.Save();
            _configResetForNewDefaults = false;
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
        Config.Set(KeyframeIntervalMs, 1000);
        Config.Set(StreamFps, 60);
        Config.Set(MaxStreamResolution, 2560);
        Config.Set(LibVlcNetworkCachingMs, 200);
        Config.Set(LibVlcLiveCachingMs, 200);
        Config.Set(LibVlcFileCachingMs, 100);
        Config.Set(IpAddress, "auto");
        Config.Set(RtspPort, 8554);
        Config.Set(AutoPortForward, true);
        Config.Set(UseTunnel, true);
        Config.Set(TunnelProvider, "pinggy");
        Config.Set(PinggySshPath, "ssh");
        Config.Set(PinggyServer, "a.pinggy.io");
        Config.Set(PinggyToken, "");
        Config.Set(PinggyRemotePort, 0);
        Config.Set(PinggyListenAddress, "");
        Config.Set(PinggyForceExisting, true);
        Config.Set(RtspTransport, "tcp");
        Config.Set(UseMediaMtx, false);
        Config.Set(MediaMtxHost, "");
        Config.Set(MediaMtxPort, 8554);
        Config.Set(MediaMtxStreamName, "");
        Config.Set(PanelCurvePreferences, "");
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

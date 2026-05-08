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

namespace DesktopBuddy;

public partial class DesktopBuddyMod : ResoniteMod
{
    public override string Name => "DesktopBuddy";
    public override string Author => "DevL0rd";
    public override string Version => "1.0.6";
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

    private static readonly Version CurrentConfigSchemaVersion = new(1, 0, 6);
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
    internal static readonly ModConfigurationKey<bool> UseMediaMtx =
        new("useMediaMtx", "Use an external MediaMTX server for streaming instead of the built-in cloudflared tunnel.", () => false);

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

    internal static MjpegServer? StreamServer;
    internal static VirtualCamera VCam;
    internal static VirtualMic VMic;
    private const int STREAM_PORT = 48080;
    internal static string? TunnelUrl;
    private static Process _tunnelProcess;
    private static string _cfPath;
    private static volatile bool _tunnelRestarting;
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
            Msg($"[MediaMTX] RTSP mode enabled, skipping local stream server and cloudflared tunnel");
        }
        else
        {
            try
            {
                StreamServer = new MjpegServer(STREAM_PORT);
                StreamServer.Start();
                Msg($"Stream server started on port {STREAM_PORT}");
            }
            catch (Exception ex)
            {
                Msg($"Stream server failed to start: {ex.Message}");
                StreamServer = null;
            }

            if (StreamServer != null)
            {
                System.Threading.Tasks.Task.Run(() => StartTunnel());
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
                Msg("[Config] Applied 1.0.6 fresh defaults: spatialAudio=false bitrate=10 keyframeIntervalMs=1000 maxStreamResolution=2560");
            }
            else
            {
                Config.Set(SpatialAudioEnabled, Config.GetValue(SpatialAudioEnabled));
                Config.Set(Bitrate, Config.GetValue(Bitrate));
                Config.Set(KeyframeIntervalMs, Config.GetValue(KeyframeIntervalMs));
                Config.Set(MaxStreamResolution, Math.Clamp(Config.GetValue(MaxStreamResolution), 128, 8192));
            }

            Config.Set(CheckForUpdates, Config.GetValue(CheckForUpdates));
            Config.Set(LibVlcNetworkCachingMs, Math.Max(200, Config.GetValue(LibVlcNetworkCachingMs)));
            Config.Set(LibVlcLiveCachingMs, Math.Max(200, Config.GetValue(LibVlcLiveCachingMs)));
            Config.Set(LibVlcFileCachingMs, Config.GetValue(LibVlcFileCachingMs));
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
        Config.Set(MaxStreamResolution, 2560);
        Config.Set(LibVlcNetworkCachingMs, 200);
        Config.Set(LibVlcLiveCachingMs, 200);
        Config.Set(LibVlcFileCachingMs, 100);
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

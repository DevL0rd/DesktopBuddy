using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public override string Version => "1.0.0";
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

    internal static ModConfiguration? Config;
    internal const int MinChildCaptureWidth = 128;
    internal const int MinChildCaptureHeight = 128;

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> FrameRate =
        new("frameRate", "Target capture frame rate", () => 30);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ImmediateGC =
        new("immediate_gc", "Force garbage collection on dispose", () => false);

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

    internal static CaptureSessionChannel? CaptureChannel;
    private static bool _captureChannelOpened;

    internal static readonly System.Collections.Generic.HashSet<DesktopTextureProvider> OurProviders = new();

    private static Thread _windowPollerThread;
    private static volatile bool _windowPollerRunning;
    internal static readonly ConcurrentQueue<WindowEvent> _windowEvents = new();

    internal struct WindowEvent
    {
        public DesktopSession Session;
        public IntPtr ChildHwnd;
        public string Title;
        public WindowEventType EventType;
    }
    internal enum WindowEventType { NewChild, ChildClosed, TitleChanged }

    private static string _latestVersion;
    private static bool _updateShown;

    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Config!.Save(true);

        Log.StartSession();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Msg($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}):\n{e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Msg($"UNOBSERVED TASK EXCEPTION:\n{e.Exception}");
        };

        InstallNativeCrashHandler();

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();

        AudioCapture.LogHandler = Msg;

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

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Thread.Sleep(5000);
                OpenCaptureChannel();
            }
            catch (Exception ex)
            {
                Msg($"[CaptureChannel] Failed to open: {ex.Message}");
            }
        });
    }

    private static void OpenCaptureChannel()
    {
        if (_captureChannelOpened) return;

        try
        {
            var engine = FrooxEngine.Engine.Current;
            if (engine == null) { Msg("[CaptureChannel] Engine.Current is null"); return; }

            var renderSystem = engine.RenderSystem;
            if (renderSystem == null) { Msg("[CaptureChannel] RenderSystem is null"); return; }

            var hostField = renderSystem.GetType().GetField("_messagingHost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (hostField == null) { Msg("[CaptureChannel] _messagingHost field not found"); return; }

            var host = hostField.GetValue(renderSystem);
            if (host == null) { Msg("[CaptureChannel] _messagingHost is null (renderer not started?)"); return; }

            var queueNameProp = host.GetType().GetProperty("QueueName");
            if (queueNameProp == null) { Msg("[CaptureChannel] QueueName property not found"); return; }

            var queueName = (string)queueNameProp.GetValue(host);
            if (string.IsNullOrEmpty(queueName)) { Msg("[CaptureChannel] QueueName is empty"); return; }

            CaptureChannel = new CaptureSessionChannel();
            CaptureChannel.Open(queueName);
            _captureChannelOpened = true;
            Msg($"[CaptureChannel] Opened successfully (queueName={queueName})");
        }
        catch (Exception ex)
        {
            Msg($"[CaptureChannel] Error: {ex}");
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

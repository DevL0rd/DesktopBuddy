using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using HarmonyLib;

namespace DesktopBuddy;

// ReSharper disable once ClassNeverInstantiated.Global - constructed by BepInEx.
[ResonitePlugin(PluginGuid, PluginName, DesktopBuddyVersion, PluginAuthor, PluginUrl)]
[BepInPlugin(PluginGuid, PluginName, DesktopBuddyVersion)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public partial class DesktopBuddyMod : BasePlugin
{
    internal const string PluginGuid = "com.devl0rd.DesktopBuddy";
    internal const string PluginName = "DesktopBuddy";
    internal const string PluginAuthor = "DevL0rd";
    internal const string PluginUrl = "https://github.com/DevL0rd/DesktopBuddy";
    internal const string DesktopBuddyVersion = DesktopBuddyVersionInfo.Version;

    internal static ManualLogSource PluginLog { get; private set; }
    private static int _initialized;
    private static int _dependencyRuntimeStarted;

    public override void Load()
    {
        PluginLog = base.Log;
        DesktopBuddy.Log.SetLogger(PluginLog);
        InstallManagedDependencyResolver();
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Msg($"Plugin {PluginGuid} loaded; waiting for Resonite engine");
    }

    private void OnEngineReady()
    {
        DesktopBuddy.Log.StartSession();
        var setupState = DesktopBuddyFirstRunSetup.Check();
        if (setupState.HasIssues)
            Msg("[Setup] Local setup has missing or outdated items; dependency runtime deferred until setup notice is dismissed");

        InitializeCore();
        if (!setupState.HasIssues)
            EnsureDependencyRuntimeStarted();
    }

    private void InitializeCore()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        Config = new DesktopBuddyConfig(base.Config);
        BindConfigKeys();
        SaveCurrentConfigDefaults();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            DesktopBuddy.Log.Msg($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}):\n{e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            DesktopBuddy.Log.Msg($"UNOBSERVED TASK EXCEPTION:\n{e.Exception}");
            e.SetObserved();
        };

        InstallNativeCrashHandler();

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();
        TopBarRaycastPortalPatch.Install(harmony);

        AudioCapture.LogHandler = Msg;
        RegisterShutdownCleanup();

        Msg("DesktopBuddy core initialized!");
    }

    internal static void EnsureDependencyRuntimeStarted()
    {
        if (Interlocked.Exchange(ref _dependencyRuntimeStarted, 1) == 1)
            return;

        Msg("[Startup] Starting DesktopBuddy dependency runtime");
        PrewarmSharedResources();

        if (IsMediaMtxEnabled)
        {
            Msg("[MediaMTX] Explicit RTSP mode enabled; built-in Cloudflare HTTP stream will not start");
        }
        else
        {
            try
            {
                StreamServer = new BuiltInStreamServer(STREAM_PORT);
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

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (SoftCam.IsFilterRegistered())
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
                if (!VBCable.HasCableInputDevice())
                    Msg("[VirtualMic] VB-Cable not installed, virtual mic unavailable");
            }
            catch (Exception ex) { Msg($"[VirtualMic] Setup error: {ex.Message}"); }
        });

        _windowPollerRunning = true;
        _windowPollerThread = new Thread(WindowPollerLoop)
        { Name = "DesktopBuddy:WindowPoller", IsBackground = true };
        _windowPollerThread.Start();

        Msg("DesktopBuddy dependency runtime initialized!");

        OpenSharedTextureBridge();
    }

    private static void RegisterShutdownCleanup()
    {
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
    }
}

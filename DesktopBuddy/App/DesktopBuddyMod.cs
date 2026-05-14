using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using ResoniteModLoader;

namespace DesktopBuddy;

public partial class DesktopBuddyMod : ResoniteMod
{

    public override string Name => "DesktopBuddy";
    public override string Author => "DevL0rd";
    internal const string DesktopBuddyVersion = "1.0.12";
    public override string Version => DesktopBuddyVersion;
    public override string Link => "https://github.com/DevL0rd/DesktopBuddy";

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
        InstallManagedDependencyResolver();
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

        Msg("DesktopBuddy initialized!");

        OpenSharedTextureBridge();
    }
}

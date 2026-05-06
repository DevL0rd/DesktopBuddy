using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;

namespace DesktopBuddyRenderer
{
    [BepInPlugin("net.desktopbuddy.renderer", "DesktopBuddyRenderer", "1.0.0")]
    public class DesktopBuddyRendererPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private CaptureSessionManager _sessionManager;

        private void Awake()
        {
            RendererDiagnostics.Log("Awake entered");
            Log = Logger;
            LogInfo("DesktopBuddyRenderer starting...");

            try
            {
                new Harmony("net.desktopbuddy.renderer").PatchAll();
                LogInfo("Harmony patches applied");
            }
            catch (Exception ex)
            {
                LogError("Harmony PatchAll failed", ex);
                throw;
            }

            try
            {
                _sessionManager = new CaptureSessionManager(Log);
                LogInfo("CaptureSessionManager created");
            }
            catch (Exception ex)
            {
                LogError("CaptureSessionManager creation failed", ex);
                throw;
            }

            TryInitializeWgcDevice();
            WgcDisplaySource.PreloadNativeHelper();
            LogInfo("DesktopBuddyRenderer ready");
        }

        private void Update()
        {
            try
            {
                _sessionManager?.Update();
            }
            catch (Exception ex)
            {
                LogError("Update failed", ex);
            }
        }

        private void OnDestroy()
        {
            RendererDiagnostics.Log("OnDestroy entered");
            _sessionManager?.Dispose();
            TryDisposeWgcDevice();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TryInitializeWgcDevice()
        {
            try
            {
                bool ready = RendererWgcDevice.Initialize(Log);
                LogInfo($"RendererWgcDevice.Initialize returned {ready}");
            }
            catch (Exception ex)
            {
                LogError("RendererWgcDevice.Initialize failed", ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TryDisposeWgcDevice()
        {
            try
            {
                RendererWgcDevice.Dispose();
                RendererDiagnostics.Log("RendererWgcDevice disposed");
            }
            catch (Exception ex)
            {
                LogError("RendererWgcDevice.Dispose failed", ex);
            }
        }

        internal static void LogInfo(string message)
        {
            RendererDiagnostics.Log(message);
            Log?.LogInfo(message);
        }

        internal static void LogWarning(string message)
        {
            RendererDiagnostics.Log("WARN " + message);
            Log?.LogWarning(message);
        }

        internal static void LogError(string message, Exception ex)
        {
            RendererDiagnostics.LogException("ERROR " + message, ex);
            Log?.LogError($"{message}: {ex}");
        }
    }
}

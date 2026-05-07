using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Diagnostics;

namespace DesktopBuddySharedTextureBridge
{
    [BepInPlugin("net.desktopbuddy.sharedtexturebridge", "DesktopBuddySharedTextureBridge", "1.0.0")]
    public class SharedTextureBridgePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private SharedTextureBridge _bridge;
        private float _resourceLogTimer;

        private void Awake()
        {
            Log = Logger;
            LogInfo("DesktopBuddySharedTextureBridge starting...");

            try
            {
                new Harmony("net.desktopbuddy.sharedtexturebridge").PatchAll();
                LogInfo("Harmony patches applied");
            }
            catch (Exception ex)
            {
                LogError("Harmony PatchAll failed", ex);
                throw;
            }

            try
            {
                _bridge = new SharedTextureBridge(Log);
                LogInfo("SharedTextureBridge created");
            }
            catch (Exception ex)
            {
                LogError("SharedTextureBridge creation failed", ex);
                throw;
            }

            LogInfo("DesktopBuddySharedTextureBridge ready");
        }

        private void Update()
        {
            try
            {
                _bridge?.Update();
                _resourceLogTimer += UnityEngine.Time.unscaledDeltaTime;
                if (_resourceLogTimer >= 2f)
                {
                    _resourceLogTimer = 0f;
                    LogResources();
                }
            }
            catch (Exception ex)
            {
                LogError("Update failed", ex);
            }
        }

        private void OnDestroy()
        {
            _bridge?.Dispose();
            UnityD3D11Device.Dispose();
        }

        internal static void LogInfo(string message)
        {
            Log?.LogInfo(message);
        }

        internal static void LogWarning(string message)
        {
            Log?.LogWarning(message);
        }

        internal static void LogError(string message, Exception ex)
        {
            Log?.LogError($"{message}: {ex}");
        }

        private void LogResources()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                process.Refresh();
                double privateMb = process.PrivateMemorySize64 / 1048576.0;
                double workingMb = process.WorkingSet64 / 1048576.0;
                double managedMb = GC.GetTotalMemory(false) / 1048576.0;
                LogInfo($"[Resources] private={privateMb:F1}MB working={workingMb:F1}MB managed={managedMb:F1}MB activeSlots={_bridge?.ActiveSlotCount ?? 0} pendingBinds={_bridge?.PendingBindCount ?? 0} textureRequests={_bridge?.TotalTextureRequestCount ?? 0}");
            }
            catch (Exception ex)
            {
                LogWarning($"[Resources] Failed: {ex.Message}");
            }
        }
    }
}

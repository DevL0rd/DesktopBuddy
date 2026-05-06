using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace DesktopBuddyRenderer
{
    [BepInPlugin("net.desktopbuddy.renderer", "DesktopBuddyRenderer", "1.0.0")]
    public class DesktopBuddyRendererPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private CaptureSessionManager _sessionManager;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("DesktopBuddyRenderer starting...");

            _sessionManager = new CaptureSessionManager(Log);
            new Harmony("net.desktopbuddy.renderer").PatchAll();
            Log.LogInfo("DesktopBuddyRenderer ready");
        }

        private void Update() => _sessionManager?.Update();

        private void OnDestroy() => _sessionManager?.Dispose();
    }
}

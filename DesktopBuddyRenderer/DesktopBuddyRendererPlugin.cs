using System;
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

            var queuePrefix = ParseQueuePrefix();
            if (queuePrefix == null)
            {
                Log.LogWarning("Could not parse QueueName from command line — side-channel disabled");
                return;
            }

            _sessionManager = new CaptureSessionManager(queuePrefix, Log);
            new Harmony("net.desktopbuddy.renderer").PatchAll();
            Log.LogInfo($"DesktopBuddyRenderer ready, queue prefix: {queuePrefix}");
        }

        private void Update() => _sessionManager?.Update();

        private void OnDestroy() => _sessionManager?.Dispose();

        private static string ParseQueuePrefix()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-QueueName", StringComparison.OrdinalIgnoreCase))
                {
                    var name = args[i + 1];
                    return name.EndsWith("Primary", StringComparison.OrdinalIgnoreCase)
                        ? name.Substring(0, name.Length - 7)
                        : name;
                }
            }
            return null;
        }
    }
}

using DesktopBuddy.Shared;
using System.Collections.Generic;
using HarmonyLib;
using Renderite.Unity;

namespace DesktopBuddyRenderer
{
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class DisplayTextureInjector
    {
        private static readonly Dictionary<int, int> RequestCounts = new Dictionary<int, int>();

        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            if (index < CaptureSessionProtocol.MagicIndexBase) return;

            RequestCounts.TryGetValue(index, out int count);
            count++;
            RequestCounts[index] = count;
            bool logThis = count <= 5 || count % 120 == 0;

            var source = CaptureSessionManager.GetSourceForIndex(index);
            if (source != null)
            {
                __result = source;
                if (logThis)
                    DesktopBuddyRendererPlugin.LogInfo(
                        $"[DisplayTextureInjector] index={index} request={count} -> {source.SourceName} " +
                        $"(IsValid={source.IsValid}, texture={(source.UnityTexture != null ? "ready" : "null")}, {source.Width}x{source.Height})");
            }
            else
            {
                if (logThis)
                    DesktopBuddyRendererPlugin.LogInfo(
                        $"[DisplayTextureInjector] index={index} request={count} -> no source registered");
            }
        }
    }
}

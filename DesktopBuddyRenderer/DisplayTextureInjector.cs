using DesktopBuddy.Shared;
using HarmonyLib;
using Renderite.Unity;

namespace DesktopBuddyRenderer
{
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class DisplayTextureInjector
    {
        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            if (index < CaptureSessionProtocol.MagicIndexBase) return;

            var source = CaptureSessionManager.GetSourceForIndex(index);
            if (source != null)
            {
                __result = source;
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayTextureInjector] index={index} → UwcDisplaySource " +
                    $"(IsValid={source.IsValid}, texture={(source.UnityTexture != null ? "ready" : "null")}, {source.Width}x{source.Height})");
            }
            else
            {
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayTextureInjector] index={index} → no source registered");
            }
        }
    }
}

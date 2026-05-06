using DesktopBuddy.Shared;
using System.Collections.Generic;
using HarmonyLib;
using Renderite.Unity;

namespace DesktopBuddySharedTextureBridge
{
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class SharedTextureIndexPatch
    {
        private static readonly Dictionary<int, int> RequestCounts = new Dictionary<int, int>();

        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            if (index < SharedTextureBridgeProtocol.MagicIndexBase) return;

            RequestCounts.TryGetValue(index, out int count);
            count++;
            RequestCounts[index] = count;
            bool logThis = count <= 5 || count % 120 == 0;

            var textureSlot = SharedTextureBridge.GetSlotForBridgeIndex(index);
            if (textureSlot != null)
            {
                __result = textureSlot;
                if (logThis)
                    SharedTextureBridgePlugin.LogInfo(
                        $"[SharedTextureIndexPatch] index={index} request={count} -> shared texture " +
                        $"(IsValid={textureSlot.IsValid}, texture={(textureSlot.UnityTexture != null ? "ready" : "null")}, {textureSlot.Width}x{textureSlot.Height})");
            }
            else
            {
                if (logThis)
                    SharedTextureBridgePlugin.LogInfo(
                        $"[SharedTextureIndexPatch] index={index} request={count} -> no slot registered");
            }
        }
    }
}

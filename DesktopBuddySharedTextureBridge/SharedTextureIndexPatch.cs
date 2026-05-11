using DesktopBuddy.Shared;
using HarmonyLib;
using Renderite.Unity;

namespace DesktopBuddySharedTextureBridge
{
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class SharedTextureIndexPatch
    {
        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            try
            {
                if (index < SharedTextureBridgeProtocol.MagicIndexBase) return;

                var textureSlot = SharedTextureBridge.GetSlotForBridgeIndex(index);
                if (textureSlot != null)
                    __result = textureSlot;
            }
            catch (System.Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"[SharedTextureIndexPatch] Postfix failed index={index}", ex);
            }
        }
    }
}

using DesktopBuddy.Shared;
using HarmonyLib;
using Renderite.Unity;

namespace DesktopBuddySharedTextureBridge
{
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class SharedTextureIndexPatch
    {
        static bool Prefix(int index, ref IDisplayTextureSource __result)
        {
            try
            {
                if (index < SharedTextureBridgeProtocol.MagicIndexBase) return true;

                var textureSlot = SharedTextureBridge.GetSlotForBridgeIndex(index);
                __result = textureSlot;
                return false;
            }
            catch (System.Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"[SharedTextureIndexPatch] Prefix failed index={index}", ex);
                __result = null;
                return false;
            }
        }
    }
}

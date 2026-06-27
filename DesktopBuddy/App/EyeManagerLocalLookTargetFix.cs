using FrooxEngine;
using FrooxEngine.CommonAvatar;
using HarmonyLib;

namespace DesktopBuddy;

[HarmonyPatch(typeof(EyeManager), "RaycastFilter")]
internal static class EyeManagerLocalLookTargetFix
{
    private static void Postfix(ICollider c, ref bool __result)
    {
        if (__result && c?.Slot?.IsLocalElement == true)
            __result = false;
    }
}

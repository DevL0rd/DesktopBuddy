using System;
using System.Linq;
using System.Reflection;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;

namespace DesktopBuddy;

internal static class TopBarRaycastPortalPatch
{
    private static bool _installed;

    internal static void Install(Harmony harmony)
    {
        if (_installed)
            return;

        try
        {
            MethodInfo target = typeof(MeshUVRaycastPortal)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != nameof(MeshUVRaycastPortal.TransferRay))
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 6
                        && parameters[0].ParameterType == typeof(RaycastHit).MakeByRefType()
                        && parameters[1].ParameterType == typeof(float3).MakeByRefType()
                        && parameters[2].ParameterType == typeof(float3).MakeByRefType()
                        && parameters[3].ParameterType == typeof(float3).MakeByRefType()
                        && parameters[4].ParameterType == typeof(Func<ICollider, int, bool>).MakeByRefType()
                        && parameters[5].ParameterType == typeof(bool).MakeByRefType();
                });

            if (target == null)
            {
                DesktopBuddyMod.Msg("[TopBarRaycast] MeshUVRaycastPortal.TransferRay signature not found; top bar raycast isolation disabled");
                return;
            }

            MethodInfo postfix = typeof(TopBarRaycastPortalPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            _installed = true;
            DesktopBuddyMod.Msg("[TopBarRaycast] Installed runtime portal filter patch");
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[TopBarRaycast] Failed to install portal filter patch: {ex}");
        }
    }

    private static void Postfix(MeshUVRaycastPortal __instance, bool __result, ref Func<ICollider, int, bool> filter)
    {
        if (!__result)
            return;

        try
        {
            Slot targetRoot = DesktopBuddyMod.GetTopBarRaycastTarget(__instance?.Slot);
            if (targetRoot == null || targetRoot.IsDestroyed)
                return;

            Func<ICollider, int, bool> previous = filter;
            filter = (collider, depth) =>
            {
                try
                {
                    if (previous != null && !previous(collider, depth))
                        return false;

                    Slot hitSlot = collider?.Slot;
                    return hitSlot != null && !targetRoot.IsDestroyed && hitSlot.IsChildOf(targetRoot);
                }
                catch
                {
                    return false;
                }
            };
        }
        catch
        {
        }
    }
}

using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    internal static void Msg(string msg) => DesktopBuddy.Log.Msg(msg);
    internal static void Error(string msg) => DesktopBuddy.Log.Error(msg);

    internal static void RegisterTopBarRaycastPortal(Slot portalSlot, Slot targetRoot)
    {
        if (portalSlot == null || targetRoot == null)
            return;

        RefID portalId = portalSlot.ReferenceID;
        lock (TopBarRaycastGate)
            TopBarRaycastTargets[portalId] = targetRoot;

        portalSlot.Destroyed += _ => UnregisterTopBarRaycastPortal(portalId);
        targetRoot.Destroyed += _ => UnregisterTopBarRaycastPortal(portalId);
        Msg($"[TopBarRaycast] Registered portal={portalId} target={targetRoot.ReferenceID}");
    }

    internal static Slot GetTopBarRaycastTarget(Slot portalSlot)
    {
        if (portalSlot == null)
            return null;

        RefID portalId = portalSlot.ReferenceID;
        lock (TopBarRaycastGate)
        {
            if (!TopBarRaycastTargets.TryGetValue(portalId, out Slot target) || target == null || target.IsDestroyed)
            {
                TopBarRaycastTargets.Remove(portalId);
                return null;
            }

            return target;
        }
    }

    private static void UnregisterTopBarRaycastPortal(RefID portalId)
    {
        lock (TopBarRaycastGate)
            TopBarRaycastTargets.Remove(portalId);
    }
}

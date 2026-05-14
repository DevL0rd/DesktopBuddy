using System;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

internal static class UiStickScroll
{
    internal static void StartLoop(
        Slot ownerRoot,
        Slot surfaceSlot,
        Slot renderRoot,
        Func<int> nextGeneration,
        Func<int> currentGeneration,
        float deadzone,
        float pixelsPerTick)
    {
        if (ownerRoot?.World == null)
            return;

        int generation = nextGeneration();

        void Tick()
        {
            if (ownerRoot == null || ownerRoot.IsDestroyed ||
                surfaceSlot == null || surfaceSlot.IsDestroyed ||
                !surfaceSlot.ActiveSelf || generation != currentGeneration())
                return;

            Process(ownerRoot.World, renderRoot, deadzone, pixelsPerTick);
            ownerRoot.World.RunInUpdates(1, Tick);
        }

        ownerRoot.World.RunInUpdates(1, Tick);
    }

    internal static void Process(World world, Slot renderRoot, float deadzone, float pixelsPerTick)
    {
        var localUserRoot = world?.LocalUser?.Root;
        if (world == null || localUserRoot == null || renderRoot == null || renderRoot.IsDestroyed)
            return;

        var handler = localUserRoot.GetRegisteredComponent((InteractionHandler h) => h.Side.Value == Chirality.Right);
        var currentTouchable = handler?.Laser?.CurrentTouchable;
        if (currentTouchable == null || !(currentTouchable is IAxisActionReceiver receiver))
            return;

        var touchableSlot = currentTouchable.Slot;
        if (touchableSlot == null || !touchableSlot.IsChildOf(renderRoot, includeSelf: true))
            return;

        var controller = world.InputInterface.GetControllerNode(Chirality.Right);
        if (controller == null)
            return;

        float axisY = controller.Axis.Value.y;
        if (Math.Abs(axisY) <= deadzone)
            return;

        receiver.ProcessAxis(handler.Laser.TouchSource, new float2(0f, axisY * pixelsPerTick));
    }
}

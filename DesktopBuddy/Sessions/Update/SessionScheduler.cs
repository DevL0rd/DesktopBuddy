using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static readonly HashSet<World> _scheduledWorlds = new();
    private const int VirtualCameraTargetFps = 60;

    private static void CleanupTrace(string message) => DesktopBuddy.Log.MsgImmediate($"[CleanupTrace] {message}");

    private static long TraceStart(string label)
    {
        CleanupTrace($"{label} START");
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static void TraceDone(string label, long startTicks)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        CleanupTrace($"{label} DONE {ms:F2}ms");
    }

    internal static void ScheduleUpdate(World world)
    {
        if (_scheduledWorlds.Contains(world)) return;
        _scheduledWorlds.Add(world);
        world.RunInUpdates(1, () => UpdateLoop(world));
    }

    private static int _updateCount;

}

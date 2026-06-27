using System;
using System.Collections.Generic;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;

namespace DesktopBuddy;

internal static class EyeManagerEnabledDiagnostic
{
    private static readonly object _gate = new();
    private static readonly HashSet<World> _watchedWorlds = new();
    private static readonly HashSet<RefID> _watchedComponents = new();
    private static bool _installed;

    internal static void Install()
    {
        lock (_gate)
        {
            if (_installed) return;
            _installed = true;
        }

        var engine = Engine.Current;
        if (engine?.WorldManager == null)
        {
            lock (_gate) { _installed = false; }
            Log.Msg("[EyeDiag] Engine/WorldManager not ready; diagnostic NOT armed. Call Install() again after engine ready.");
            return;
        }

        engine.WorldManager.WorldAdded += WatchWorld;
        engine.WorldManager.WorldFocused += WatchWorld;
        foreach (var world in engine.WorldManager.Worlds)
            WatchWorld(world);

        Log.Msg("[EyeDiag] Installed. Watching ALL worlds for EyeManager.Enabled -> false (and its slot ActiveSelf -> false).");
    }

    private static void WatchWorld(World world)
    {
        if (world == null) return;
        lock (_gate)
        {
            if (!_watchedWorlds.Add(world)) return;
        }

        world.RunSynchronously(() =>
        {
            try
            {
                world.ComponentAdded += OnWorldComponentAdded;
                int existing = 0;
                foreach (var eye in world.RootSlot.GetComponentsInChildren<EyeManager>())
                {
                    OnEyeManagerAdded(eye.Slot, eye);
                    existing++;
                }
                Log.Msg($"[EyeDiag] Attached to world '{world.Name}' (armed {existing} existing EyeManager(s); future ones caught live via ComponentAdded).");
            }
            catch (Exception ex)
            {
                Log.Msg($"[EyeDiag] Failed to attach to world '{world.Name}': {ex}");
            }
        });
    }

    private static void OnWorldComponentAdded(Slot slot, Component component)
    {
        if (component is EyeManager eye)
            OnEyeManagerAdded(slot, eye);
    }

    private static void OnEyeManagerAdded(Slot slot, Component component)
    {
        if (component is not EyeManager eye || eye.IsDestroyed) return;
        lock (_gate)
        {
            if (!_watchedComponents.Add(eye.ReferenceID)) return;
        }

        try
        {
            eye.EnabledField.OnValueChange += _ => OnEnabledChanged(eye);
            eye.Slot.ActiveSelf_Field.OnValueChange += _ => OnSlotActiveChanged(eye);

            bool isLocalAvatar = eye.World?.LocalUser?.Root != null && eye.Slot.ActiveUserRoot == eye.World.LocalUser.Root;
            Log.Msg(
                $"[EyeDiag] ARMED on EyeManager {eye.ReferenceID} | slot='{SlotPath(eye.Slot)}' | world='{eye.World?.Name}' | " +
                $"localUserAvatar={isLocalAvatar} | Enabled={eye.EnabledField.Value} | slotActive={eye.Slot.ActiveSelf} | " +
                $"EnabledDriven={eye.EnabledField.IsDriven}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[EyeDiag] Failed to arm on EyeManager {eye.ReferenceID}: {ex}");
        }
    }

    private static void OnEnabledChanged(EyeManager eye)
    {
        try
        {
            if (eye == null || eye.IsDestroyed) return;
            var field = eye.EnabledField;
            if (field.Value) return;

            string link = field.IsDriven
                ? $"DRIVEN by [{field.ActiveLink}]"
                : (field.IsLinked ? $"LINKED [{field.ActiveLink}] (hook)" : "NOT driven -> DIRECT VALUE WRITE");

            Log.Msg(
                "[EyeDiag] !!!!!!!!!! EyeManager.Enabled -> FALSE !!!!!!!!!!\n" +
                $"  eyeManager = {eye.ReferenceID}\n" +
                $"  slot       = '{SlotPath(eye.Slot)}'\n" +
                $"  world      = '{eye.World?.Name}'  worldTime = {eye.World?.Time?.WorldTime}\n" +
                $"  link       = {link}\n" +
                $"  --- MANAGED STACK AT WRITE ---\n{Environment.StackTrace}\n" +
                "[EyeDiag] ----------------------------------------------");
        }
        catch (Exception ex)
        {
            Log.Msg($"[EyeDiag] Enabled handler error: {ex}\n{Environment.StackTrace}");
        }
    }

    private static void OnSlotActiveChanged(EyeManager eye)
    {
        try
        {
            if (eye == null || eye.IsDestroyed) return;
            var field = eye.Slot.ActiveSelf_Field;
            if (field.Value) return;

            string link = field.IsDriven
                ? $"DRIVEN by [{field.ActiveLink}]"
                : "NOT driven -> DIRECT VALUE WRITE";

            Log.Msg(
                "[EyeDiag] ########## EyeManager SLOT ActiveSelf -> FALSE ##########\n" +
                $"  eyeManager = {eye.ReferenceID}\n" +
                $"  slot       = '{SlotPath(eye.Slot)}'\n" +
                $"  link       = {link}\n" +
                $"  --- MANAGED STACK AT WRITE ---\n{Environment.StackTrace}\n" +
                "[EyeDiag] ----------------------------------------------");
        }
        catch (Exception ex)
        {
            Log.Msg($"[EyeDiag] Slot handler error: {ex}\n{Environment.StackTrace}");
        }
    }

    private static string SlotPath(Slot slot)
    {
        if (slot == null) return "<null>";
        var parts = new List<string>();
        var s = slot;
        int guard = 0;
        while (s != null && guard++ < 48)
        {
            parts.Insert(0, s.Name);
            s = s.Parent;
        }
        return string.Join("/", parts);
    }
}

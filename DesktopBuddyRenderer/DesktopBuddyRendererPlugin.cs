using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using BepInEx;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using HarmonyLib;
using Renderite.Unity;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    [BepInPlugin("net.desktopbuddy.renderer", "DesktopBuddyRenderer", "1.0.0")]
    public class DesktopBuddyRendererPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static DesktopBuddyRendererPlugin Instance;

        private string _queuePrefix;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private float _pollTimer;
        private const float PollInterval = 0.1f; // 100ms

        // Maps session slot → active UwcDisplaySource
        private readonly Dictionary<int, UwcDisplaySource> _activeSources = new Dictionary<int, UwcDisplaySource>();

        // Maps magic displayIndex → UwcDisplaySource (for the Harmony patch to look up)
        private static readonly Dictionary<int, UwcDisplaySource> _indexToSource = new Dictionary<int, UwcDisplaySource>();

        // Pending binds — UWC may not have enumerated the window yet
        private readonly List<(int slot, UwcDisplaySource source)> _pendingBinds = new List<(int, UwcDisplaySource)>();

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo("DesktopBuddyRenderer starting...");

            _queuePrefix = ParseQueuePrefix();
            if (_queuePrefix == null)
            {
                Log.LogWarning("Could not parse QueueName from command line — side-channel disabled");
                return;
            }

            // Apply Harmony patches
            var harmony = new Harmony("net.desktopbuddy.renderer");
            harmony.PatchAll();
            Log.LogInfo($"DesktopBuddyRenderer ready, queue prefix: {_queuePrefix}");
        }

        private void Update()
        {
            if (_queuePrefix == null) return;

            // Tick all active sources every frame (request capture + detect texture readiness)
            foreach (var kv in _activeSources)
                kv.Value.Tick();

            // Retry pending UWC binds
            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, source) = _pendingBinds[i];
                if (source.TryBind())
                {
                    _pendingBinds.RemoveAt(i);
                    int offset = slot * CaptureSessionProtocol.SessionSize;
                    _accessor.Write(offset + 20, CaptureSessionProtocol.StatusRunning);
                    _accessor.Write(offset + 24, source.Width);
                    _accessor.Write(offset + 28, source.Height);
                    Log.LogInfo($"[PendingBind] Slot {slot} now bound: {source.Width}x{source.Height}");
                }
            }

            _pollTimer += Time.unscaledDeltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            // Try to open the MMF if not yet open
            if (_mmf == null)
            {
                try
                {
                    var mmfName = CaptureSessionProtocol.GetMmfName(_queuePrefix + "Primary");
                    _mmf = MemoryMappedFile.OpenExisting(mmfName);
                    _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.TotalSize,
                        MemoryMappedFileAccess.ReadWrite);
                    Log.LogInfo($"Opened MMF: {mmfName}");
                }
                catch (System.IO.FileNotFoundException)
                {
                    return; // Game side hasn't created the MMF yet
                }
            }

            // Poll each session slot
            for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
            {
                int offset = i * CaptureSessionProtocol.SessionSize;
                int status = _accessor.ReadInt32(offset + 20);

                if (status == CaptureSessionProtocol.StatusStart && !_activeSources.ContainsKey(i))
                {
                    long hwndRaw = _accessor.ReadInt64(offset + 4);
                    long monitorRaw = _accessor.ReadInt64(offset + 12);
                    var hwnd = new IntPtr(hwndRaw);

                    Log.LogInfo($"Starting capture slot={i} hwnd=0x{hwndRaw:X} monitor=0x{monitorRaw:X}");

                    var source = new UwcDisplaySource(hwnd);
                    _activeSources[i] = source;

                    int magicIndex = CaptureSessionProtocol.MagicIndexBase + i;
                    _indexToSource[magicIndex] = source;

                    if (source.TryBind())
                    {
                        _accessor.Write(offset + 20, CaptureSessionProtocol.StatusRunning);
                        _accessor.Write(offset + 24, source.Width);
                        _accessor.Write(offset + 28, source.Height);
                        Log.LogInfo($"Capture slot={i} running: {source.Width}x{source.Height}");
                    }
                    else
                    {
                        // UWC hasn't seen this window yet — queue for retry
                        _pendingBinds.Add((i, source));
                        Log.LogInfo($"Capture slot={i} queued for UWC bind (hwnd not yet enumerated)");
                    }
                }
                else if (status == CaptureSessionProtocol.StatusStop && _activeSources.ContainsKey(i))
                {
                    Log.LogInfo($"Stopping capture slot={i}");
                    int magicIndex = CaptureSessionProtocol.MagicIndexBase + i;
                    _indexToSource.Remove(magicIndex);

                    _activeSources[i].Dispose();
                    _activeSources.Remove(i);

                    _pendingBinds.RemoveAll(p => p.slot == i);

                    _accessor.Write(offset + 20, CaptureSessionProtocol.StatusIdle);
                }
            }
        }

        /// <summary>
        /// Look up the UwcDisplaySource for a magic displayIndex, or null if not ours.
        /// Called from the Harmony patch on DesktopTextureAsset.Handle.
        /// </summary>
        internal static UwcDisplaySource GetSourceForDisplayIndex(int displayIndex)
        {
            _indexToSource.TryGetValue(displayIndex, out var source);
            return source;
        }

        private static string ParseQueuePrefix()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-QueueName", StringComparison.OrdinalIgnoreCase))
                {
                    var queueName = args[i + 1];
                    if (queueName.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
                        return queueName.Substring(0, queueName.Length - 7);
                    return queueName;
                }
            }
            return null;
        }

        private void OnDestroy()
        {
            foreach (var kv in _activeSources)
                kv.Value.Dispose();
            _activeSources.Clear();
            _indexToSource.Clear();
            _pendingBinds.Clear();

            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }

    /// <summary>
    /// Harmony postfix on DisplayDriver.TryGetDisplayTexture(int index).
    /// For magic indices (>= MagicIndexBase), return our UwcDisplaySource.
    /// The normal DesktopTextureAsset.Update() flow then handles RegisterRequest/TextureUpdated.
    /// </summary>
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class DisplayDriverPatch
    {
        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            if (index < CaptureSessionProtocol.MagicIndexBase)
                return; // Normal display — keep original result

            var source = DesktopBuddyRendererPlugin.GetSourceForDisplayIndex(index);
            if (source != null)
            {
                __result = source;
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayDriverPatch] index={index} → UwcDisplaySource (IsValid={source.IsValid}, " +
                    $"texture={(source.UnityTexture != null ? "ready" : "null")}, {source.Width}x{source.Height})");
            }
            else
            {
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayDriverPatch] index={index} → no source registered yet");
            }
        }
    }
}

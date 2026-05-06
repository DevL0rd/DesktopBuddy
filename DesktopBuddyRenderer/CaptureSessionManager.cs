using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using InterprocessLib;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    internal sealed class CaptureSessionManager : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private Messenger _messenger;
        private float _connectRetryTimer;
        private float _connectLogTimer;
        private const float ConnectRetryInterval = 1f;
        private const float ConnectLogInterval = 5f;

        private readonly Dictionary<int, IDesktopDisplaySource> _activeSources = new Dictionary<int, IDesktopDisplaySource>();
        private static readonly Dictionary<int, IDesktopDisplaySource> _indexToSource = new Dictionary<int, IDesktopDisplaySource>();
        private readonly List<(int slot, IDesktopDisplaySource source)> _pendingBinds = new List<(int, IDesktopDisplaySource)>();

        internal CaptureSessionManager(ManualLogSource log)
        {
            _log = log;
            DesktopBuddyRendererPlugin.LogInfo("[CaptureSessionManager] Constructed");
        }

        internal static IDesktopDisplaySource GetSourceForIndex(int displayIndex)
        {
            _indexToSource.TryGetValue(displayIndex, out var source);
            return source;
        }

        internal void Update()
        {
            TryEnsureMessenger();

            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { DesktopBuddyRendererPlugin.LogError("IPC action failed", ex); }
            }

            foreach (var kv in _activeSources)
                kv.Value.Tick();

            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, source) = _pendingBinds[i];
                bool bound;
                try
                {
                    bound = source.TryBind();
                }
                catch (Exception ex)
                {
                    DesktopBuddyRendererPlugin.LogError($"Pending TryBind threw slot={slot}", ex);
                    continue;
                }
                if (!bound) continue;
                _pendingBinds.RemoveAt(i);
                WriteRunning(slot, source);
                DesktopBuddyRendererPlugin.LogInfo($"[PendingBind] Slot {slot} bound: {source.Width}x{source.Height}");
            }
        }

        private void TryEnsureMessenger()
        {
            if (_messenger != null) return;

            _connectRetryTimer += Time.unscaledDeltaTime;
            _connectLogTimer += Time.unscaledDeltaTime;
            if (_connectLogTimer >= ConnectLogInterval)
            {
                _connectLogTimer = 0f;
                DesktopBuddyRendererPlugin.LogInfo($"[CaptureSessionManager] Waiting for InterprocessLib queue {CaptureSessionProtocol.QueueName}");
            }
            if (_connectRetryTimer < ConnectRetryInterval) return;
            _connectRetryTimer = 0f;

            try
            {
                DesktopBuddyRendererPlugin.LogInfo("[CaptureSessionManager] Creating Messenger");
                Messenger.OnWarning += OnWarning;
                Messenger.OnFailure += OnFailure;

                _messenger = new Messenger(
                    CaptureSessionProtocol.OwnerId,
                    false,
                    CaptureSessionProtocol.QueueName,
                    SimpleMemoryPackerPool.Instance);

                _messenger.ReceiveObject<CaptureStartMessage>(
                    CaptureSessionProtocol.StartMessageId,
                    msg =>
                    {
                        DesktopBuddyRendererPlugin.LogInfo(
                            $"[CaptureSessionManager] Received Start slot={msg.SessionId} hwnd=0x{msg.Hwnd:X} monitor=0x{msg.MonitorHandle:X} legacyUwc={msg.UseLegacyUwc}");
                        _mainThreadActions.Enqueue(() => StartCapture(msg.SessionId, msg.Hwnd, msg.MonitorHandle, msg.UseLegacyUwc));
                    });

                _messenger.ReceiveObject<CaptureStopMessage>(
                    CaptureSessionProtocol.StopMessageId,
                    msg =>
                    {
                        DesktopBuddyRendererPlugin.LogInfo($"[CaptureSessionManager] Received Stop slot={msg.SessionId}");
                        _mainThreadActions.Enqueue(() => StopCapture(msg.SessionId));
                    });

                DesktopBuddyRendererPlugin.LogInfo($"Opened InterprocessLib queue: {CaptureSessionProtocol.QueueName}");
            }
            catch (Exception ex)
            {
                Messenger.OnWarning -= OnWarning;
                Messenger.OnFailure -= OnFailure;
                _messenger?.Dispose();
                _messenger = null;
                DesktopBuddyRendererPlugin.LogWarning($"InterprocessLib queue not ready: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void StartCapture(int slot, long hwndRaw, long monitorRaw, bool useLegacyUwc)
        {
            if (_activeSources.ContainsKey(slot))
                StopCapture(slot);

            var hwnd = new IntPtr(hwndRaw);
            bool useUwcSource = useLegacyUwc && hwnd != IntPtr.Zero;

            DesktopBuddyRendererPlugin.LogInfo($"Starting capture slot={slot} hwnd=0x{hwndRaw:X} monitor=0x{monitorRaw:X} source={(useUwcSource ? "UWC" : "WGC")}");

            IDesktopDisplaySource source;
            try
            {
                source = useUwcSource
                    ? (IDesktopDisplaySource)new UwcDisplaySource(hwnd, _log)
                    : new WgcDisplaySource(hwnd, new IntPtr(monitorRaw), _log);
                _activeSources[slot] = source;
                _indexToSource[CaptureSessionProtocol.MagicIndexBase + slot] = source;
                DesktopBuddyRendererPlugin.LogInfo($"Registered source index={CaptureSessionProtocol.MagicIndexBase + slot} slot={slot}");
            }
            catch (Exception ex)
            {
                DesktopBuddyRendererPlugin.LogError($"Source construction failed slot={slot}", ex);
                return;
            }

            bool bound;
            try
            {
                bound = source.TryBind();
            }
            catch (Exception ex)
            {
                DesktopBuddyRendererPlugin.LogError($"Initial TryBind threw slot={slot}", ex);
                return;
            }

            if (bound)
            {
                WriteRunning(slot, source);
            }
            else
            {
                DesktopBuddyRendererPlugin.LogWarning($"Initial TryBind failed slot={slot}; adding pending bind");
                _pendingBinds.Add((slot, source));
            }
        }

        private void StopCapture(int slot)
        {
            if (!_activeSources.ContainsKey(slot)) return;

            DesktopBuddyRendererPlugin.LogInfo($"Stopping capture slot={slot}");
            _indexToSource.Remove(CaptureSessionProtocol.MagicIndexBase + slot);
            _activeSources[slot].Dispose();
            _activeSources.Remove(slot);
            _pendingBinds.RemoveAll(p => p.slot == slot);
        }

        private void WriteRunning(int slot, IDesktopDisplaySource source)
        {
            try
            {
                _messenger?.SendObject(CaptureSessionProtocol.RunningMessageId, new CaptureRunningMessage
                {
                    SessionId = slot,
                    Width = source.Width,
                    Height = source.Height
                });
            }
            catch (Exception ex)
            {
                DesktopBuddyRendererPlugin.LogWarning($"Failed to send running ack for slot {slot}: {ex.Message}");
            }

            DesktopBuddyRendererPlugin.LogInfo($"Capture slot={slot} running via {source.SourceName}: {source.Width}x{source.Height}");
        }

        private void OnWarning(string message)
        {
            DesktopBuddyRendererPlugin.LogWarning($"[InterprocessLib] {message}");
        }

        private void OnFailure(Exception ex)
        {
            DesktopBuddyRendererPlugin.LogError("[InterprocessLib]", ex);
        }

        public void Dispose()
        {
            DesktopBuddyRendererPlugin.LogInfo("[CaptureSessionManager] Disposing");
            foreach (var kv in _activeSources)
                kv.Value.Dispose();
            _activeSources.Clear();
            _indexToSource.Clear();
            _pendingBinds.Clear();
            _messenger?.Dispose();
            _messenger = null;
            Messenger.OnWarning -= OnWarning;
            Messenger.OnFailure -= OnFailure;
        }
    }
}

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
        private const float ConnectRetryInterval = 1f;

        private readonly Dictionary<int, UwcDisplaySource> _activeSources = new Dictionary<int, UwcDisplaySource>();
        private static readonly Dictionary<int, UwcDisplaySource> _indexToSource = new Dictionary<int, UwcDisplaySource>();
        private readonly List<(int slot, UwcDisplaySource source)> _pendingBinds = new List<(int, UwcDisplaySource)>();

        internal CaptureSessionManager(ManualLogSource log)
        {
            _log = log;
        }

        internal static UwcDisplaySource GetSourceForIndex(int displayIndex)
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
                catch (Exception ex) { _log.LogError($"IPC action failed: {ex}"); }
            }

            foreach (var kv in _activeSources)
                kv.Value.Tick();

            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, source) = _pendingBinds[i];
                if (!source.TryBind()) continue;
                _pendingBinds.RemoveAt(i);
                WriteRunning(slot, source);
                _log.LogInfo($"[PendingBind] Slot {slot} bound: {source.Width}x{source.Height}");
            }
        }

        private void TryEnsureMessenger()
        {
            if (_messenger != null) return;

            _connectRetryTimer += Time.unscaledDeltaTime;
            if (_connectRetryTimer < ConnectRetryInterval) return;
            _connectRetryTimer = 0f;

            try
            {
                Messenger.OnWarning += OnWarning;
                Messenger.OnFailure += OnFailure;

                _messenger = new Messenger(
                    CaptureSessionProtocol.OwnerId,
                    false,
                    CaptureSessionProtocol.QueueName,
                    SimpleMemoryPackerPool.Instance);

                _messenger.ReceiveObject<CaptureStartMessage>(
                    CaptureSessionProtocol.StartMessageId,
                    msg => _mainThreadActions.Enqueue(() => StartCapture(msg.SessionId, msg.Hwnd, msg.MonitorHandle)));

                _messenger.ReceiveObject<CaptureStopMessage>(
                    CaptureSessionProtocol.StopMessageId,
                    msg => _mainThreadActions.Enqueue(() => StopCapture(msg.SessionId)));

                _log.LogInfo($"Opened InterprocessLib queue: {CaptureSessionProtocol.QueueName}");
            }
            catch (Exception ex)
            {
                Messenger.OnWarning -= OnWarning;
                Messenger.OnFailure -= OnFailure;
                _messenger?.Dispose();
                _messenger = null;
                _log.LogDebug($"InterprocessLib queue not ready: {ex.Message}");
            }
        }

        private void StartCapture(int slot, long hwndRaw, long monitorRaw)
        {
            if (_activeSources.ContainsKey(slot))
                StopCapture(slot);

            var hwnd = new IntPtr(hwndRaw);

            _log.LogInfo($"Starting capture slot={slot} hwnd=0x{hwndRaw:X} monitor=0x{monitorRaw:X}");

            var source = new UwcDisplaySource(hwnd, _log);
            _activeSources[slot] = source;
            _indexToSource[CaptureSessionProtocol.MagicIndexBase + slot] = source;

            if (source.TryBind())
                WriteRunning(slot, source);
            else
                _pendingBinds.Add((slot, source));
        }

        private void StopCapture(int slot)
        {
            if (!_activeSources.ContainsKey(slot)) return;

            _log.LogInfo($"Stopping capture slot={slot}");
            _indexToSource.Remove(CaptureSessionProtocol.MagicIndexBase + slot);
            _activeSources[slot].Dispose();
            _activeSources.Remove(slot);
            _pendingBinds.RemoveAll(p => p.slot == slot);
        }

        private void WriteRunning(int slot, UwcDisplaySource source)
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
                _log.LogWarning($"Failed to send running ack for slot {slot}: {ex.Message}");
            }

            _log.LogInfo($"Capture slot={slot} running: {source.Width}x{source.Height}");
        }

        private void OnWarning(string message)
        {
            _log.LogWarning($"[InterprocessLib] {message}");
        }

        private void OnFailure(Exception ex)
        {
            _log.LogError($"[InterprocessLib] {ex}");
        }

        public void Dispose()
        {
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

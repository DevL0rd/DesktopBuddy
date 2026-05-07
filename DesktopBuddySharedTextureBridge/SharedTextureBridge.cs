using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using InterprocessLib;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class SharedTextureBridge : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private Messenger _messenger;
        private float _connectRetryTimer;
        private float _connectLogTimer;
        private const float ConnectRetryInterval = 1f;
        private const float ConnectLogInterval = 5f;

        private readonly Dictionary<int, SharedTextureSlot> _activeSlots = new Dictionary<int, SharedTextureSlot>();
        private static readonly Dictionary<int, SharedTextureSlot> _bridgeIndexToSlot = new Dictionary<int, SharedTextureSlot>();
        private readonly List<(int slot, SharedTextureSlot textureSlot)> _pendingBinds = new List<(int, SharedTextureSlot)>();

        internal SharedTextureBridge(ManualLogSource log)
        {
            _log = log;
            SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Constructed");
        }

        internal static SharedTextureSlot GetSlotForBridgeIndex(int bridgeIndex)
        {
            _bridgeIndexToSlot.TryGetValue(bridgeIndex, out var textureSlot);
            return textureSlot;
        }

        internal int ActiveSlotCount => _activeSlots.Count;
        internal int PendingBindCount => _pendingBinds.Count;
        internal int TotalTextureRequestCount
        {
            get
            {
                int total = 0;
                foreach (var slot in _activeSlots.Values)
                    total += slot.RequestCount;
                return total;
            }
        }

        internal void Update()
        {
            TryEnsureMessenger();

            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogError("IPC action failed", ex); }
            }

            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, textureSlot) = _pendingBinds[i];
                bool bound;
                try
                {
                    bound = textureSlot.TryBind();
                }
                catch (Exception ex)
                {
                    SharedTextureBridgePlugin.LogError($"Pending TryBind threw slot={slot}", ex);
                    continue;
                }
                if (!bound) continue;
                _pendingBinds.RemoveAt(i);
                WriteRunning(slot, textureSlot);
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Slot {slot} bound: {textureSlot.Width}x{textureSlot.Height}");
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
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Waiting for InterprocessLib queue {SharedTextureBridgeProtocol.QueueName}");
            }
            if (_connectRetryTimer < ConnectRetryInterval) return;
            _connectRetryTimer = 0f;

            try
            {
                SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Creating Messenger");
                Messenger.OnWarning += OnWarning;
                Messenger.OnFailure += OnFailure;

                _messenger = new Messenger(
                    SharedTextureBridgeProtocol.OwnerId,
                    false,
                    SharedTextureBridgeProtocol.QueueName,
                    SimpleMemoryPackerPool.Instance);

                _messenger.ReceiveObject<SharedTextureStartMessage>(
                    SharedTextureBridgeProtocol.StartMessageId,
                    msg =>
                    {
                        SharedTextureBridgePlugin.LogInfo(
                            $"[SharedTextureBridge] Received Start slot={msg.SlotId} shared=0x{msg.SharedTextureHandle:X} {msg.SharedTextureWidth}x{msg.SharedTextureHeight}");
                        _mainThreadActions.Enqueue(() => StartSharedTexture(
                            msg.SlotId,
                            msg.SharedTextureHandle,
                            msg.SharedTextureWidth,
                            msg.SharedTextureHeight));
                    });

                _messenger.ReceiveObject<SharedTextureStopMessage>(
                    SharedTextureBridgeProtocol.StopMessageId,
                    msg =>
                    {
                        SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Received Stop slot={msg.SlotId}");
                        _mainThreadActions.Enqueue(() => StopSharedTexture(msg.SlotId));
                    });

                SharedTextureBridgePlugin.LogInfo($"Opened InterprocessLib queue: {SharedTextureBridgeProtocol.QueueName}");
            }
            catch (Exception ex)
            {
                Messenger.OnWarning -= OnWarning;
                Messenger.OnFailure -= OnFailure;
                _messenger?.Dispose();
                _messenger = null;
                SharedTextureBridgePlugin.LogWarning($"InterprocessLib queue not ready: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void StartSharedTexture(int slot, long sharedTextureHandleRaw, int sharedTextureWidth, int sharedTextureHeight)
        {
            if (_activeSlots.ContainsKey(slot))
                StopSharedTexture(slot);

            var sharedTextureHandle = new IntPtr(sharedTextureHandleRaw);

            SharedTextureBridgePlugin.LogInfo($"Starting shared texture slot={slot} shared=0x{sharedTextureHandleRaw:X} {sharedTextureWidth}x{sharedTextureHeight}");
            if (sharedTextureHandle == IntPtr.Zero || sharedTextureWidth <= 0 || sharedTextureHeight <= 0)
            {
                SharedTextureBridgePlugin.LogWarning($"Shared texture start ignored slot={slot}: missing handle or size");
                return;
            }

            SharedTextureSlot textureSlot;
            try
            {
                textureSlot = new SharedTextureSlot(sharedTextureHandle, sharedTextureWidth, sharedTextureHeight, _log);
                _activeSlots[slot] = textureSlot;
                _bridgeIndexToSlot[SharedTextureBridgeProtocol.MagicIndexBase + slot] = textureSlot;
                SharedTextureBridgePlugin.LogInfo($"Registered bridge index={SharedTextureBridgeProtocol.MagicIndexBase + slot} slot={slot}");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"Shared texture slot construction failed slot={slot}", ex);
                return;
            }

            bool bound;
            try
            {
                bound = textureSlot.TryBind();
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"Initial TryBind threw slot={slot}", ex);
                return;
            }

            if (bound)
            {
                WriteRunning(slot, textureSlot);
            }
            else
            {
                SharedTextureBridgePlugin.LogWarning($"Initial TryBind failed slot={slot}; adding pending bind");
                _pendingBinds.Add((slot, textureSlot));
            }
        }

        private void StopSharedTexture(int slot)
        {
            if (!_activeSlots.ContainsKey(slot)) return;

            SharedTextureBridgePlugin.LogInfo($"Stopping shared texture slot={slot}");
            _bridgeIndexToSlot.Remove(SharedTextureBridgeProtocol.MagicIndexBase + slot);
            _activeSlots[slot].Dispose();
            _activeSlots.Remove(slot);
            _pendingBinds.RemoveAll(p => p.slot == slot);
        }

        private void WriteRunning(int slot, SharedTextureSlot textureSlot)
        {
            try
            {
                _messenger?.SendObject(SharedTextureBridgeProtocol.RunningMessageId, new SharedTextureRunningMessage
                {
                    SlotId = slot,
                    Width = textureSlot.Width,
                    Height = textureSlot.Height
                });
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogWarning($"Failed to send running ack for slot {slot}: {ex.Message}");
            }

            SharedTextureBridgePlugin.LogInfo($"Shared texture slot={slot} running: {textureSlot.Width}x{textureSlot.Height}");
        }

        private void OnWarning(string message)
        {
            SharedTextureBridgePlugin.LogWarning($"[InterprocessLib] {message}");
        }

        private void OnFailure(Exception ex)
        {
            SharedTextureBridgePlugin.LogError("[InterprocessLib]", ex);
        }

        public void Dispose()
        {
            SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Disposing");
            foreach (var kv in _activeSlots)
                kv.Value.Dispose();
            _activeSlots.Clear();
            _bridgeIndexToSlot.Clear();
            _pendingBinds.Clear();
            _messenger?.Dispose();
            _messenger = null;
            Messenger.OnWarning -= OnWarning;
            Messenger.OnFailure -= OnFailure;
        }
    }
}

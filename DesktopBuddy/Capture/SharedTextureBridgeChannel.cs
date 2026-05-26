using System;
using System.Collections.Generic;
using System.Threading;
using DesktopBuddy.Shared;
using InterprocessLib;

namespace DesktopBuddy;

internal sealed class SharedTextureBridgeChannel : IDisposable
{
    private Messenger _messenger;
    private readonly object _slotStateLock = new();
    private readonly HashSet<int> _usedSlots = new();
    private readonly bool[] _slotStopping = new bool[SharedTextureBridgeProtocol.MaxTextureSlots];
    private readonly int[] _slotGenerations = new int[SharedTextureBridgeProtocol.MaxTextureSlots];
    private readonly bool[] _slotRunning = new bool[SharedTextureBridgeProtocol.MaxTextureSlots];
    private readonly int[] _slotWidths = new int[SharedTextureBridgeProtocol.MaxTextureSlots];
    private readonly int[] _slotHeights = new int[SharedTextureBridgeProtocol.MaxTextureSlots];
    private bool _disposed;

    internal bool IsOpen => _messenger != null && !_disposed;

    internal void Open()
    {
        if (_messenger != null) return;

        Messenger.OnWarning += OnWarning;
        Messenger.OnFailure += OnFailure;

        _messenger = new Messenger(
            SharedTextureBridgeProtocol.OwnerId,
            true,
            SharedTextureBridgeProtocol.QueueName,
            SimpleMemoryPackerPool.Instance);

        RegisterMessages();

        Log.Msg($"[SharedTextureBridgeChannel] Opened InterprocessLib queue: {SharedTextureBridgeProtocol.QueueName}");
    }

    private void RegisterMessages()
    {
        // InterprocessLib's wrapper commands use type indexes, so both endpoints must
        // register the same shared texture message types in the same order.
        _messenger.ReceiveObject<SharedTextureStartMessage>(
            SharedTextureBridgeProtocol.StartMessageId,
            _ => { });
        _messenger.ReceiveObject<SharedTextureStopMessage>(
            SharedTextureBridgeProtocol.StopMessageId,
            _ => { });
        _messenger.ReceiveObject<SharedTextureRunningMessage>(
            SharedTextureBridgeProtocol.RunningMessageId,
            OnRunning);
        _messenger.ReceiveObject<SharedTextureStoppedMessage>(
            SharedTextureBridgeProtocol.StoppedMessageId,
            OnStopped);
        _messenger.ReceiveObject<SharedTextureRendererDeviceMessage>(
            SharedTextureBridgeProtocol.RendererDeviceMessageId,
            OnRendererDevice);
    }

    internal int RegisterTexture(IntPtr sharedTextureHandle, string sharedTextureName, int sharedTextureWidth, int sharedTextureHeight)
    {
        if (_messenger == null)
            throw new InvalidOperationException("Channel not open");

        int slot = -1;
        int generation;
        lock (_slotStateLock)
        {
            for (int i = 0; i < SharedTextureBridgeProtocol.MaxTextureSlots; i++)
            {
                if (!_usedSlots.Contains(i) && !_slotStopping[i])
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0)
            {
                Log.Msg("[SharedTextureBridgeChannel] No free texture slots available");
                return -1;
            }

            _usedSlots.Add(slot);
            generation = ++_slotGenerations[slot];
            _slotStopping[slot] = false;
            _slotRunning[slot] = false;
            _slotWidths[slot] = 0;
            _slotHeights[slot] = 0;
        }

        QueueStart(slot, generation, sharedTextureHandle, sharedTextureName, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Registered texture slot={slot} gen={generation} name={sharedTextureName} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
        return slot;
    }

    internal void StopTexture(int slot)
    {
        Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture ENTER slot={slot} disposed={_disposed} messenger={_messenger != null}");
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;

        bool shouldQueueStop = false;
        int generation = 0;
        lock (_slotStateLock)
        {
            if (_usedSlots.Contains(slot) && !_slotStopping[slot])
            {
                generation = _slotGenerations[slot];
                _slotStopping[slot] = true;
                _slotRunning[slot] = false;
                _slotWidths[slot] = 0;
                _slotHeights[slot] = 0;
                shouldQueueStop = true;
            }
        }

        if (shouldQueueStop)
            QueueStop(slot, generation);

        Log.Msg($"[SharedTextureBridgeChannel] Stopped texture slot={slot}");
        Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture EXIT slot={slot}");
    }

    internal void UpdateTexture(int slot, IntPtr sharedTextureHandle, string sharedTextureName, int sharedTextureWidth, int sharedTextureHeight)
    {
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;

        int generation;
        lock (_slotStateLock)
        {
            if (!_usedSlots.Contains(slot)) return;

            generation = ++_slotGenerations[slot];
            _slotStopping[slot] = false;
            _slotRunning[slot] = false;
            _slotWidths[slot] = 0;
            _slotHeights[slot] = 0;
        }

        QueueStart(slot, generation, sharedTextureHandle, sharedTextureName, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Updated texture slot={slot} gen={generation} name={sharedTextureName} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
    }

    internal bool IsTextureRunning(int slot)
    {
        if (slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots)
            return false;

        lock (_slotStateLock)
            return _slotRunning[slot];
    }

    internal bool IsTextureRunning(int slot, int width, int height)
    {
        if (slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots)
            return false;

        lock (_slotStateLock)
            return _slotRunning[slot] &&
                   _slotWidths[slot] == width &&
                   _slotHeights[slot] == height;
    }

    private void QueueStart(int slot, int generation, IntPtr sharedTextureHandle, string sharedTextureName, int sharedTextureWidth, int sharedTextureHeight)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var messenger = _messenger;
                if (messenger == null || _disposed) return;

                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.SendStart START slot={slot} gen={generation} name={sharedTextureName} shared=0x{sharedTextureHandle:X}");
                messenger.SendObject(SharedTextureBridgeProtocol.StartMessageId, new SharedTextureStartMessage
                {
                    SlotId = slot,
                    Generation = generation,
                    SharedTextureHandle = sharedTextureHandle.ToInt64(),
                    SharedTextureName = sharedTextureName,
                    SharedTextureWidth = sharedTextureWidth,
                    SharedTextureHeight = sharedTextureHeight
                });
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.SendStart DONE slot={slot} gen={generation}");
            }
            catch (Exception ex)
            {
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.SendStart ERROR slot={slot} gen={generation}: {ex}");
            }
        });
    }

    private void QueueStop(int slot, int generation)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var messenger = _messenger;
                if (messenger == null || _disposed) return;

                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject START slot={slot} gen={generation}");
                messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot, Generation = generation });
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject DONE slot={slot} gen={generation}");
            }
            catch (Exception ex)
            {
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject ERROR slot={slot} gen={generation}: {ex}");
            }
        });
    }

    private void OnRunning(SharedTextureRunningMessage message)
    {
        try
        {
            if (message == null) return;
            int slot = message.SlotId;
            if (slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;
            string logMessage;
            lock (_slotStateLock)
            {
                if (!_usedSlots.Contains(slot) || _slotGenerations[slot] != message.Generation)
                {
                    logMessage = $"[SharedTextureBridgeChannel] Ignored stale running ack slot={slot} gen={message.Generation} current={_slotGenerations[slot]}";
                }
                else
                {
                    _slotRunning[slot] = true;
                    _slotWidths[slot] = message.Width;
                    _slotHeights[slot] = message.Height;
                    logMessage = $"[SharedTextureBridgeChannel] Texture {slot} gen={message.Generation} running: {message.Width}x{message.Height}";
                }
            }

            Log.Msg(logMessage);
        }
        catch (Exception ex)
        {
            Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.OnRunning ERROR: {ex}");
        }
    }

    private void OnStopped(SharedTextureStoppedMessage message)
    {
        try
        {
            if (message == null) return;
            int slot = message.SlotId;
            if (slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;
            string logMessage;
            lock (_slotStateLock)
            {
                if (_slotGenerations[slot] != message.Generation)
                {
                    logMessage = $"[SharedTextureBridgeChannel] Ignored stale stopped ack slot={slot} gen={message.Generation} current={_slotGenerations[slot]}";
                }
                else
                {
                    _usedSlots.Remove(slot);
                    _slotStopping[slot] = false;
                    _slotRunning[slot] = false;
                    _slotWidths[slot] = 0;
                    _slotHeights[slot] = 0;
                    logMessage = $"[SharedTextureBridgeChannel] Texture {slot} gen={message.Generation} stopped";
                }
            }

            Log.Msg(logMessage);
        }
        catch (Exception ex)
        {
            Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.OnStopped ERROR: {ex}");
        }
    }

    private static void OnRendererDevice(SharedTextureRendererDeviceMessage message)
    {
        try
        {
            if (message == null || message.AdapterLuid == 0) return;
            WgcCapture.SetRendererAdapterHint(message.AdapterLuid, (uint)message.VendorId, message.Description);
        }
        catch (Exception ex)
        {
            Log.Msg($"[SharedTextureBridgeChannel] Renderer device hint error: {ex}");
        }
    }

    private static void OnWarning(string message)
    {
        Log.Msg($"[InterprocessLib] WARN {message}");
    }

    private static void OnFailure(Exception ex)
    {
        Log.Msg($"[InterprocessLib] ERROR {ex}");
    }

    public void Dispose()
    {
        int usedSlotCount;
        lock (_slotStateLock)
            usedSlotCount = _usedSlots.Count;

        Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.Dispose ENTER disposed={_disposed} messenger={_messenger != null} slots={usedSlotCount}");
        if (_disposed) return;
        _disposed = true;

        var slotsToStop = new List<(int Slot, int Generation)>();
        lock (_slotStateLock)
        {
            foreach (int slot in _usedSlots)
                slotsToStop.Add((slot, _slotGenerations[slot]));

            _usedSlots.Clear();
            Array.Clear(_slotStopping, 0, _slotStopping.Length);
            Array.Clear(_slotRunning, 0, _slotRunning.Length);
            Array.Clear(_slotWidths, 0, _slotWidths.Length);
            Array.Clear(_slotHeights, 0, _slotHeights.Length);
        }

        if (_messenger != null)
        {
            foreach (var slot in slotsToStop)
            {
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.Dispose SendObject START slot={slot.Slot}");
                try { _messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot.Slot, Generation = slot.Generation }); }
                catch { }
                Log.Msg($"[CleanupTrace] SharedTextureBridgeChannel.Dispose SendObject DONE slot={slot.Slot}");
            }
        }

        Log.Msg("[CleanupTrace] SharedTextureBridgeChannel.Dispose Messenger.Dispose START");
        _messenger?.Dispose();
        Log.Msg("[CleanupTrace] SharedTextureBridgeChannel.Dispose Messenger.Dispose DONE");
        _messenger = null;

        Messenger.OnWarning -= OnWarning;
        Messenger.OnFailure -= OnFailure;
        Log.Msg("[CleanupTrace] SharedTextureBridgeChannel.Dispose EXIT");
    }
}

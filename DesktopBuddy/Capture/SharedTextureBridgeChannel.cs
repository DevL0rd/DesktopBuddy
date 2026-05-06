using System;
using System.Collections.Generic;
using DesktopBuddy.Shared;
using InterprocessLib;

namespace DesktopBuddy;

internal sealed class SharedTextureBridgeChannel : IDisposable
{
    private Messenger _messenger;
    private readonly HashSet<int> _usedSlots = new();
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

        _messenger.ReceiveObject<SharedTextureRunningMessage>(
            SharedTextureBridgeProtocol.RunningMessageId,
            OnRunning);

        Log.Msg($"[SharedTextureBridgeChannel] Opened InterprocessLib queue: {SharedTextureBridgeProtocol.QueueName}");
    }

    internal int RegisterTexture(IntPtr sharedTextureHandle, int sharedTextureWidth, int sharedTextureHeight)
    {
        if (_messenger == null)
            throw new InvalidOperationException("Channel not open");

        int slot = -1;
        for (int i = 0; i < SharedTextureBridgeProtocol.MaxTextureSlots; i++)
        {
            if (!_usedSlots.Contains(i))
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
        _slotRunning[slot] = false;
        _slotWidths[slot] = 0;
        _slotHeights[slot] = 0;

        SendStart(slot, sharedTextureHandle, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Registered texture slot={slot} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
        return slot;
    }

    internal void StopTexture(int slot)
    {
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;

        if (_usedSlots.Remove(slot))
        {
            _messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot });
            _slotRunning[slot] = false;
            _slotWidths[slot] = 0;
            _slotHeights[slot] = 0;
        }

        Log.Msg($"[SharedTextureBridgeChannel] Stopped texture slot={slot}");
    }

    internal void UpdateTexture(int slot, IntPtr sharedTextureHandle, int sharedTextureWidth, int sharedTextureHeight)
    {
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;
        if (!_usedSlots.Contains(slot)) return;

        _slotRunning[slot] = false;
        _slotWidths[slot] = 0;
        _slotHeights[slot] = 0;

        SendStart(slot, sharedTextureHandle, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Updated texture slot={slot} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
    }

    internal bool IsTextureRunning(int slot)
    {
        return slot >= 0 && slot < SharedTextureBridgeProtocol.MaxTextureSlots && _slotRunning[slot];
    }

    private void SendStart(int slot, IntPtr sharedTextureHandle, int sharedTextureWidth, int sharedTextureHeight)
    {
        _messenger.SendObject(SharedTextureBridgeProtocol.StartMessageId, new SharedTextureStartMessage
        {
            SlotId = slot,
            SharedTextureHandle = sharedTextureHandle.ToInt64(),
            SharedTextureWidth = sharedTextureWidth,
            SharedTextureHeight = sharedTextureHeight
        });
    }

    private void OnRunning(SharedTextureRunningMessage message)
    {
        int slot = message.SlotId;
        if (slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;

        _slotRunning[slot] = true;
        _slotWidths[slot] = message.Width;
        _slotHeights[slot] = message.Height;
        Log.Msg($"[SharedTextureBridgeChannel] Texture {slot} running: {message.Width}x{message.Height}");
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
        if (_disposed) return;
        _disposed = true;

        if (_messenger != null)
        {
            foreach (var slot in _usedSlots)
            {
                try { _messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot }); }
                catch { }
            }
        }

        _usedSlots.Clear();
        _messenger?.Dispose();
        _messenger = null;

        Messenger.OnWarning -= OnWarning;
        Messenger.OnFailure -= OnFailure;
    }
}

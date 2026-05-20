using System;
using System.Collections.Generic;
using System.Threading;
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

        QueueStart(slot, sharedTextureHandle, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Registered texture slot={slot} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
        return slot;
    }

    internal void StopTexture(int slot)
    {
        Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture ENTER slot={slot} disposed={_disposed} messenger={_messenger != null}");
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;

        if (_usedSlots.Remove(slot))
        {
            QueueStop(slot);
            _slotRunning[slot] = false;
            _slotWidths[slot] = 0;
            _slotHeights[slot] = 0;
        }

        Log.Msg($"[SharedTextureBridgeChannel] Stopped texture slot={slot}");
        Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture EXIT slot={slot}");
    }

    internal void UpdateTexture(int slot, IntPtr sharedTextureHandle, int sharedTextureWidth, int sharedTextureHeight)
    {
        if (_messenger == null || _disposed || slot < 0 || slot >= SharedTextureBridgeProtocol.MaxTextureSlots) return;
        if (!_usedSlots.Contains(slot)) return;

        _slotRunning[slot] = false;
        _slotWidths[slot] = 0;
        _slotHeights[slot] = 0;

        QueueStart(slot, sharedTextureHandle, sharedTextureWidth, sharedTextureHeight);
        Log.Msg($"[SharedTextureBridgeChannel] Updated texture slot={slot} shared=0x{sharedTextureHandle:X} {sharedTextureWidth}x{sharedTextureHeight}");
    }

    internal bool IsTextureRunning(int slot)
    {
        return slot >= 0 && slot < SharedTextureBridgeProtocol.MaxTextureSlots && _slotRunning[slot];
    }

    internal bool IsTextureRunning(int slot, int width, int height)
    {
        return slot >= 0 &&
               slot < SharedTextureBridgeProtocol.MaxTextureSlots &&
               _slotRunning[slot] &&
               _slotWidths[slot] == width &&
               _slotHeights[slot] == height;
    }

    private void QueueStart(int slot, IntPtr sharedTextureHandle, int sharedTextureWidth, int sharedTextureHeight)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var messenger = _messenger;
                if (messenger == null || _disposed) return;

                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.SendStart START slot={slot} shared=0x{sharedTextureHandle:X}");
                messenger.SendObject(SharedTextureBridgeProtocol.StartMessageId, new SharedTextureStartMessage
                {
                    SlotId = slot,
                    SharedTextureHandle = sharedTextureHandle.ToInt64(),
                    SharedTextureWidth = sharedTextureWidth,
                    SharedTextureHeight = sharedTextureHeight
                });
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.SendStart DONE slot={slot}");
            }
            catch (Exception ex)
            {
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.SendStart ERROR slot={slot}: {ex}");
            }
        });
    }

    private void QueueStop(int slot)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var messenger = _messenger;
                if (messenger == null || _disposed) return;

                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject START slot={slot}");
                messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot });
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject DONE slot={slot}");
            }
            catch (Exception ex)
            {
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.StopTexture SendObject ERROR slot={slot}: {ex}");
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

            _slotRunning[slot] = true;
            _slotWidths[slot] = message.Width;
            _slotHeights[slot] = message.Height;
            Log.Msg($"[SharedTextureBridgeChannel] Texture {slot} running: {message.Width}x{message.Height}");
        }
        catch (Exception ex)
        {
            Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.OnRunning ERROR: {ex}");
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
        Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.Dispose ENTER disposed={_disposed} messenger={_messenger != null} slots={_usedSlots.Count}");
        if (_disposed) return;
        _disposed = true;

        if (_messenger != null)
        {
            foreach (var slot in _usedSlots)
            {
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.Dispose SendObject START slot={slot}");
                try { _messenger.SendObject(SharedTextureBridgeProtocol.StopMessageId, new SharedTextureStopMessage { SlotId = slot }); }
                catch { }
                Log.MsgImmediate($"[CleanupTrace] SharedTextureBridgeChannel.Dispose SendObject DONE slot={slot}");
            }
        }

        _usedSlots.Clear();
        Log.MsgImmediate("[CleanupTrace] SharedTextureBridgeChannel.Dispose Messenger.Dispose START");
        _messenger?.Dispose();
        Log.MsgImmediate("[CleanupTrace] SharedTextureBridgeChannel.Dispose Messenger.Dispose DONE");
        _messenger = null;

        Messenger.OnWarning -= OnWarning;
        Messenger.OnFailure -= OnFailure;
        Log.MsgImmediate("[CleanupTrace] SharedTextureBridgeChannel.Dispose EXIT");
    }
}

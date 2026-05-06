using System;
using System.Collections.Generic;
using DesktopBuddy.Shared;
using InterprocessLib;

namespace DesktopBuddy;

internal sealed class CaptureSessionChannel : IDisposable
{
    private Messenger _messenger;
    private readonly HashSet<int> _usedSlots = new();
    private readonly bool[] _running = new bool[CaptureSessionProtocol.MaxSessions];
    private readonly int[] _widths = new int[CaptureSessionProtocol.MaxSessions];
    private readonly int[] _heights = new int[CaptureSessionProtocol.MaxSessions];
    private bool _disposed;

    internal bool IsOpen => _messenger != null && !_disposed;

    internal void Open(string queueName)
    {
        if (_messenger != null) return;

        Messenger.OnWarning += OnWarning;
        Messenger.OnFailure += OnFailure;

        _messenger = new Messenger(
            CaptureSessionProtocol.OwnerId,
            true,
            CaptureSessionProtocol.QueueName,
            SimpleMemoryPackerPool.Instance);

        _messenger.ReceiveObject<CaptureRunningMessage>(
            CaptureSessionProtocol.RunningMessageId,
            OnRunning);

        Log.Msg($"[CaptureSessionChannel] Opened InterprocessLib queue: {CaptureSessionProtocol.QueueName}");
    }

    internal int RegisterSession(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (_messenger == null)
            throw new InvalidOperationException("Channel not open");

        int slot = -1;
        for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
        {
            if (!_usedSlots.Contains(i))
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            Log.Msg("[CaptureSessionChannel] No free capture slots available");
            return -1;
        }

        _usedSlots.Add(slot);
        _running[slot] = false;
        _widths[slot] = 0;
        _heights[slot] = 0;

        _messenger.SendObject(CaptureSessionProtocol.StartMessageId, new CaptureStartMessage
        {
            SessionId = slot,
            Hwnd = hwnd.ToInt64(),
            MonitorHandle = monitorHandle.ToInt64(),
            UseLegacyUwc = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.UseLegacyUwc) ?? false
        });

        Log.Msg($"[CaptureSessionChannel] Registered session slot={slot} hwnd=0x{hwnd:X} monitor=0x{monitorHandle:X} legacyUwc={DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.UseLegacyUwc) ?? false}");
        return slot;
    }

    internal void StopSession(int slot)
    {
        if (_messenger == null || _disposed || slot < 0 || slot >= CaptureSessionProtocol.MaxSessions) return;

        if (_usedSlots.Remove(slot))
        {
            _messenger.SendObject(CaptureSessionProtocol.StopMessageId, new CaptureStopMessage { SessionId = slot });
            _running[slot] = false;
            _widths[slot] = 0;
            _heights[slot] = 0;
        }

        Log.Msg($"[CaptureSessionChannel] Stopped session slot={slot}");
    }

    internal bool IsSessionRunning(int slot)
    {
        return slot >= 0 && slot < CaptureSessionProtocol.MaxSessions && _running[slot];
    }

    private void OnRunning(CaptureRunningMessage message)
    {
        int slot = message.SessionId;
        if (slot < 0 || slot >= CaptureSessionProtocol.MaxSessions) return;

        _running[slot] = true;
        _widths[slot] = message.Width;
        _heights[slot] = message.Height;
        Log.Msg($"[CaptureSessionChannel] Session {slot} running: {message.Width}x{message.Height}");
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
                try { _messenger.SendObject(CaptureSessionProtocol.StopMessageId, new CaptureStopMessage { SessionId = slot }); }
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

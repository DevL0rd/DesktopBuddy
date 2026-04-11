using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBuddy;

/// <summary>
/// Game-side writer for the capture session MMF.
/// Creates the MMF and writes HWND/monitor info for the renderer plugin to pick up.
/// </summary>
internal sealed class CaptureSessionChannel : IDisposable
{
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private bool _disposed;

    internal bool IsOpen => _mmf != null;

    internal void Open(string queueName)
    {
        if (_mmf != null) return;

        var name = CaptureSessionProtocol.GetMmfName(queueName);
        _mmf = MemoryMappedFile.CreateOrOpen(name, CaptureSessionProtocol.TotalSize);
        _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.TotalSize);

        // Zero all slots
        for (int i = 0; i < CaptureSessionProtocol.TotalSize; i++)
            _accessor.Write(i, (byte)0);

        Log.Msg($"[CaptureSessionChannel] Opened MMF: {name}");
    }

    /// <summary>
    /// Register a capture session. Returns the slot index (0-based) used as offset for magic DisplayIndex.
    /// </summary>
    internal int RegisterSession(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (_accessor == null)
            throw new InvalidOperationException("Channel not open");

        // Find a free slot (renderer writes StatusIdle after processing a stop)
        int slot = -1;
        for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
        {
            int off = i * CaptureSessionProtocol.SessionSize;
            int status = _accessor.ReadInt32(off + 20);
            if (status == CaptureSessionProtocol.StatusIdle)
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

        int offset = slot * CaptureSessionProtocol.SessionSize;

        _accessor.Write(offset + 0, slot);                              // sessionId
        _accessor.Write(offset + 4, hwnd.ToInt64());                    // hwnd
        _accessor.Write(offset + 12, monitorHandle.ToInt64());          // monitorHandle
        _accessor.Write(offset + 20, CaptureSessionProtocol.StatusStart); // status = start

        Log.Msg($"[CaptureSessionChannel] Registered session slot={slot} hwnd=0x{hwnd:X} monitor=0x{monitorHandle:X}");
        return slot;
    }

    /// <summary>
    /// Signal the renderer to stop capturing for a session slot.
    /// </summary>
    internal void StopSession(int slot)
    {
        if (_accessor == null || _disposed) return;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        _accessor.Write(offset + 20, CaptureSessionProtocol.StatusStop);
        Log.Msg($"[CaptureSessionChannel] Stopped session slot={slot}");
    }

    /// <summary>
    /// Check if the renderer has acknowledged the start (status = running).
    /// </summary>
    internal bool IsSessionRunning(int slot)
    {
        if (_accessor == null) return false;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        return _accessor.ReadInt32(offset + 20) == CaptureSessionProtocol.StatusRunning;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal all active sessions to stop
        if (_accessor != null)
        {
            for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
            {
                int offset = i * CaptureSessionProtocol.SessionSize;
                int status = _accessor.ReadInt32(offset + 20);
                if (status == CaptureSessionProtocol.StatusStart || status == CaptureSessionProtocol.StatusRunning)
                    _accessor.Write(offset + 20, CaptureSessionProtocol.StatusStop);
            }
        }

        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}

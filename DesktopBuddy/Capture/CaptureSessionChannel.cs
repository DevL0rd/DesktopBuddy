using System;
using System.IO.MemoryMappedFiles;

namespace DesktopBuddy;

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

        for (int i = 0; i < CaptureSessionProtocol.TotalSize; i++)
            _accessor.Write(i, (byte)0);

        Log.Msg($"[CaptureSessionChannel] Opened MMF: {name}");
    }

    internal int RegisterSession(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (_accessor == null)
            throw new InvalidOperationException("Channel not open");

        int slot = -1;
        for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
        {
            int off = i * CaptureSessionProtocol.SessionSize;
            if (_accessor.ReadInt32(off + CaptureSessionProtocol.OffsetStatus) == CaptureSessionProtocol.StatusIdle)
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
        _accessor.Write(offset + CaptureSessionProtocol.OffsetSessionId, slot);
        _accessor.Write(offset + CaptureSessionProtocol.OffsetHwnd, hwnd.ToInt64());
        _accessor.Write(offset + CaptureSessionProtocol.OffsetMonitor, monitorHandle.ToInt64());
        _accessor.Write(offset + CaptureSessionProtocol.OffsetStatus, CaptureSessionProtocol.StatusStart);

        Log.Msg($"[CaptureSessionChannel] Registered session slot={slot} hwnd=0x{hwnd:X} monitor=0x{monitorHandle:X}");
        return slot;
    }

    internal void StopSession(int slot)
    {
        if (_accessor == null || _disposed) return;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        _accessor.Write(offset + CaptureSessionProtocol.OffsetStatus, CaptureSessionProtocol.StatusStop);
        Log.Msg($"[CaptureSessionChannel] Stopped session slot={slot}");
    }

    internal bool IsSessionRunning(int slot)
    {
        if (_accessor == null) return false;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        return _accessor.ReadInt32(offset + CaptureSessionProtocol.OffsetStatus) == CaptureSessionProtocol.StatusRunning;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_accessor != null)
        {
            for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
            {
                int offset = i * CaptureSessionProtocol.SessionSize;
                int status = _accessor.ReadInt32(offset + CaptureSessionProtocol.OffsetStatus);
                if (status == CaptureSessionProtocol.StatusStart || status == CaptureSessionProtocol.StatusRunning)
                    _accessor.Write(offset + CaptureSessionProtocol.OffsetStatus, CaptureSessionProtocol.StatusStop);
            }
        }

        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}

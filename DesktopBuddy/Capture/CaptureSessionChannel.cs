using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
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
    internal string QueueName { get; private set; }

    internal void Open(string queueName)
    {
        if (_mmf != null) return;
        QueueName = queueName;

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
    internal int RegisterSession(IntPtr hwnd, IntPtr monitorHandle, int monitorIndex = -1)
    {
        if (_accessor == null)
            throw new InvalidOperationException("Channel not open");

        // Find a free slot (renderer writes StatusIdle after processing a stop)
        int slot = -1;
        for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
        {
            int off = i * CaptureSessionProtocol.SessionSize;
            int status = _accessor.ReadInt32(off + CaptureSessionProtocol.OffStatus);
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

        _accessor.Write(offset + CaptureSessionProtocol.OffSessionId, slot);
        _accessor.Write(offset + CaptureSessionProtocol.OffHwnd, hwnd.ToInt64());
        _accessor.Write(offset + CaptureSessionProtocol.OffMonitor, monitorHandle.ToInt64());
        _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamNone);
        _accessor.Write(offset + CaptureSessionProtocol.OffStreamPort, 0);
        _accessor.Write(offset + CaptureSessionProtocol.OffProcessId, 0);
        _accessor.Write(offset + CaptureSessionProtocol.OffMonitorIndex, monitorIndex);
        _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusStart);

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
        _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusStop);
        Log.Msg($"[CaptureSessionChannel] Stopped session slot={slot}");
    }

    /// <summary>
    /// Request the renderer to start streaming for a session slot.
    /// </summary>
    internal void RequestStream(int slot, uint processId)
    {
        if (_accessor == null || _disposed) return;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        _accessor.Write(offset + CaptureSessionProtocol.OffProcessId, (int)processId);
        _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamRequested);
        Log.Msg($"[CaptureSessionChannel] Requested stream slot={slot} pid={processId}");
    }

    /// <summary>
    /// Signal the renderer to stop streaming for a session slot.
    /// </summary>
    internal void StopStream(int slot)
    {
        if (_accessor == null || _disposed) return;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamStopping);
        Log.Msg($"[CaptureSessionChannel] Stopping stream slot={slot}");
    }

    /// <summary>
    /// Read the stream port assigned by the renderer for a session slot.
    /// Returns 0 if not yet active.
    /// </summary>
    internal int GetStreamPort(int slot)
    {
        if (_accessor == null) return 0;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        return _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStreamPort);
    }

    /// <summary>
    /// Check if the renderer has streaming active for a session slot.
    /// </summary>
    internal bool IsStreamActive(int slot)
    {
        if (_accessor == null) return false;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        return _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStreamStatus) == CaptureSessionProtocol.StreamActive;
    }

    /// <summary>
    /// Check if the renderer has acknowledged the start (status = running).
    /// </summary>
    internal bool IsSessionRunning(int slot)
    {
        if (_accessor == null) return false;
        int offset = slot * CaptureSessionProtocol.SessionSize;
        return _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStatus) == CaptureSessionProtocol.StatusRunning;
    }

    // --- Tunnel URL reading (written by renderer's CloudflareTunnel) ---

    private MemoryMappedFile _tunnelMmf;
    private MemoryMappedViewAccessor _tunnelAccessor;
    private string _lastTunnelUrl;

    /// <summary>
    /// Try to read the tunnel URL from the renderer's shared MMF.
    /// Returns null if not available yet. Caches and returns the same
    /// string instance if unchanged to avoid allocations.
    /// </summary>
    internal string ReadTunnelUrl()
    {
        if (_tunnelAccessor == null)
        {
            try
            {
                var name = CaptureSessionProtocol.GetTunnelMmfName(QueueName);
                _tunnelMmf = MemoryMappedFile.OpenExisting(name);
                _tunnelAccessor = _tunnelMmf.CreateViewAccessor(0, CaptureSessionProtocol.TunnelMmfSize, MemoryMappedFileAccess.Read);
            }
            catch (System.IO.FileNotFoundException) { return null; }
            catch (Exception ex) { Log.Msg($"[CaptureSessionChannel] Tunnel MMF open error: {ex.Message}"); return null; }
        }

        int len = _tunnelAccessor.ReadInt32(0);
        if (len <= 0 || len > CaptureSessionProtocol.TunnelMmfSize - 4)
        {
            if (_lastTunnelUrl != null)
            {
                _lastTunnelUrl = null;
                Log.Msg("[CaptureSessionChannel] Tunnel URL cleared");
            }
            return null;
        }

        var bytes = new byte[len];
        _tunnelAccessor.ReadArray(4, bytes, 0, len);
        var url = Encoding.UTF8.GetString(bytes);
        if (url != _lastTunnelUrl)
        {
            _lastTunnelUrl = url;
            Log.Msg($"[CaptureSessionChannel] Tunnel URL: {url}");
        }
        return _lastTunnelUrl;
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
                int status = _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStatus);
                if (status == CaptureSessionProtocol.StatusStart || status == CaptureSessionProtocol.StatusRunning)
                    _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusStop);
            }
        }

        _accessor?.Dispose();
        _mmf?.Dispose();

        _tunnelAccessor?.Dispose();
        _tunnelMmf?.Dispose();
    }
}

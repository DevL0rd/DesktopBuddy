using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    internal sealed class CaptureSessionManager : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly string _queuePrefix;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private float _pollTimer;
        private const float PollInterval = 0.1f;

        private readonly Dictionary<int, UwcDisplaySource> _activeSources = new Dictionary<int, UwcDisplaySource>();
        private static readonly Dictionary<int, UwcDisplaySource> _indexToSource = new Dictionary<int, UwcDisplaySource>();
        private readonly List<(int slot, UwcDisplaySource source)> _pendingBinds = new List<(int, UwcDisplaySource)>();

        internal CaptureSessionManager(string queuePrefix, ManualLogSource log)
        {
            _queuePrefix = queuePrefix;
            _log = log;
        }

        internal static UwcDisplaySource GetSourceForIndex(int displayIndex)
        {
            _indexToSource.TryGetValue(displayIndex, out var source);
            return source;
        }

        internal void Update()
        {
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

            _pollTimer += Time.unscaledDeltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            if (!TryOpenMmf()) return;

            for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
                PollSlot(i);
        }

        private bool TryOpenMmf()
        {
            if (_mmf != null) return true;
            try
            {
                var mmfName = CaptureSessionProtocol.GetMmfName(_queuePrefix + "Primary");
                _mmf = MemoryMappedFile.OpenExisting(mmfName);
                _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);
                _log.LogInfo($"Opened MMF: {mmfName}");
                return true;
            }
            catch (System.IO.FileNotFoundException)
            {
                return false;
            }
        }

        private void PollSlot(int slot)
        {
            int offset = slot * CaptureSessionProtocol.SessionSize;
            int status = _accessor.ReadInt32(offset + CaptureSessionProtocol.OffsetStatus);

            if (status == CaptureSessionProtocol.StatusStart && !_activeSources.ContainsKey(slot))
                StartCapture(slot, offset);
            else if (status == CaptureSessionProtocol.StatusStop && _activeSources.ContainsKey(slot))
                StopCapture(slot, offset);
        }

        private void StartCapture(int slot, int offset)
        {
            long hwndRaw = _accessor.ReadInt64(offset + CaptureSessionProtocol.OffsetHwnd);
            long monitorRaw = _accessor.ReadInt64(offset + CaptureSessionProtocol.OffsetMonitor);
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

        private void StopCapture(int slot, int offset)
        {
            _log.LogInfo($"Stopping capture slot={slot}");
            _indexToSource.Remove(CaptureSessionProtocol.MagicIndexBase + slot);
            _activeSources[slot].Dispose();
            _activeSources.Remove(slot);
            _pendingBinds.RemoveAll(p => p.slot == slot);
            _accessor.Write(offset + CaptureSessionProtocol.OffsetStatus, CaptureSessionProtocol.StatusIdle);
        }

        private void WriteRunning(int slot, UwcDisplaySource source)
        {
            int offset = slot * CaptureSessionProtocol.SessionSize;
            _accessor.Write(offset + CaptureSessionProtocol.OffsetStatus, CaptureSessionProtocol.StatusRunning);
            _accessor.Write(offset + CaptureSessionProtocol.OffsetWidth, source.Width);
            _accessor.Write(offset + CaptureSessionProtocol.OffsetHeight, source.Height);
            _log.LogInfo($"Capture slot={slot} running: {source.Width}x{source.Height}");
        }

        public void Dispose()
        {
            foreach (var kv in _activeSources)
                kv.Value.Dispose();
            _activeSources.Clear();
            _indexToSource.Clear();
            _pendingBinds.Clear();
            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }
}

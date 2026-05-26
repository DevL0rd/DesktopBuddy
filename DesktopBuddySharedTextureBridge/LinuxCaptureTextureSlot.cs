using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Renderite.Unity;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class LinuxCaptureTextureSlot : IBridgeTextureSlot
    {
        private const int DeferredReleaseFrames = 3;

        private readonly ManualLogSource _log;
        private readonly HashSet<Action> _requests = new HashSet<Action>();
        private readonly LinuxNativeBridgeRenderer _bridge = new LinuxNativeBridgeRenderer();
        private readonly DxvkDmaBufImporter _importer;
        private readonly Queue<DeferredRelease> _deferredReleases = new Queue<DeferredRelease>();
        private readonly uint _pipeWireNodeId;

        private DmaBufImportedTexture _current;
        private bool _disposed;
        private bool _captureStarted;
        private bool _started;
        private int _width;
        private int _height;
        private int _pollsWithoutFrame;
        private int _importFailures;
        private int _importedFrames;
        private bool _loggedFirstFrame;

        private struct DeferredRelease
        {
            public DmaBufImportedTexture Texture;
            public int FramesRemaining;
        }

        public Texture UnityTexture => _current?.UnityTexture;
        public int Width => Math.Max(1, _width);
        public int Height => Math.Max(1, _height);
        public int RequestCount => _requests.Count;
        public bool IsValid => !_disposed && _started && _current != null;
        public string SourceName => "LinuxDmaBufCapture";

        internal LinuxCaptureTextureSlot(uint pipeWireNodeId, int widthHint, int heightHint, ManualLogSource log)
        {
            _pipeWireNodeId = pipeWireNodeId;
            _width = Math.Max(1, widthHint);
            _height = Math.Max(1, heightHint);
            _log = log;
            _importer = new DxvkDmaBufImporter(log);
        }

        public bool TryBind()
        {
            if (_started) return true;
            if (_disposed) return false;

            EnsureCaptureStarted();
            PollAndImportFrame();
            return _started;
        }

        public void Tick()
        {
            ProcessDeferredReleases();
            if (!_captureStarted || _disposed)
                return;

            PollAndImportFrame();
        }

        private void EnsureCaptureStarted()
        {
            if (_captureStarted || _disposed)
                return;

            if (!_importer.EnsureInitialized())
                return;

            ulong[] modifiers = _importer.QuerySupportedModifiers();
            int status = _bridge.StartGpu(_pipeWireNodeId, modifiers);
            SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] GPU DMA-BUF capture start returned {status} node={_pipeWireNodeId}");
            if (status != 0)
                SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] GPU capture did not start cleanly: {status}");

            _captureStarted = status == 0;
        }

        private bool PollAndImportFrame()
        {
            int status = _bridge.PollFrame(out var frame);
            if (status == 1)
            {
                _pollsWithoutFrame++;
                if (_pollsWithoutFrame == 120 || _pollsWithoutFrame % 600 == 0)
                    SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Waiting for DMA-BUF frame ({_pollsWithoutFrame} polls)");
                return false;
            }

            if (status != 0 || frame.Status != 0)
            {
                SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] PollFrame status={status} frameStatus={frame.Status} fd={frame.Fd}");
                return false;
            }
            _pollsWithoutFrame = 0;

            if (!_importer.TryImport(frame, out var imported))
            {
                _importFailures++;
                if (_importFailures <= 8 || _importFailures % 120 == 0)
                    SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] DMA-BUF import failed count={_importFailures} fd={frame.Fd} {frame.Width}x{frame.Height} fourcc=0x{frame.Fourcc:X8} modifier=0x{frame.Modifier:X} stride={frame.Stride}");
                return false;
            }

            var old = _current;
            bool resized = old == null || old.Width != imported.Width || old.Height != imported.Height;
            if (old != null && !resized && old.UnityTexture != null)
            {
                old.UnityTexture.UpdateExternalTexture(imported.ShaderResourceView);
                old.OwnsUnityTexture = false;
                imported.UseExistingUnityTexture(old.UnityTexture);
            }

            _current = imported;
            _width = imported.Width;
            _height = imported.Height;
            _importedFrames++;
            if (old != null)
                _deferredReleases.Enqueue(new DeferredRelease { Texture = old, FramesRemaining = DeferredReleaseFrames });

            if (!_started)
                _started = true;

            if (!_loggedFirstFrame || resized)
                NotifyCallbacks();

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] First GPU DMA-BUF frame imported {_width}x{_height} fourcc=0x{frame.Fourcc:X8} dxgi={imported.DxgiFormat} modifier=0x{frame.Modifier:X} stride={frame.Stride}");
            }
            else if (_importedFrames % 120 == 0)
            {
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Imported GPU DMA-BUF frames={_importedFrames} {_width}x{_height}");
            }

            return true;
        }

        private void ProcessDeferredReleases()
        {
            int count = _deferredReleases.Count;
            for (int i = 0; i < count; i++)
            {
                var release = _deferredReleases.Dequeue();
                release.FramesRemaining--;
                if (release.FramesRemaining > 0)
                {
                    _deferredReleases.Enqueue(release);
                    continue;
                }

                try { release.Texture?.Dispose(); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Deferred release failed: {ex.Message}"); }
            }
        }

        public void RegisterRequest(Action onTextureChanged)
        {
            try
            {
                if (onTextureChanged != null) _requests.Add(onTextureChanged);
                if (_current != null)
                    onTextureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LinuxCapture] RegisterRequest failed", ex);
            }
        }

        public void UnregisterRequest(Action onTextureChanged)
        {
            try
            {
                if (onTextureChanged != null) _requests.Remove(onTextureChanged);
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LinuxCapture] UnregisterRequest failed", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _current?.Dispose(); }
            catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Current release failed: {ex.Message}"); }
            _current = null;

            while (_deferredReleases.Count > 0)
            {
                try { _deferredReleases.Dequeue().Texture?.Dispose(); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Queued release failed: {ex.Message}"); }
            }

            _bridge.Dispose();
            _importer.Dispose();
            _requests.Clear();
        }

        private void NotifyCallbacks()
        {
            foreach (var cb in _requests)
            {
                try { cb?.Invoke(); }
                catch (Exception ex) { _log?.LogWarning($"[LinuxCapture] Callback error: {ex.Message}"); }
            }
        }
    }
}

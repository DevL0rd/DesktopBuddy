using System;
using System.Collections.Generic;
using uWindowCapture;
using UnityEngine;
using Renderite.Unity;

namespace DesktopBuddyRenderer
{
    /// <summary>
    /// Implements IDisplayTextureSource by wrapping a UwcWindow found by HWND.
    /// Plugged into DesktopTextureAsset._source when our magic DisplayIndex is detected.
    /// </summary>
    internal sealed class UwcDisplaySource : IDisplayTextureSource
    {
        private UwcWindow _window;
        private readonly IntPtr _hwnd;
        private readonly HashSet<Action> _requests = new HashSet<Action>();
        private bool _disposed;
        private bool _textureReady; // tracks whether we've seen a non-null texture

        public Texture UnityTexture => _window?.texture;
        public int Width => _window?.width ?? 0;
        public int Height => _window?.height ?? 0;
        public bool IsValid => _window != null && _window.isAlive;

        internal UwcDisplaySource(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        /// <summary>
        /// Try to bind to the UwcWindow. Returns true if found.
        /// May need to be retried if UWC hasn't enumerated the window yet.
        /// </summary>
        private int _tryBindCount;

        internal bool TryBind()
        {
            if (_window != null) return true;

            var mgr = UwcManager.instance;
            if (mgr == null)
            {
                if (_tryBindCount == 0 || _tryBindCount % 50 == 0)
                    DesktopBuddyRendererPlugin.Log.LogWarning($"[UwcDisplaySource] TryBind#{_tryBindCount}: UwcManager.instance is null");
                _tryBindCount++;
                return false;
            }

            _window = UwcManager.Find(_hwnd);
            if (_window == null)
            {
                if (_tryBindCount == 0 || _tryBindCount % 50 == 0)
                {
                    var windows = UwcManager.windows;
                    int count = windows != null ? windows.Count : -1;
                    string sample = "";
                    if (windows != null)
                    {
                        int shown = 0;
                        foreach (var kv in windows)
                        {
                            var w = kv.Value;
                            if (w == null) continue;
                            sample += $" [0x{w.handle.ToInt64():X}='{w.title}']";
                            if (++shown >= 5) break;
                        }
                    }
                    DesktopBuddyRendererPlugin.Log.LogWarning(
                        $"[UwcDisplaySource] TryBind#{_tryBindCount}: want hwnd=0x{_hwnd.ToInt64():X}, " +
                        $"UWC has {count} windows:{sample}");
                }
                _tryBindCount++;
                return false;
            }

            _window.captureMode = CaptureMode.PrintWindow;
            _window.RequestCapture(CapturePriority.High);

            // Wire up texture change notifications
            _window.onCaptured.AddListener(OnCaptured);
            _window.onSizeChanged.AddListener(OnCaptured);

            DesktopBuddyRendererPlugin.Log.LogInfo(
                $"[UwcDisplaySource] Bound to UwcWindow hwnd=0x{_hwnd.ToInt64():X} " +
                $"title='{_window.title}' {_window.width}x{_window.height} after {_tryBindCount} retries");
            return true;
        }

        /// <summary>
        /// Must be called every frame (from plugin Update). Requests a new capture and
        /// detects when the texture transitions from null → valid, firing callbacks.
        /// This matches the pattern used by DuplicableDisplay in Renderite.
        /// </summary>
        internal void Tick()
        {
            if (_window == null || _disposed) return;

            _window.RequestCapture();

            if (!_textureReady && _window.texture != null)
            {
                _textureReady = true;
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[UwcDisplaySource] Texture became ready: {_window.width}x{_window.height} hwnd=0x{_hwnd.ToInt64():X}");
                NotifyCallbacks();
            }
        }

        private void OnCaptured()
        {
            NotifyCallbacks();
        }

        private void NotifyCallbacks()
        {
            foreach (var cb in _requests)
            {
                try { cb?.Invoke(); }
                catch (Exception ex)
                {
                    DesktopBuddyRendererPlugin.Log.LogWarning($"[UwcDisplaySource] Callback error: {ex.Message}");
                }
            }
        }

        public void RegisterRequest(Action onTextureChanged)
        {
            _requests.Add(onTextureChanged);
            // Request an immediate capture so the consumer gets an initial frame
            _window?.RequestCapture(CapturePriority.High);
            DesktopBuddyRendererPlugin.Log.LogInfo(
                $"[UwcDisplaySource] RegisterRequest: texture={(UnityTexture != null ? "ready" : "null")} {Width}x{Height}");
        }

        public void UnregisterRequest(Action onTextureChanged)
        {
            _requests.Remove(onTextureChanged);
        }

        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_window != null)
            {
                _window.onCaptured.RemoveListener(OnCaptured);
                _window.onSizeChanged.RemoveListener(OnCaptured);
            }

            _requests.Clear();
            _window = null;
        }
    }
}

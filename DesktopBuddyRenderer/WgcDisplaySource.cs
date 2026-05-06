using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using Renderite.Unity;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    internal sealed class WgcDisplaySource : IDesktopDisplaySource
    {
        private readonly ManualLogSource _log;
        private readonly IntPtr _hwnd;
        private readonly IntPtr _monitorHandle;
        private readonly HashSet<Action> _requests = new HashSet<Action>();

        private IntPtr _nativeSession;
        private Texture2D _unityTexture;
        private IntPtr _lastTexture;
        private uint _lastVersion;
        private int _tickCount;
        private bool _started;
        private bool _disposed;
        private bool _closed;

        public Texture UnityTexture => _unityTexture;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsValid => !_disposed && !_closed && _started && _nativeSession != IntPtr.Zero && _unityTexture != null && _lastVersion > 0;
        public string SourceName => "WGC/native";
        private string SourceLabel => _hwnd != IntPtr.Zero
            ? $"hwnd=0x{_hwnd.ToInt64():X}"
            : $"monitor=0x{_monitorHandle.ToInt64():X}";

        internal WgcDisplaySource(IntPtr hwnd, IntPtr monitorHandle, ManualLogSource log)
        {
            _hwnd = hwnd;
            _monitorHandle = monitorHandle;
            _log = log;
            DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Constructed native {SourceLabel}");
        }

        internal static void PreloadNativeHelper()
        {
            try
            {
                Native.EnsureLoaded();
            }
            catch (Exception ex)
            {
                DesktopBuddyRendererPlugin.LogError("[WgcDisplaySource] Native helper preload failed", ex);
            }
        }

        public bool TryBind()
        {
            if (_started) return true;
            if (_disposed) return false;

            DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Native TryBind start {SourceLabel}");
            if (!RendererWgcDevice.IsReady && !RendererWgcDevice.Initialize(_log))
            {
                DesktopBuddyRendererPlugin.LogWarning("[WgcDisplaySource] Renderer D3D device is not ready");
                return false;
            }

            try
            {
                Native.EnsureLoaded();
                int hr = Native.DbWgcCreate(RendererWgcDevice.D3dDevice, _hwnd, _monitorHandle, out _nativeSession);
                if (hr < 0 || _nativeSession == IntPtr.Zero)
                {
                    DesktopBuddyRendererPlugin.LogWarning(
                        $"[WgcDisplaySource] Native create failed hr=0x{hr:X8} {SourceLabel}: {Native.GetLastError()}");
                    return false;
                }

                _started = true;
                Tick();
                DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Native started {SourceLabel} {Width}x{Height}");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                DesktopBuddyRendererPlugin.LogError("[WgcDisplaySource] Native helper DLL not found", ex);
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                DesktopBuddyRendererPlugin.LogError("[WgcDisplaySource] Native helper entry point missing", ex);
                return false;
            }
            catch (Exception ex)
            {
                DesktopBuddyRendererPlugin.LogError($"[WgcDisplaySource] Native TryBind failed {SourceLabel}", ex);
                Dispose();
                return false;
            }
        }

        public void Tick()
        {
            if (_disposed || !_started || _nativeSession == IntPtr.Zero) return;

            int hr = Native.DbWgcGetFrameInfo(_nativeSession, out var info);
            if (hr < 0)
            {
                DesktopBuddyRendererPlugin.LogWarning($"[WgcDisplaySource] Native frame info failed hr=0x{hr:X8} {SourceLabel}");
                return;
            }

            _tickCount++;
            Width = info.Width;
            Height = info.Height;
            _closed = info.IsClosed != 0;

            if (_tickCount <= 10 || _tickCount % 120 == 0 ||
                info.Texture == IntPtr.Zero || Width <= 0 || Height <= 0 || info.IsValid == 0)
            {
                DesktopBuddyRendererPlugin.LogInfo(
                    $"[WgcDisplaySource] FrameInfo tick={_tickCount} {Width}x{Height} " +
                    $"ptr=0x{info.Texture.ToInt64():X} version={info.Version} " +
                    $"valid={info.IsValid} closed={info.IsClosed} dxgiFormat={info.DxgiFormat} {SourceLabel}");
            }

            if (_closed)
            {
                DesktopBuddyRendererPlugin.LogWarning($"[WgcDisplaySource] Native session closed {SourceLabel}");
                return;
            }

            if (Width <= 0 || Height <= 0)
                return;

            bool sizeChanged = _unityTexture != null && (_unityTexture.width != Width || _unityTexture.height != Height);
            if (_unityTexture == null || sizeChanged)
            {
                var oldTexture = _unityTexture;
                var unityTexture = new Texture2D(Width, Height, TextureFormat.BGRA32, false, false);
                unityTexture.name = $"DesktopBuddy WGC {SourceLabel}";
                unityTexture.wrapMode = TextureWrapMode.Clamp;
                unityTexture.Apply(false, false);

                IntPtr nativePtr = unityTexture.GetNativeTexturePtr();
                if (nativePtr == IntPtr.Zero)
                {
                    DesktopBuddyRendererPlugin.LogWarning($"[WgcDisplaySource] Unity target texture native pointer is null {SourceLabel}");
                    UnityEngine.Object.Destroy(unityTexture);
                    return;
                }

                int setHr = Native.DbWgcSetTargetTexture(_nativeSession, nativePtr, Width, Height);
                if (setHr < 0)
                {
                    DesktopBuddyRendererPlugin.LogWarning(
                        $"[WgcDisplaySource] Native target set failed hr=0x{setHr:X8} ptr=0x{nativePtr.ToInt64():X} {Width}x{Height} {SourceLabel}");
                    UnityEngine.Object.Destroy(unityTexture);
                    return;
                }

                _unityTexture = unityTexture;
                _lastTexture = nativePtr;
                if (oldTexture != null)
                    UnityEngine.Object.Destroy(oldTexture);

                DesktopBuddyRendererPlugin.LogInfo(
                    $"[WgcDisplaySource] Unity-owned target texture ready {Width}x{Height} " +
                    $"ptr=0x{_lastTexture.ToInt64():X} version={info.Version} nativeTarget=0x{info.Texture.ToInt64():X} dxgiFormat={info.DxgiFormat} " +
                    $"unityFormat={TextureFormat.BGRA32} unityLinear=False {SourceLabel}");
                NotifyCallbacks();
            }

            if (info.Version != _lastVersion)
            {
                _lastVersion = info.Version;
                if (info.Version <= 10 || info.Version % 120 == 0)
                {
                    DesktopBuddyRendererPlugin.LogInfo(
                        $"[WgcDisplaySource] Frame version advanced to {info.Version} " +
                        $"{Width}x{Height} ptr=0x{info.Texture.ToInt64():X} dxgiFormat={info.DxgiFormat} {SourceLabel}");
                }
                NotifyCallbacks();
            }
        }

        public void RegisterRequest(Action onTextureChanged)
        {
            if (onTextureChanged != null) _requests.Add(onTextureChanged);
            DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] RegisterRequest texture={(UnityTexture != null ? "ready" : "null")} {Width}x{Height} {SourceLabel}");
        }

        public void UnregisterRequest(Action onTextureChanged)
        {
            if (onTextureChanged != null) _requests.Remove(onTextureChanged);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_unityTexture != null)
            {
                UnityEngine.Object.Destroy(_unityTexture);
                _unityTexture = null;
            }

            if (_nativeSession != IntPtr.Zero)
            {
                Native.DbWgcDestroy(_nativeSession);
                _nativeSession = IntPtr.Zero;
            }

            _requests.Clear();
            DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Disposed native {SourceLabel}");
        }

        private void NotifyCallbacks()
        {
            foreach (var cb in _requests)
            {
                try { cb?.Invoke(); }
                catch (Exception ex) { _log.LogWarning($"[WgcDisplaySource] Callback error: {ex.Message}"); }
            }
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct FrameInfo
            {
                public int Width;
                public int Height;
                public IntPtr Texture;
                public uint Version;
                public int IsValid;
                public int IsClosed;
                public uint DxgiFormat;
            }

            [DllImport("DesktopBuddyRendererNative", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int DbWgcCreate(IntPtr d3dDevice, IntPtr hwnd, IntPtr monitor, out IntPtr session);

            [DllImport("DesktopBuddyRendererNative", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int DbWgcGetFrameInfo(IntPtr session, out FrameInfo info);

            [DllImport("DesktopBuddyRendererNative", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int DbWgcSetTargetTexture(IntPtr session, IntPtr texture, int width, int height);

            [DllImport("DesktopBuddyRendererNative", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void DbWgcDestroy(IntPtr session);

            [DllImport("DesktopBuddyRendererNative", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private static extern int DbWgcCopyLastError(StringBuilder buffer, int bufferLength);

            internal static void EnsureLoaded()
            {
                if (_loaded) return;

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = string.IsNullOrEmpty(pluginDir)
                    ? "DesktopBuddyRendererNative.dll"
                    : Path.Combine(pluginDir, "DesktopBuddyRendererNative.dll");
                DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Loading native helper: {path}");
                IntPtr module = LoadLibrary(path);
                if (module == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new DllNotFoundException($"Could not load {path}, Win32 error {error}");
                }

                _loaded = true;
                DesktopBuddyRendererPlugin.LogInfo($"[WgcDisplaySource] Native helper loaded: 0x{module.ToInt64():X}");
            }

            internal static string GetLastError()
            {
                var buffer = new StringBuilder(1024);
                int length = DbWgcCopyLastError(buffer, buffer.Capacity);
                return length <= 0 ? "" : buffer.ToString();
            }

            private static bool _loaded;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibrary(string path);
        }
    }
}

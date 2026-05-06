using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    internal static class RendererWgcDevice
    {
        private const int ID3D11Resource_GetDevice = 3;
        private const int ID3D11Device_GetImmediateContext = 40;

        private static readonly object Lock = new object();
        private static Texture2D _probeTexture;
        private static IntPtr _d3dDevice;
        private static IntPtr _d3dContext;
        private static bool _initialized;

        internal static IntPtr D3dDevice => _d3dDevice;
        internal static IntPtr D3dContext => _d3dContext;
        internal static object ContextLock => Lock;
        internal static bool IsReady => _initialized && _d3dDevice != IntPtr.Zero && _d3dContext != IntPtr.Zero;

        internal static bool Initialize(ManualLogSource log)
        {
            Info(log, "[WGC] Initialize entered");
            if (IsReady) return true;

            lock (Lock)
            {
                Info(log, "[WGC] Initialize lock entered");
                if (IsReady) return true;

                try
                {
                    Info(log, "[WGC] Creating Unity probe texture");
                    _probeTexture = new Texture2D(1, 1, TextureFormat.BGRA32, false, true);
                    Info(log, "[WGC] Applying Unity probe texture");
                    _probeTexture.Apply(false, true);

                    Info(log, "[WGC] Getting native texture pointer");
                    IntPtr nativeTexture = _probeTexture.GetNativeTexturePtr();
                    if (nativeTexture == IntPtr.Zero)
                    {
                        Error(log, "[WGC] Unity probe texture native pointer is null");
                        return false;
                    }
                    Info(log, $"[WGC] Probe native texture=0x{nativeTexture.ToInt64():X}");

                    unsafe
                    {
                        Info(log, "[WGC] Reading D3D resource vtable");
                        var resourceVtable = *(IntPtr**)nativeTexture;
                        var getDevice = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)resourceVtable[ID3D11Resource_GetDevice];
                        IntPtr d3dDevice;
                        Info(log, "[WGC] Calling ID3D11Resource.GetDevice");
                        getDevice(nativeTexture, &d3dDevice);
                        _d3dDevice = d3dDevice;

                        if (_d3dDevice == IntPtr.Zero)
                        {
                            Error(log, "[WGC] ID3D11Resource.GetDevice returned null");
                            return false;
                        }
                        Info(log, $"[WGC] D3D device=0x{_d3dDevice.ToInt64():X}");

                        Info(log, "[WGC] Calling ID3D11Device.GetImmediateContext");
                        var deviceVtable = *(IntPtr**)_d3dDevice;
                        var getImmediateContext = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)deviceVtable[ID3D11Device_GetImmediateContext];
                        IntPtr d3dContext;
                        getImmediateContext(_d3dDevice, &d3dContext);
                        _d3dContext = d3dContext;
                        Info(log, $"[WGC] D3D context=0x{_d3dContext.ToInt64():X}");
                    }

                    if (_d3dContext == IntPtr.Zero)
                    {
                        Error(log, "[WGC] ID3D11Device.GetImmediateContext returned null");
                        return false;
                    }

                    Info(log, "[WGC] Enabling multithread protection");
                    EnableMultithreadProtection(log);

                    _initialized = true;
                    Info(log, $"[WGC] Unity D3D11 device ready device=0x{_d3dDevice.ToInt64():X} context=0x{_d3dContext.ToInt64():X}");
                    return true;
                }
                catch (Exception ex)
                {
                    RendererDiagnostics.LogException("[WGC] Renderer device init failed", ex);
                    log?.LogError($"[WGC] Renderer device init failed: {ex}");
                    return false;
                }
            }
        }

        private static unsafe void EnableMultithreadProtection(ManualLogSource log)
        {
            var mtGuid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            if (Marshal.QueryInterface(_d3dDevice, ref mtGuid, out IntPtr mtPtr) < 0 || mtPtr == IntPtr.Zero)
            {
                Info(log, "[WGC] ID3D11Multithread unavailable");
                return;
            }

            try
            {
                var vtable = *(IntPtr**)mtPtr;
                var setProtected = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)vtable[4];
                setProtected(mtPtr, 1, null);
                Info(log, "[WGC] Unity D3D11 multithread protection enabled");
            }
            finally
            {
                Marshal.Release(mtPtr);
            }
        }

        internal static void Dispose()
        {
            lock (Lock)
            {
                RendererDiagnostics.Log("[WGC] Dispose entered");

                if (_d3dContext != IntPtr.Zero)
                {
                    Marshal.Release(_d3dContext);
                    _d3dContext = IntPtr.Zero;
                }

                if (_d3dDevice != IntPtr.Zero)
                {
                    Marshal.Release(_d3dDevice);
                    _d3dDevice = IntPtr.Zero;
                }

                if (_probeTexture != null)
                {
                    UnityEngine.Object.Destroy(_probeTexture);
                    _probeTexture = null;
                }

                _initialized = false;
            }
        }

        private static void Info(ManualLogSource log, string message)
        {
            RendererDiagnostics.Log(message);
            log?.LogInfo(message);
        }

        private static void Error(ManualLogSource log, string message)
        {
            RendererDiagnostics.Log("ERROR " + message);
            log?.LogError(message);
        }
    }
}

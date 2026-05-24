using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal static class UnityD3D11Device
    {
        private const int ID3D11Resource_GetDevice = 3;
        private const int IDXGIDevice_GetAdapter = 7;
        private const int IDXGIAdapter_GetDesc = 8;

        private static readonly object Lock = new object();
        private static IntPtr _d3dDevice;
        private static bool _initialized;
        private static long _adapterLuid;
        private static int _adapterVendorId;
        private static string _adapterDescription;

        internal static IntPtr D3dDevice => _d3dDevice;
        internal static bool IsReady => _initialized && _d3dDevice != IntPtr.Zero;
        internal static long AdapterLuid => _adapterLuid;
        internal static int AdapterVendorId => _adapterVendorId;
        internal static string AdapterDescription => _adapterDescription;
        internal static bool HasAdapterInfo => _adapterLuid != 0;

        internal static bool Initialize(ManualLogSource log)
        {
            Info(log, "[UnityD3D11] Initialize entered");
            if (IsReady) return true;

            lock (Lock)
            {
                Info(log, "[UnityD3D11] Initialize lock entered");
                if (IsReady) return true;

                try
                {
                    Texture2D probeTexture = null;
                    Info(log, "[UnityD3D11] Creating Unity probe texture");
                    try
                    {
                        probeTexture = new Texture2D(1, 1, TextureFormat.BGRA32, false, true);
                        Info(log, "[UnityD3D11] Applying Unity probe texture");
                        probeTexture.Apply(false, true);

                        Info(log, "[UnityD3D11] Getting native texture pointer");
                        IntPtr nativeTexture = probeTexture.GetNativeTexturePtr();
                        if (nativeTexture == IntPtr.Zero)
                        {
                            Error(log, "[UnityD3D11] Unity probe texture native pointer is null");
                            return false;
                        }
                        Info(log, $"[UnityD3D11] Probe native texture=0x{nativeTexture.ToInt64():X}");

                        unsafe
                        {
                            Info(log, "[UnityD3D11] Reading D3D resource vtable");
                            var resourceVtable = *(IntPtr**)nativeTexture;
                            var getDevice = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)resourceVtable[ID3D11Resource_GetDevice];
                            IntPtr d3dDevice;
                            Info(log, "[UnityD3D11] Calling ID3D11Resource.GetDevice");
                            getDevice(nativeTexture, &d3dDevice);
                            _d3dDevice = d3dDevice;
                        }
                    }
                    finally
                    {
                        if (probeTexture != null)
                            UnityEngine.Object.Destroy(probeTexture);
                    }

                    if (_d3dDevice == IntPtr.Zero)
                    {
                        Error(log, "[UnityD3D11] ID3D11Resource.GetDevice returned null");
                        return false;
                    }
                    Info(log, $"[UnityD3D11] D3D device=0x{_d3dDevice.ToInt64():X}");
                    TryReadAdapterInfo(log);

                    _initialized = true;
                    Info(log, $"[UnityD3D11] Unity D3D11 device ready device=0x{_d3dDevice.ToInt64():X} adapter='{_adapterDescription}' vendor=0x{_adapterVendorId:X4} LUID=0x{_adapterLuid:X16}");
                    return true;
                }
                catch (Exception ex)
                {
                    log?.LogError($"[UnityD3D11] Renderer device init failed: {ex}");
                    return false;
                }
            }
        }

        private static unsafe void TryReadAdapterInfo(ManualLogSource log)
        {
            var dxgiDeviceGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            IntPtr dxgiDevice = IntPtr.Zero;
            IntPtr adapter = IntPtr.Zero;
            try
            {
                int hr = Marshal.QueryInterface(_d3dDevice, ref dxgiDeviceGuid, out dxgiDevice);
                if (hr < 0 || dxgiDevice == IntPtr.Zero)
                {
                    Info(log, $"[UnityD3D11] IDXGIDevice QueryInterface failed hr=0x{hr:X8}");
                    return;
                }

                var dxgiVtable = *(IntPtr**)dxgiDevice;
                var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)dxgiVtable[IDXGIDevice_GetAdapter];
                hr = getAdapter(dxgiDevice, &adapter);
                if (hr < 0 || adapter == IntPtr.Zero)
                {
                    Info(log, $"[UnityD3D11] IDXGIDevice.GetAdapter failed hr=0x{hr:X8}");
                    return;
                }

                var adapterVtable = *(IntPtr**)adapter;
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)adapterVtable[IDXGIAdapter_GetDesc];
                DXGI_ADAPTER_DESC desc;
                hr = getDesc(adapter, &desc);
                if (hr < 0)
                {
                    Info(log, $"[UnityD3D11] IDXGIAdapter.GetDesc failed hr=0x{hr:X8}");
                    return;
                }

                _adapterLuid = desc.AdapterLuid;
                _adapterVendorId = unchecked((int)desc.VendorId);
                _adapterDescription = new string(desc.Description).TrimEnd('\0');
                Info(log, $"[UnityD3D11] Adapter '{_adapterDescription}' VendorId=0x{desc.VendorId:X4} LUID=0x{desc.AdapterLuid:X16}");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[UnityD3D11] Failed to read adapter info: {ex.Message}");
            }
            finally
            {
                if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
            }
        }

        internal static void Dispose()
        {
            SharedTextureBridgePlugin.LogInfo($"[UnityD3D11] Dispose ENTER initialized={_initialized} device=0x{_d3dDevice.ToInt64():X}");
            lock (Lock)
            {
                SharedTextureBridgePlugin.LogInfo("[UnityD3D11] Dispose lock entered");
                if (_d3dDevice != IntPtr.Zero)
                {
                    try { Marshal.Release(_d3dDevice); }
                    catch (Exception ex) { SharedTextureBridgePlugin.LogError("[UnityD3D11] Device release failed", ex); }
                    _d3dDevice = IntPtr.Zero;
                }

                _initialized = false;
                _adapterLuid = 0;
                _adapterVendorId = 0;
                _adapterDescription = null;
            }
            SharedTextureBridgePlugin.LogInfo("[UnityD3D11] Dispose EXIT");
        }

        private static void Info(ManualLogSource log, string message)
        {
            log?.LogInfo(message);
        }

        private static void Error(ManualLogSource log, string message)
        {
            log?.LogError(message);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct DXGI_ADAPTER_DESC
        {
            public fixed char Description[128];
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long AdapterLuid;
        }
    }
}

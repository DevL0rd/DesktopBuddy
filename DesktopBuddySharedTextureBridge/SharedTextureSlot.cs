using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Renderite.Unity;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class SharedTextureSlot : IDisplayTextureSource, IDisposable
    {
        private const int ID3D11Device_OpenSharedResource = 28;
        private const int ID3D11Device_CreateShaderResourceView = 7;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;
        private const uint D3D11_SRV_DIMENSION_TEXTURE2D = 4;

        private static readonly Guid Texture2DGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        private readonly ManualLogSource _log;
        private readonly IntPtr _sharedHandle;
        private readonly HashSet<Action> _requests = new HashSet<Action>();

        private IntPtr _openedTexture;
        private IntPtr _shaderResourceView;
        private Texture2D _unityTexture;
        private bool _started;
        private bool _disposed;

        public Texture UnityTexture => _unityTexture;
        public int Width { get; }
        public int Height { get; }
        public int RequestCount => _requests.Count;
        public bool IsValid => !_disposed && _started && _unityTexture != null;
        public string SourceName => "SharedTexture";

        internal SharedTextureSlot(IntPtr sharedHandle, int width, int height, ManualLogSource log)
        {
            _sharedHandle = sharedHandle;
            Width = width;
            Height = height;
            _log = log;
            SharedTextureBridgePlugin.LogInfo($"[SharedTexture] Constructed handle=0x{_sharedHandle.ToInt64():X} {Width}x{Height}");
        }

        public bool TryBind()
        {
            if (_started) return true;
            if (_disposed) return false;
            if (_sharedHandle == IntPtr.Zero || Width <= 0 || Height <= 0)
                return false;

            if (!UnityD3D11Device.IsReady && !UnityD3D11Device.Initialize(_log))
            {
                SharedTextureBridgePlugin.LogWarning("[SharedTexture] Renderer D3D device is not ready");
                return false;
            }

            try
            {
                OpenSharedTexture();
                CreateShaderResourceView();

                _unityTexture = Texture2D.CreateExternalTexture(
                    Width,
                    Height,
                    TextureFormat.BGRA32,
                    false,
                    false,
                    _shaderResourceView);
                _unityTexture.name = $"DesktopBuddy SharedTexture 0x{_sharedHandle.ToInt64():X}";
                _unityTexture.wrapMode = TextureWrapMode.Clamp;

                _started = true;
                SharedTextureBridgePlugin.LogInfo(
                    $"[SharedTexture] Bound handle=0x{_sharedHandle.ToInt64():X} texture=0x{_openedTexture.ToInt64():X} srv=0x{_shaderResourceView.ToInt64():X} {Width}x{Height}");
                NotifyCallbacks();
                return true;
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[SharedTexture] TryBind failed", ex);
                Dispose();
                return false;
            }
        }

        public void RegisterRequest(Action onTextureChanged)
        {
            if (onTextureChanged != null) _requests.Add(onTextureChanged);
            if (_unityTexture != null)
                onTextureChanged?.Invoke();
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

            if (_shaderResourceView != IntPtr.Zero)
            {
                Marshal.Release(_shaderResourceView);
                _shaderResourceView = IntPtr.Zero;
            }

            if (_openedTexture != IntPtr.Zero)
            {
                Marshal.Release(_openedTexture);
                _openedTexture = IntPtr.Zero;
            }

            _requests.Clear();
            SharedTextureBridgePlugin.LogInfo($"[SharedTexture] Disposed handle=0x{_sharedHandle.ToInt64():X}");
        }

        private unsafe void OpenSharedTexture()
        {
            var vtable = *(IntPtr**)UnityD3D11Device.D3dDevice;
            var openSharedResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[ID3D11Device_OpenSharedResource];
            Guid textureGuid = Texture2DGuid;
            IntPtr texture;
            int hr = openSharedResource(UnityD3D11Device.D3dDevice, _sharedHandle, &textureGuid, &texture);
            if (hr < 0 || texture == IntPtr.Zero)
                throw new InvalidOperationException($"OpenSharedResource failed hr=0x{hr:X8} handle=0x{_sharedHandle.ToInt64():X}");

            _openedTexture = texture;
        }

        private unsafe void CreateShaderResourceView()
        {
            var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
                ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D,
                Texture2D = new D3D11_TEX2D_SRV
                {
                    MostDetailedMip = 0,
                    MipLevels = 1
                }
            };

            var vtable = *(IntPtr**)UnityD3D11Device.D3dDevice;
            var createSrv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, D3D11_SHADER_RESOURCE_VIEW_DESC*, IntPtr*, int>)vtable[ID3D11Device_CreateShaderResourceView];
            IntPtr srv;
            int hr = createSrv(UnityD3D11Device.D3dDevice, _openedTexture, &desc, &srv);
            if (hr < 0 || srv == IntPtr.Zero)
                throw new InvalidOperationException($"CreateShaderResourceView failed hr=0x{hr:X8} texture=0x{_openedTexture.ToInt64():X}");

            _shaderResourceView = srv;
        }

        private void NotifyCallbacks()
        {
            foreach (var cb in _requests)
            {
                try { cb?.Invoke(); }
                catch (Exception ex) { _log?.LogWarning($"[SharedTexture] Callback error: {ex.Message}"); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEX2D_SRV
        {
            public uint MostDetailedMip;
            public uint MipLevels;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            public uint Format;
            public uint ViewDimension;
            public D3D11_TEX2D_SRV Texture2D;
        }
    }
}

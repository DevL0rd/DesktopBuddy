using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;

namespace DesktopBuddy;

public sealed partial class WgcCapture
{

    internal readonly struct GpuAdapterInfo
    {
        public readonly string Name;
        public readonly uint VendorId;
        public readonly long Luid;
        public readonly ulong DedicatedVideoMemoryBytes;
        public readonly bool IsBasicRenderDriver;

        public GpuAdapterInfo(string name, uint vendorId, long luid, ulong dedicatedVideoMemoryBytes, bool isBasicRenderDriver)
        {
            Name = name;
            VendorId = vendorId;
            Luid = luid;
            DedicatedVideoMemoryBytes = dedicatedVideoMemoryBytes;
            IsBasicRenderDriver = isBasicRenderDriver;
        }

        public override string ToString() => $"{Name} VendorId=0x{VendorId:X4} VRAM={DedicatedVideoMemoryBytes / 1048576}MB LUID=0x{Luid:X16}";
    }

    private static uint _preferredD3dAdapterVendorId;
    private static readonly object _captureInteropLock = new();
    private static IGraphicsCaptureItemInterop _captureInterop;

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private const int IDXGIFactory_EnumAdapters = 7;
    private const int IDXGIFactory6_EnumAdapterByGpuPreference = 29;
    private const int IDXGIAdapter_GetDesc = 8;
    private const uint DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE = 2;
    private const uint MicrosoftBasicRenderDriverVendorId = 0x1414;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    private const int ID3D11DeviceContext_Flush = 111;
    private const int ID3D11Device_CreateTexture2D = 5;
    private const int ID3D11DeviceContext_CopyResource = 47;

    private const uint DXGI_FORMAT_B8G8R8A8_TYPELESS = 90;
    private const uint D3D11_USAGE_DEFAULT = 0;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_RESOURCE_MISC_SHARED = 0x2;

    private static IntPtr _cachedPreferredAdapter = IntPtr.Zero;
    private static uint _cachedPreferredAdapterVendorId;
    private static long _cachedPreferredAdapterLuid;
    private static bool _adapterCacheReady;
    private static bool _rendererAdapterHintReady;
    private static long _rendererAdapterHintLuid;
    private static uint _rendererAdapterHintVendorId;
    private static string _rendererAdapterHintDescription;
    private static readonly object _adapterCacheLock = new();

    internal static bool SharedD3dDeviceInitialized
    {
        get { return _adapterCacheReady; }
    }

    internal static long SharedD3dAdapterLuid
    {
        get { return _cachedPreferredAdapterLuid; }
    }

    internal static bool HasRendererAdapterHint
    {
        get
        {
            lock (_adapterCacheLock)
                return _rendererAdapterHintReady && _rendererAdapterHintLuid != 0;
        }
    }

    internal static uint RendererAdapterHintVendorId
    {
        get
        {
            lock (_adapterCacheLock)
                return _rendererAdapterHintReady ? _rendererAdapterHintVendorId : 0;
        }
    }

    internal static void SetRendererAdapterHint(long adapterLuid, uint vendorId, string description)
    {
        if (adapterLuid == 0) return;

        bool changed;
        lock (_adapterCacheLock)
        {
            changed = !_rendererAdapterHintReady || _rendererAdapterHintLuid != adapterLuid;
            _rendererAdapterHintReady = true;
            _rendererAdapterHintLuid = adapterLuid;
            _rendererAdapterHintVendorId = vendorId;
            _rendererAdapterHintDescription = description;

            if (_adapterCacheReady && _cachedPreferredAdapterLuid != adapterLuid)
            {
                if (_cachedPreferredAdapter != IntPtr.Zero)
                {
                    try { Marshal.Release(_cachedPreferredAdapter); }
                    catch { }
                }

                _cachedPreferredAdapter = IntPtr.Zero;
                _cachedPreferredAdapterVendorId = 0;
                _cachedPreferredAdapterLuid = 0;
                _adapterCacheReady = false;
                _preferredD3dAdapterVendorId = 0;
            }
        }

        if (changed)
            Log.Msg($"[WgcCapture] Renderer adapter hint: '{description}' VendorId=0x{vendorId:X4} LUID=0x{adapterLuid:X16}");
    }

    public IntPtr D3dContext => _d3dContext;

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame;

    private IntPtr _hwnd;
    private bool _isDesktop;
    private readonly object _d3dLock = new();
    private IDirect3DDevice _winrtDevice;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;
    private TypedEventHandler<GraphicsCaptureItem, object> _itemClosedHandler;

    private volatile bool _closed;
    private int _framesCaptured;
    private int _sharedFramesCopied;
    private volatile bool _disposed;
    private volatile bool _needsPoolRecreate;
    private readonly object _resizeRecreateLock = new();
    private int _resizeRecreateWorkerRunning;
    private int _resizeTargetWidth;
    private int _resizeTargetHeight;

}

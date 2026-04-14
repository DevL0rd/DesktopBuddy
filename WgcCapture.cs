using System;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DesktopBuddy;

public sealed class WgcCapture : IDisposable
{
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
    private const int IDXGIAdapter_GetDesc = 8;

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

    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;

    private static IntPtr _cachedPreferredAdapter = IntPtr.Zero;
    private static bool _adapterCacheReady;
    private static readonly object _adapterCacheLock = new();

    private static unsafe IntPtr FindPreferredAdapter()
    {
        lock (_adapterCacheLock)
        {
            if (_adapterCacheReady)
            {
                if (_cachedPreferredAdapter != IntPtr.Zero)
                    Marshal.AddRef(_cachedPreferredAdapter);
                return _cachedPreferredAdapter;
            }

            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
            if (hr < 0 || factory == IntPtr.Zero) { _adapterCacheReady = true; return IntPtr.Zero; }

            var vtable = *(IntPtr**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[IDXGIFactory_EnumAdapters];

            IntPtr bestAdapter = IntPtr.Zero;
            bool bestIsDiscrete = false;

            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                hr = enumAdapters(factory, i, &adapter);
                if (hr < 0) break;

                var adapterVtable = *(IntPtr**)adapter;
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)adapterVtable[IDXGIAdapter_GetDesc];
                DXGI_ADAPTER_DESC desc;
                getDesc(adapter, &desc);

                bool isDiscrete = desc.VendorId == 0x10DE || desc.VendorId == 0x1002;
                string descStr = new string((char*)desc.Description);
                Log.Msg($"[WgcCapture] Adapter {i}: '{descStr}' VendorId=0x{desc.VendorId:X4} VRAM={desc.DedicatedVideoMemory / 1048576}MB{(isDiscrete ? " [discrete]" : "")}");

                if (isDiscrete && !bestIsDiscrete)
                {
                    if (bestAdapter != IntPtr.Zero) Marshal.Release(bestAdapter);
                    bestAdapter = adapter;
                    bestIsDiscrete = true;
                }
                else
                {
                    if (bestAdapter == IntPtr.Zero) bestAdapter = adapter;
                    else Marshal.Release(adapter);
                }
            }

            Marshal.Release(factory);

            _cachedPreferredAdapter = bestAdapter;
            if (_cachedPreferredAdapter != IntPtr.Zero)
                Marshal.AddRef(_cachedPreferredAdapter);
            _adapterCacheReady = true;
            return bestAdapter;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private static unsafe int CallCreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice)
    {
        var lib = LoadLibraryW("d3d11.dll");
        var proc = GetProcAddress(lib, "CreateDirect3D11DeviceFromDXGIDevice");
        if (proc == IntPtr.Zero) { graphicsDevice = IntPtr.Zero; return -1; }

        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)proc;
        IntPtr result;
        int hr = fn(dxgiDevice, &result);
        graphicsDevice = result;
        return hr;
    }

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    private const int ID3D11Device_CreateTexture2D = 5;
    private const int ID3D11Device_GetDeviceRemovedReason = 38;
    private const int ID3D11DeviceContext_Map = 14;
    private const int ID3D11DeviceContext_Unmap = 15;
    private const int ID3D11DeviceContext_CopyResource = 47;
    private const int ID3D11DeviceContext_ClearState = 110;
    private const int ID3D11DeviceContext_Flush = 111;

    // Compute pipeline vtable slots
    private const int ID3D11Device_CreateBuffer = 3;
    private const int ID3D11Device_CreateShaderResourceView = 7;
    private const int ID3D11Device_CreateUnorderedAccessView = 8;
    private const int ID3D11Device_CreateComputeShader = 18;
    private const int ID3D11DeviceContext_Dispatch = 41;
    private const int ID3D11DeviceContext_UpdateSubresource = 48;
    private const int ID3D11DeviceContext_CSSetShaderResources = 67;
    private const int ID3D11DeviceContext_CSSetUnorderedAccessViews = 68;
    private const int ID3D11DeviceContext_CSSetShader = 69;
    private const int ID3D11DeviceContext_CSSetConstantBuffers = 71;

    private const int DXGI_FORMAT_R32_TYPELESS = 39;
    private const int DXGI_FORMAT_R32_UINT = 42;
    private const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;
    private const uint D3D11_BIND_UNORDERED_ACCESS = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    // D3D11_SRV_DIMENSION_TEXTURE2D = 4; union is 16 bytes (largest member is Texture2DArray with 4 uints)
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint Field0, Field1, Field2, Field3;
    }

    // D3D11_UAV_DIMENSION_TEXTURE2D = 2; union is 12 bytes (Buffer has 3 uints)
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_UNORDERED_ACCESS_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint Field0, Field1, Field2;
    }

    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_USAGE_DEFAULT = 0;
    private const int D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame;


    private IntPtr _hwnd;
    private bool _isDesktop;
    private IDirect3DDevice _winrtDevice;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;

    private volatile bool _closed;
    private int _lastWidth, _lastHeight;
    private int _framesCaptured;
    private volatile bool _disposed;
    private volatile bool _needsPoolRecreate;

    // Compute shader pipeline — created once in Init, never recreated
    private IntPtr _computeShader;
    private IntPtr _constantBuffer;
    private IntPtr _convertedTexture;
    private IntPtr _convertedUav;
    private IntPtr _convertedStaging;
    private int _csTexW, _csTexH;

    private DesktopTextureSource _textureTarget;

    private static readonly Guid DxgiAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid TexGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
    public IntPtr D3dDevice => _d3dDevice;
    public bool IsValid => !_disposed && !_closed && _item != null && (_isDesktop || (IsWindow(_hwnd) && !IsIconic(_hwnd)));

    public void RecreatePoolIfNeeded()
    {
        if (!_needsPoolRecreate || _disposed) return;
        lock (_disposeLock)
        {
            if (!_needsPoolRecreate || _disposed) return;
            try
            {
                _framePool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                    new SizeInt32 { Width = Width, Height = Height });
                _needsPoolRecreate = false;
                Log.Msg($"[WgcCapture] FramePool recreated for {Width}x{Height}");
            }
            catch (Exception ex)
            {
                _needsPoolRecreate = false;
                Log.Msg($"[WgcCapture] FramePool.Recreate failed: {ex.Message}");
            }
        }
    }

    public bool Init(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _isDesktop = hwnd == IntPtr.Zero;
        try
        {
            uint deviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

            IntPtr preferredAdapter = FindPreferredAdapter();
            int driverType = preferredAdapter != IntPtr.Zero ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE;
            int hr = D3D11CreateDevice(preferredAdapter, driverType, IntPtr.Zero,
                deviceFlags, IntPtr.Zero, 0, 7,
                out _d3dDevice, out _, out _d3dContext);
            if (preferredAdapter != IntPtr.Zero) Marshal.Release(preferredAdapter);
            if (hr < 0) { Log.Msg($"[WgcCapture] D3D11CreateDevice failed hr=0x{hr:X8}"); return false; }

            var mtGuid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            if (Marshal.QueryInterface(_d3dDevice, ref mtGuid, out IntPtr mtPtr) >= 0)
            {
                unsafe
                {
                    var vtable = *(IntPtr**)mtPtr;
                    var setProtFn = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)vtable[4];
                    setProtFn(mtPtr, 1, null);
                }
                Marshal.Release(mtPtr);
                Log.Msg("[WgcCapture] D3D11 multithread protection enabled");
            }

            Log.Msg("[WgcCapture] D3D11 device created");
            LoadComputeShader();

            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.QueryInterface(_d3dDevice, ref dxgiGuid, out IntPtr dxgiDevice);

            hr = CallCreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            Marshal.Release(dxgiDevice);
            if (hr < 0 || inspectable == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}");
                return false;
            }

            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);

            if (hwnd == IntPtr.Zero)
            {
                IntPtr hMon = monitorHandle != default ? monitorHandle : MonitorFromPoint(0, 0, 1);
                Log.Msg($"[WgcCapture] Creating capture for monitor 0x{hMon:X} (explicit={monitorHandle != default})");
                _item = CreateItemForMonitor(hMon);
            }
            else
            {
                _item = CreateItemForWindow(hwnd);
            }

            if (_item == null) { Log.Msg("[WgcCapture] CaptureItem is null"); return false; }

            _item.Closed += (sender, args) => { _closed = true; };

            Width = _item.Size.Width;
            Height = _item.Size.Height;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Msg($"[WgcCapture] IsBorderRequired not supported (Win11+ only): {ex.Message}"); }
            _session.IsCursorCaptureEnabled = true;

            _session.StartCapture();

            Log.Msg($"[WgcCapture] Init complete: {Width}x{Height}, hwnd={hwnd}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] Init failed: {ex}");
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        WindowsCreateString(className, className.Length, out IntPtr hstring);
        RoGetActivationFactory(hstring, ref iid, out IntPtr factory);
        WindowsDeleteString(hstring);
        return factory;
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForWindow(hwnd, ref itemGuid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref itemGuid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        lock (_disposeLock)
        {
        if (_disposed) return;
        try
        {
        var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        var size = frame.ContentSize;
        int w = size.Width;
        int h = size.Height;
        if (w <= 0 || h <= 0) { frame.Dispose(); return; }

        if (_needsPoolRecreate) { frame.Dispose(); return; }

        if (w != Width || h != Height)
        {
            Log.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
            Width = w; Height = h;
            _needsPoolRecreate = true;
            frame.Dispose();
            return;
        }

        IntPtr surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        frame.Dispose();
        if (surfaceAbi == IntPtr.Zero) return;

        var dxgiAccessGuid = DxgiAccessGuid;
        int qiHr = Marshal.QueryInterface(surfaceAbi, ref dxgiAccessGuid, out IntPtr dxgiAccessPtr);
        Marshal.Release(surfaceAbi);
        if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) return;

        IntPtr srcTexture;
        unsafe
        {
            var vtable = *(IntPtr**)dxgiAccessPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            Guid localTexGuid = TexGuid;
            IntPtr tex;
            int getHr = fn(dxgiAccessPtr, &localTexGuid, &tex);
            srcTexture = tex;
            if (getHr < 0) { Marshal.Release(dxgiAccessPtr); return; }
        }
        Marshal.Release(dxgiAccessPtr);

        try
        {
            using (DesktopBuddyMod.Perf.Time("queue_frame"))
            {
                var gpuCb = OnGpuFrame;
                try { gpuCb?.Invoke(_d3dDevice, srcTexture, w, h); }
                catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
            }

            var tex = _textureTarget;
            if (tex != null && !tex.IsDestroyed)
            {
                EnsureConvertedTexture(w, h);
                GpuConvert(srcTexture, w, h);
                EnsureConvertedStaging(w, h);
                ContextCopyResource(_d3dContext, _convertedStaging, _convertedTexture);

                D3D11_MAPPED_SUBRESOURCE mapped = default;
                int hr;
                using (DesktopBuddyMod.Perf.Time("gpu_readback"))
                    hr = ContextMap(_d3dContext, _convertedStaging, 0, 1, 0, ref mapped);

                if (hr >= 0)
                {
                    try
                    {
                        using (DesktopBuddyMod.Perf.Time("bitmap_copy"))
                            tex.WriteFrameDirect(mapped.pData, (int)mapped.RowPitch, w, h);
                    }
                    finally { ContextUnmap(_d3dContext, _convertedStaging, 0); }
                }
                else
                {
                    Log.Msg($"[WgcCapture] Map failed hr=0x{hr:X8}");
                }
            }

            _lastWidth = w; _lastHeight = h; _framesCaptured++;
            if (_framesCaptured == 1) Log.Msg($"[WgcCapture] First frame: {w}x{h}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] OnFrameArrived error: {ex.Message}");
        }
        finally { Marshal.Release(srcTexture); }
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] OnFrameArrived OUTER error: {ex.Message}\n{ex.StackTrace}");
        }
        }
    }

    public void SetTextureTarget(DesktopTextureSource tex) => _textureTarget = tex;

    private unsafe bool LoadComputeShader()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            byte[] csoBytes;
            using (var stream = asm.GetManifestResourceStream("DesktopBuddy.Shaders.BgraToRgba.cso"))
            {
                if (stream == null) { Log.Msg("[WgcCapture] Shader resource not found"); return false; }
                csoBytes = new byte[stream.Length];
                stream.Read(csoBytes, 0, csoBytes.Length);
            }

            var devVtable = *(IntPtr**)_d3dDevice;

            // Compute shader — loaded once, permanent
            var createCS = (delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, out IntPtr, int>)devVtable[ID3D11Device_CreateComputeShader];
            int hr;
            fixed (byte* bytecode = csoBytes)
                hr = createCS(_d3dDevice, bytecode, (nuint)csoBytes.Length, IntPtr.Zero, out _computeShader);
            if (hr < 0) { Log.Msg($"[WgcCapture] CreateComputeShader hr=0x{hr:X8}"); return false; }

            // Constant buffer: { uint Width; uint Height; } padded to 16 bytes
            var cbDesc = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_CONSTANT_BUFFER };
            var createBuf = (delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_BUFFER_DESC, IntPtr, out IntPtr, int>)devVtable[ID3D11Device_CreateBuffer];
            hr = createBuf(_d3dDevice, ref cbDesc, IntPtr.Zero, out _constantBuffer);
            if (hr < 0) { Log.Msg($"[WgcCapture] CreateBuffer (cbuf) hr=0x{hr:X8}"); return false; }

            Log.Msg("[WgcCapture] Compute shader pipeline ready");
            return true;
        }
        catch (Exception ex) { Log.Msg($"[WgcCapture] LoadComputeShader: {ex.Message}"); return false; }
    }

    private unsafe void EnsureConvertedTexture(int w, int h)
    {
        if (_convertedTexture != IntPtr.Zero && _csTexW == w && _csTexH == h) return;

        if (_convertedUav != IntPtr.Zero) { Marshal.Release(_convertedUav); _convertedUav = IntPtr.Zero; }
        if (_convertedTexture != IntPtr.Zero) { Marshal.Release(_convertedTexture); _convertedTexture = IntPtr.Zero; }

        // R32_TYPELESS texture written by compute shader via R32_UINT UAV
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_R32_TYPELESS,
            SampleCount = 1, SampleQuality = 0,
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_UNORDERED_ACCESS,
        };
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _convertedTexture);

        var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT_R32_UINT,
            ViewDimension = 4,  // D3D11_UAV_DIMENSION_TEXTURE2D
            Field0 = 0,         // MipSlice = 0
        };
        var devVtable = *(IntPtr**)_d3dDevice;
        var createUav = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref D3D11_UNORDERED_ACCESS_VIEW_DESC, out IntPtr, int>)devVtable[ID3D11Device_CreateUnorderedAccessView];
        int hr = createUav(_d3dDevice, _convertedTexture, ref uavDesc, out _convertedUav);
        if (hr < 0) throw new System.Runtime.InteropServices.COMException("CreateUnorderedAccessView failed", hr);

        // Upload new dimensions to constant buffer
        uint* cbData = stackalloc uint[4];
        cbData[0] = (uint)w; cbData[1] = (uint)h;
        var ctxVtable = *(IntPtr**)_d3dContext;
        var updateRes = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint*, uint, uint, void>)ctxVtable[ID3D11DeviceContext_UpdateSubresource];
        updateRes(_d3dContext, _constantBuffer, 0, IntPtr.Zero, cbData, 0, 0);

        _csTexW = w; _csTexH = h;
        Log.Msg($"[WgcCapture] Compute texture {w}x{h} ready");
    }

    private void EnsureConvertedStaging(int w, int h)
    {
        if (_convertedStaging != IntPtr.Zero && _csTexW == w && _csTexH == h) return;
        if (_convertedStaging != IntPtr.Zero) { Marshal.Release(_convertedStaging); _convertedStaging = IntPtr.Zero; }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_R32_TYPELESS,
            SampleCount = 1, SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _convertedStaging);
    }

    private unsafe void GpuConvert(IntPtr srcTexture, int w, int h)
    {
        if (_computeShader == IntPtr.Zero) return;

        // SRV: BGRA source — hardware swizzles B8G8R8A8 reads to RGBA float4 in shader
        var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            ViewDimension = 4,  // D3D11_SRV_DIMENSION_TEXTURE2D
            Field0 = 0,         // MostDetailedMip = 0
            Field1 = 1,         // MipLevels = 1
        };
        var devVtable = *(IntPtr**)_d3dDevice;
        var createSrv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref D3D11_SHADER_RESOURCE_VIEW_DESC, out IntPtr, int>)devVtable[ID3D11Device_CreateShaderResourceView];
        int hr = createSrv(_d3dDevice, srcTexture, ref srvDesc, out IntPtr srv);
        if (hr < 0) { Log.Msg($"[WgcCapture] CreateSRV hr=0x{hr:X8}"); return; }

        try
        {
            var vtable = *(IntPtr**)_d3dContext;

            var setShader = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)vtable[ID3D11DeviceContext_CSSetShader];
            setShader(_d3dContext, _computeShader, IntPtr.Zero, 0);

            var setSrv = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetShaderResources];
            setSrv(_d3dContext, 0, 1, &srv);

            IntPtr uav = _convertedUav;
            var setUav = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)vtable[ID3D11DeviceContext_CSSetUnorderedAccessViews];
            setUav(_d3dContext, 0, 1, &uav, null);

            IntPtr cb = _constantBuffer;
            var setCb = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetConstantBuffers];
            setCb(_d3dContext, 0, 1, &cb);

            var dispatch = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)vtable[ID3D11DeviceContext_Dispatch];
            dispatch(_d3dContext, (uint)((w + 15) / 16), (uint)((h + 15) / 16), 1);
        }
        finally { Marshal.Release(srv); }
    }

    private unsafe void CheckDevice(string context)
    {
        var vtable = *(IntPtr**)_d3dDevice;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[ID3D11Device_GetDeviceRemovedReason];
        int hr = fn(_d3dDevice);
        if (hr < 0)
            Log.Msg($"[WgcCapture] DEVICE REMOVED after {context}: hr=0x{hr:X8}");
    }

    private static unsafe void ContextCopyResource(IntPtr context, IntPtr dst, IntPtr src)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[ID3D11DeviceContext_CopyResource];
        fn(context, dst, src);
    }

    private static unsafe int ContextMap(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, ref D3D11_MAPPED_SUBRESOURCE, int>)vtable[ID3D11DeviceContext_Map];
        return fn(context, resource, subresource, mapType, mapFlags, ref mapped);
    }

    private static unsafe void ContextUnmap(IntPtr context, IntPtr resource, uint subresource)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtable[ID3D11DeviceContext_Unmap];
        fn(context, resource, subresource);
    }

    private static unsafe void DeviceCreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_TEXTURE2D_DESC, IntPtr, out IntPtr, int>)vtable[ID3D11Device_CreateTexture2D];
        int hr = fn(device, ref desc, initialData, out texture);
        if (hr < 0) throw new COMException("CreateTexture2D failed", hr);
    }

    private readonly object _disposeLock = new();

    public object D3dContextLock => _disposeLock;


    public unsafe void FlushD3dContext()
    {
        lock (_disposeLock)
        {
            if (_disposed || _d3dContext == IntPtr.Zero) return;
            try
            {
                var vtable = *(IntPtr**)_d3dContext;
                var clearFn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[ID3D11DeviceContext_ClearState];
                clearFn(_d3dContext);
                var flushFn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[ID3D11DeviceContext_Flush];
                flushFn(_d3dContext);
                Log.Msg("[WgcCapture] D3D11 ClearState+Flush OK");
            }
            catch (Exception ex) { Log.Msg($"[WgcCapture] D3D11 flush error: {ex.Message}"); }
        }
    }

    public void StopCapture()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Log.Msg($"[WgcCapture:StopCapture] Stopping session hwnd={_hwnd}");
        try { if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived; } catch (Exception ex) { Log.Msg($"[WgcCapture:StopCapture] Unhook error: {ex.Message}"); }
        
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _session = null;
        _framePool = null;
        Log.Msg("[WgcCapture:StopCapture] Session stopped, events unhooked");
    }

    public void Dispose()
    {
        bool alreadyStopped;
        lock (_disposeLock)
        {
            alreadyStopped = _disposed;
            _disposed = true;
        }

        if (!alreadyStopped)
        {
            Log.Msg($"[WgcCapture:Dispose] Unhooking events");
            try { if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived; }
            catch (Exception ex) { Log.Msg($"[WgcCapture:Dispose] Unhook error: {ex.Message}"); }

            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }
            _session = null;
            _framePool = null;
        }
        _item = null;

        Log.Msg($"[WgcCapture:Dispose] Releasing GPU resources");
        if (_convertedUav != IntPtr.Zero) { Marshal.Release(_convertedUav); _convertedUav = IntPtr.Zero; }
        if (_convertedTexture != IntPtr.Zero) { Marshal.Release(_convertedTexture); _convertedTexture = IntPtr.Zero; }
        if (_convertedStaging != IntPtr.Zero) { Marshal.Release(_convertedStaging); _convertedStaging = IntPtr.Zero; }
        if (_constantBuffer != IntPtr.Zero) { Marshal.Release(_constantBuffer); _constantBuffer = IntPtr.Zero; }
        if (_computeShader != IntPtr.Zero) { Marshal.Release(_computeShader); _computeShader = IntPtr.Zero; }
        Log.Msg($"[WgcCapture:Dispose] GPU resources released");

        _winrtDevice = null;
        bool forceGC = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.ImmediateGC) ?? true;
        if (forceGC)
        {
            Log.Msg($"[WgcCapture:Dispose] Forcing GC to finalize orphaned WinRT wrappers");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        else
        {
            Log.Msg($"[WgcCapture:Dispose] Immediate GC disabled, WinRT wrappers will finalize later");
        }

        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
        Log.Msg($"[WgcCapture:Dispose] D3D device released");

    }
}

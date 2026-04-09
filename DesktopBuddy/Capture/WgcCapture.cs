using System;
using System.Runtime.InteropServices;
using System.Threading;
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

    private static unsafe IntPtr FindPreferredAdapter()
    {
        var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero) return IntPtr.Zero;

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
        return bestAdapter;
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

    private const int ID3D11Device_CreateBuffer = 3;
    private const int ID3D11Device_CreateTexture2D = 5;
    private const int ID3D11Device_CreateShaderResourceView = 7;
    private const int ID3D11Device_CreateUnorderedAccessView = 8;
    private const int ID3D11Device_CreateComputeShader = 18;
    private const int ID3D11Device_GetDeviceRemovedReason = 38;
    private const int ID3D11DeviceContext_Map = 14;
    private const int ID3D11DeviceContext_Unmap = 15;
    private const int ID3D11DeviceContext_Dispatch = 41;
    private const int ID3D11DeviceContext_CopyResource = 47;
    private const int ID3D11DeviceContext_ClearState = 110;
    private const int ID3D11DeviceContext_Flush = 111;
    private const int ID3D11DeviceContext_CSSetShaderResources = 67;
    private const int ID3D11DeviceContext_CSSetUnorderedAccessViews = 68;
    private const int ID3D11DeviceContext_CSSetShader = 69;
    private const int ID3D11DeviceContext_CSSetConstantBuffers = 71;

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

    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int DXGI_FORMAT_R32_UINT = 42;
    private const int D3D11_USAGE_DEFAULT = 0;
    private const int D3D11_USAGE_STAGING = 3;
    private const int D3D11_USAGE_DYNAMIC = 2;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_CPU_ACCESS_WRITE = 0x10000;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_BIND_UNORDERED_ACCESS = 0x80;
    private const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint Pad0, Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_UNORDERED_ACCESS_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint MipSlice;
        public uint Pad0, Pad1;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuConstants
    {
        public uint Width;
        public uint Height;
        public uint Pad0, Pad1;
    }

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame;


    private IntPtr _hwnd;
    private bool _isDesktop;
    private IDirect3DDevice _winrtDevice;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;

    private IntPtr _stagingTexture;
    private int _stagingTexW, _stagingTexH;
    private IntPtr _encodeTexture0, _encodeTexture1;
    private int _encodeTexFlip;
    private int _encodeTexW, _encodeTexH;
    private byte[] _buffer;
    private GCHandle _pinnedBuffer;
    private readonly object _frameLock = new();
    private volatile bool _frameReady;
    private volatile bool _closed;
    private int _lastWidth, _lastHeight;
    private int _framesCaptured;
    private volatile bool _disposed;
    private volatile bool _needsPoolRecreate;

    private IntPtr _computeShader;
    private IntPtr _convertedTexture;
    private IntPtr _convertedSRV;
    private IntPtr _sourceSRV;
    private IntPtr _destUAV;
    private IntPtr _constantBuffer;
    private int _gpuConvertW, _gpuConvertH;
    private bool _gpuConvertReady;
    private int _lastConstW, _lastConstH;

    private static readonly Guid DxgiAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid TexGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
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
            EnsureEncodeTexture(w, h);
            int slot = _encodeTexFlip;
            IntPtr encodeTex = slot == 0 ? _encodeTexture0 : _encodeTexture1;
            _encodeTexFlip = 1 - slot;
            ContextCopyResource(_d3dContext, encodeTex, srcTexture);

            using (DesktopBuddyMod.Perf.Time("nvenc_encode"))
            {
                var gpuCb = OnGpuFrame;
                try { gpuCb?.Invoke(_d3dDevice, encodeTex, w, h); }
                catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
            }

            EnsureGpuConvertPipeline(w, h);
            EnsureStagingTexture(w, h);

            if (!_gpuConvertReady)
            {
                Log.Msg("[WgcCapture] GPU pipeline not ready, skipping frame");
                return;
            }

            using (DesktopBuddyMod.Perf.Time("gpu_convert"))
                GpuConvertBgraToRgba(srcTexture, w, h);
            ContextCopyResource(_d3dContext, _stagingTexture, _convertedTexture);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            int hr;
            using (DesktopBuddyMod.Perf.Time("gpu_readback"))
                hr = ContextMap(_d3dContext, _stagingTexture, 0, 1, 0, ref mapped);
            if (hr < 0) { Log.Msg($"[WgcCapture] Map failed hr=0x{hr:X8}"); return; }

            try
            {
                int bufSize = w * h * 4;
                int srcPitch = (int)mapped.RowPitch;
                int dstStride = w * 4;

                lock (_frameLock)
                {
                    EnsureBuffer(bufSize);

                    using (DesktopBuddyMod.Perf.Time("cpu_memcpy"))
                    {
                        unsafe
                        {
                            byte* src = (byte*)mapped.pData;
                            fixed (byte* dst = _buffer)
                            {
                                if (srcPitch == dstStride)
                                {
                                    Buffer.MemoryCopy(src, dst, bufSize, bufSize);
                                }
                                else
                                {
                                    for (int y = 0; y < h; y++)
                                        Buffer.MemoryCopy(src + y * srcPitch, dst + y * dstStride, dstStride, dstStride);
                                }
                            }
                        }
                    }

                    _lastWidth = w; _lastHeight = h;
                    _frameReady = true; _framesCaptured++;
                }
                if (_framesCaptured == 1) Log.Msg($"[WgcCapture] First frame ready: {w}x{h}, GPU compute shader active");

            }
            finally { ContextUnmap(_d3dContext, _stagingTexture, 0); }
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

    public byte[] TakeFrame(out int width, out int height)
    {
        if (!_frameReady)
        {
            width = 0;
            height = 0;
            return null;
        }

        lock (_frameLock)
        {
            _frameReady = false;
            width = _lastWidth;
            height = _lastHeight;
            return _buffer;
        }
    }

    private void EnsureEncodeTexture(int w, int h)
    {
        if (_encodeTexture0 != IntPtr.Zero && w == _encodeTexW && h == _encodeTexH) return;
        if (_encodeTexture0 != IntPtr.Zero) { Marshal.Release(_encodeTexture0); _encodeTexture0 = IntPtr.Zero; }
        if (_encodeTexture1 != IntPtr.Zero) { Marshal.Release(_encodeTexture1); _encodeTexture1 = IntPtr.Zero; }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, SampleQuality = 0,
            Usage = 0,
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _encodeTexture0);
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _encodeTexture1);
        _encodeTexW = w; _encodeTexH = h;
        _encodeTexFlip = 0;
    }

    private void EnsureStagingTexture(int w, int h)
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            if (w == _stagingTexW && h == _stagingTexH) return;
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_R32_UINT,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _stagingTexture);
        _stagingTexW = w; _stagingTexH = h;
    }

    private void EnsureBuffer(int size)
    {
        if (_buffer != null && _buffer.Length == size) return;
        if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        _buffer = new byte[size];
        _pinnedBuffer = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    private unsafe void CheckDevice(string context)
    {
        var vtable = *(IntPtr**)_d3dDevice;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[ID3D11Device_GetDeviceRemovedReason];
        int hr = fn(_d3dDevice);
        if (hr < 0)
            Log.Msg($"[WgcCapture] DEVICE REMOVED after {context}: hr=0x{hr:X8}");
    }

    private void EnsureGpuConvertPipeline(int w, int h)
    {
        if (_gpuConvertReady && w == _gpuConvertW && h == _gpuConvertH) return;

        ReleaseGpuConvertResources();

        try
        {
            if (_computeShader == IntPtr.Zero)
            {
                var asm = typeof(WgcCapture).Assembly;
                using var stream = asm.GetManifestResourceStream("DesktopBuddy.Shaders.BgraToRgba.cso");
                if (stream == null) { Log.Msg("[WgcCapture] GPU shader not found in resources, falling back to CPU"); return; }
                var bytecode = new byte[stream.Length];
                stream.Read(bytecode, 0, bytecode.Length);

                _computeShader = DeviceCreateComputeShader(_d3dDevice, bytecode);
                Log.Msg($"[WgcCapture] GPU compute shader created ({bytecode.Length} bytes)");
            }

            var texDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_R32_UINT,
                SampleCount = 1, SampleQuality = 0,
                Usage = D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_UNORDERED_ACCESS,
                CPUAccessFlags = 0, MiscFlags = 0
            };
            DeviceCreateTexture2D(_d3dDevice, ref texDesc, IntPtr.Zero, out _convertedTexture);
            Log.Msg($"[GPU] UAV texture created: 0x{_convertedTexture:X}");

            var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
            {
                Format = DXGI_FORMAT_R32_UINT,
                ViewDimension = 4,
                MipSlice = 0
            };
            _destUAV = DeviceCreateUAV(_d3dDevice, _convertedTexture, ref uavDesc);
            Log.Msg($"[GPU] UAV created: 0x{_destUAV:X}");

            _constantBuffer = DeviceCreateConstantBuffer(_d3dDevice, 16);
            Log.Msg($"[GPU] Constant buffer created: 0x{_constantBuffer:X}");

            var srvTexDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1, SampleQuality = 0,
                Usage = D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = 0, MiscFlags = 0
            };
            DeviceCreateTexture2D(_d3dDevice, ref srvTexDesc, IntPtr.Zero, out var srvTexture);

            var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                ViewDimension = 4,
                MostDetailedMip = 0,
                MipLevels = 1
            };
            _sourceSRV = DeviceCreateSRV(_d3dDevice, srvTexture, ref srvDesc);
            _convertedSRV = srvTexture;
            Log.Msg($"[GPU] SRV texture created: 0x{srvTexture:X}, SRV: 0x{_sourceSRV:X}");

            _gpuConvertW = w;
            _gpuConvertH = h;
            _gpuConvertReady = true;
            Log.Msg($"[WgcCapture] GPU convert pipeline ready: {w}x{h}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] GPU convert pipeline failed: {ex.Message}, falling back to CPU");
            ReleaseGpuConvertResources();
        }
    }

    private int _gpuDispatchCount;

    private unsafe void GpuConvertBgraToRgba(IntPtr srcTexture, int w, int h)
    {
        bool verbose = _gpuDispatchCount < 3;
        _gpuDispatchCount++;

        if (verbose) Log.Msg($"[GPU] Dispatch #{_gpuDispatchCount}: {w}x{h} src=0x{srcTexture:X} srvTex=0x{_convertedSRV:X} shader=0x{_computeShader:X}");

        ContextCopyResource(_d3dContext, _convertedSRV, srcTexture);
        if (verbose) { CheckDevice("CopyResource->SRV"); Log.Msg("[GPU] CopyResource to SRV texture OK"); }

        if (w != _lastConstW || h != _lastConstH)
        {
            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            int hr = ContextMap(_d3dContext, _constantBuffer, 0, 4, 0, ref mapped);
            if (hr < 0) { Log.Msg($"[GPU] ConstantBuffer Map failed hr=0x{hr:X8}"); return; }
            var constants = (GpuConstants*)mapped.pData;
            constants->Width = (uint)w;
            constants->Height = (uint)h;
            ContextUnmap(_d3dContext, _constantBuffer, 0);
            _lastConstW = w;
            _lastConstH = h;
            if (verbose) Log.Msg("[GPU] Constants updated");
        }

        var vtable = *(IntPtr**)_d3dContext;

        var csSetShader = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)vtable[ID3D11DeviceContext_CSSetShader];
        csSetShader(_d3dContext, _computeShader, IntPtr.Zero, 0);
        if (verbose) Log.Msg("[GPU] CSSetShader OK");

        IntPtr srv = _sourceSRV;
        var csSetSRV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetShaderResources];
        csSetSRV(_d3dContext, 0, 1, &srv);
        if (verbose) Log.Msg("[GPU] CSSetShaderResources OK");

        IntPtr uav = _destUAV;
        var csSetUAV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, IntPtr, void>)vtable[ID3D11DeviceContext_CSSetUnorderedAccessViews];
        csSetUAV(_d3dContext, 0, 1, &uav, IntPtr.Zero);
        if (verbose) Log.Msg("[GPU] CSSetUnorderedAccessViews OK");

        IntPtr cb = _constantBuffer;
        var csSetCB = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetConstantBuffers];
        csSetCB(_d3dContext, 0, 1, &cb);
        if (verbose) Log.Msg("[GPU] CSSetConstantBuffers OK");

        uint groupsX = ((uint)w + 15) / 16, groupsY = ((uint)h + 15) / 16;
        if (verbose) Log.Msg($"[GPU] Dispatching {groupsX}x{groupsY}x1...");
        var dispatch = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)vtable[ID3D11DeviceContext_Dispatch];
        dispatch(_d3dContext, groupsX, groupsY, 1);
        if (verbose) { CheckDevice("Dispatch"); Log.Msg("[GPU] Dispatch OK"); }

        IntPtr nullPtr = IntPtr.Zero;
        csSetSRV(_d3dContext, 0, 1, &nullPtr);
        csSetUAV(_d3dContext, 0, 1, &nullPtr, IntPtr.Zero);
        if (verbose) Log.Msg("[GPU] Unbind OK, dispatch complete");
    }

    private void ReleaseGpuConvertResources(bool disposing = false)
    {
        _gpuConvertReady = false;
        if (disposing)
        {
            _destUAV = IntPtr.Zero;
            _sourceSRV = IntPtr.Zero;
            _convertedSRV = IntPtr.Zero;
            _convertedTexture = IntPtr.Zero;
            _constantBuffer = IntPtr.Zero;
        }
        else
        {
            if (_destUAV != IntPtr.Zero) { Marshal.Release(_destUAV); _destUAV = IntPtr.Zero; }
            if (_sourceSRV != IntPtr.Zero) { Marshal.Release(_sourceSRV); _sourceSRV = IntPtr.Zero; }
            if (_convertedSRV != IntPtr.Zero) { Marshal.Release(_convertedSRV); _convertedSRV = IntPtr.Zero; }
            if (_convertedTexture != IntPtr.Zero) { Marshal.Release(_convertedTexture); _convertedTexture = IntPtr.Zero; }
            if (_constantBuffer != IntPtr.Zero) { Marshal.Release(_constantBuffer); _constantBuffer = IntPtr.Zero; }
        }
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

    private static unsafe IntPtr DeviceCreateComputeShader(IntPtr device, byte[] bytecode)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, nuint, IntPtr, out IntPtr, int>)vtable[ID3D11Device_CreateComputeShader];
        fixed (byte* pBytecode = bytecode)
        {
            int hr = fn(device, (IntPtr)pBytecode, (nuint)bytecode.Length, IntPtr.Zero, out IntPtr shader);
            if (hr < 0) throw new COMException($"CreateComputeShader failed hr=0x{hr:X8}", hr);
            return shader;
        }
    }

    private static unsafe IntPtr DeviceCreateSRV(IntPtr device, IntPtr resource, ref D3D11_SHADER_RESOURCE_VIEW_DESC desc)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref D3D11_SHADER_RESOURCE_VIEW_DESC, out IntPtr, int>)vtable[ID3D11Device_CreateShaderResourceView];
        int hr = fn(device, resource, ref desc, out IntPtr srv);
        if (hr < 0) throw new COMException($"CreateShaderResourceView failed hr=0x{hr:X8}", hr);
        return srv;
    }

    private static unsafe IntPtr DeviceCreateUAV(IntPtr device, IntPtr resource, ref D3D11_UNORDERED_ACCESS_VIEW_DESC desc)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref D3D11_UNORDERED_ACCESS_VIEW_DESC, out IntPtr, int>)vtable[ID3D11Device_CreateUnorderedAccessView];
        int hr = fn(device, resource, ref desc, out IntPtr uav);
        if (hr < 0) throw new COMException($"CreateUnorderedAccessView failed hr=0x{hr:X8}", hr);
        return uav;
    }

    private static unsafe IntPtr DeviceCreateConstantBuffer(IntPtr device, uint size)
    {
        var vtable = *(IntPtr**)device;
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = size,
            Usage = D3D11_USAGE_DYNAMIC,
            BindFlags = D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = D3D11_CPU_ACCESS_WRITE,
            MiscFlags = 0, StructureByteStride = 0
        };
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_BUFFER_DESC, IntPtr, out IntPtr, int>)vtable[ID3D11Device_CreateBuffer];
        int hr = fn(device, ref desc, IntPtr.Zero, out IntPtr buffer);
        if (hr < 0) throw new COMException($"CreateBuffer failed hr=0x{hr:X8}", hr);
        return buffer;
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

            _session = null;
            _framePool = null;
        }
        _item = null;

        Log.Msg($"[WgcCapture:Dispose] Releasing GPU resources");
        ReleaseGpuConvertResources(disposing: false);
        if (_computeShader != IntPtr.Zero) { Marshal.Release(_computeShader); _computeShader = IntPtr.Zero; }
        if (_encodeTexture0 != IntPtr.Zero) { Marshal.Release(_encodeTexture0); _encodeTexture0 = IntPtr.Zero; }
        if (_encodeTexture1 != IntPtr.Zero) { Marshal.Release(_encodeTexture1); _encodeTexture1 = IntPtr.Zero; }
        if (_stagingTexture != IntPtr.Zero) { Marshal.Release(_stagingTexture); _stagingTexture = IntPtr.Zero; }
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

        if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        _buffer = null;
        Log.Msg($"[WgcCapture] Disposed hwnd={_hwnd}");
    }
}

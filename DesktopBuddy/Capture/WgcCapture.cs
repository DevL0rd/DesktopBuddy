using System;
using System.Runtime.InteropServices;
using System.Threading;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DesktopBuddy;

/// <summary>
/// Windows.Graphics.Capture based screen/window capture.
/// GPU-accelerated, per-window, no GDI overhead.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    // COM interop for creating capture items from HWND without picker
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    // Access underlying DXGI interface from WinRT surface
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

    // D3D11 native interop
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string pSrcData, int srcDataSize,
        IntPtr pSourceName, IntPtr pDefines, IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1, uint flags2,
        out IntPtr ppCode, out IntPtr ppErrorMsgs);

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

    // ID3D11Device vtable indices (verified against d3d11.h 10.0.19041.0)
    private const int ID3D11Device_CreateBuffer = 3;
    private const int ID3D11Device_CreateTexture2D = 5;
    private const int ID3D11Device_CreateShaderResourceView = 7;
    private const int ID3D11Device_CreateUnorderedAccessView = 8;
    private const int ID3D11Device_CreateComputeShader = 18;
    private const int ID3D11Device_GetDeviceRemovedReason = 38;
    // ID3D11DeviceContext vtable indices (verified against d3d11.h 10.0.19041.0)
    private const int ID3D11DeviceContext_Map = 14;
    private const int ID3D11DeviceContext_Unmap = 15;
    private const int ID3D11DeviceContext_Dispatch = 41;
    private const int ID3D11DeviceContext_CopyResource = 47;
    private const int ID3D11DeviceContext_CSSetShaderResources = 67;
    private const int ID3D11DeviceContext_CSSetUnorderedAccessViews = 68;
    private const int ID3D11DeviceContext_CSSetShader = 69;
    private const int ID3D11DeviceContext_CSSetConstantBuffers = 71;

    // D3D11 structures
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format; // DXGI_FORMAT
        public uint SampleCount, SampleQuality;
        public int Usage; // D3D11_USAGE
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
    private struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    private const int DXGI_FORMAT_R32_UINT = 42;
    private const int D3D11_USAGE_DEFAULT = 0;
    private const int D3D11_USAGE_STAGING = 3;
    private const int D3D11_USAGE_DYNAMIC = 2;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_CPU_ACCESS_WRITE = 0x10000;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_BIND_UNORDERED_ACCESS = 0x80;
    private const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;

    // Structures for compute shader views
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        public int Format;
        public int ViewDimension; // D3D11_SRV_DIMENSION_TEXTURE2D = 4
        public uint MostDetailedMip;
        public uint MipLevels;
        // remaining union fields (unused for Texture2D with single mip)
        public uint Pad0, Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_UNORDERED_ACCESS_VIEW_DESC
    {
        public int Format;
        public int ViewDimension; // D3D11_UAV_DIMENSION_TEXTURE2D = 2
        public uint MipSlice;
        // remaining union fields
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
        public uint Pad0, Pad1; // 16-byte alignment for constant buffer
    }

    /// <summary>
    /// Called with (d3dDevice, srcTexture, width, height) for each captured frame.
    /// Runs on WGC background thread. Texture only valid during callback.
    /// </summary>
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
    private IntPtr _encodeTexture; // Persistent GPU texture for NVENC encoding + keepalive
    private int _encodeTexW, _encodeTexH;
    private byte[] _buffer;
    private GCHandle _pinnedBuffer;
    private readonly object _frameLock = new();
    private volatile bool _frameReady;
    private volatile bool _closed;
    private int _lastWidth, _lastHeight;
    private int _framesCaptured;
    private volatile bool _disposed;

    // GPU compute shader pipeline for BGRA→RGBA + Y-flip
    private IntPtr _computeShader;
    private IntPtr _convertedTexture; // R8G8B8A8_UNORM output from compute shader
    private IntPtr _convertedSRV;     // Not used for output, but we need SRV on source
    private IntPtr _sourceSRV;        // SRV for source BGRA texture
    private IntPtr _destUAV;          // UAV for destination RGBA texture
    private IntPtr _constantBuffer;   // Width/Height constants
    private int _gpuConvertW, _gpuConvertH;
    private bool _gpuConvertReady;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
    public bool IsValid => !_disposed && !_closed && _item != null && (_isDesktop || (IsWindow(_hwnd) && !IsIconic(_hwnd)));

    /// <summary>
    /// Initialize WGC capture for a window (hwnd) or entire desktop (hwnd=IntPtr.Zero uses primary monitor).
    /// </summary>
    public bool Init(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _isDesktop = hwnd == IntPtr.Zero;
        try
        {
            // BGRA support required for WGC frame surfaces. Debug layer disabled — it causes
            // assertion crashes when D3D context is used from multiple threads during disposal.
            uint deviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                deviceFlags, IntPtr.Zero, 0, 7,
                out _d3dDevice, out _, out _d3dContext);
            if (hr < 0) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] D3D11CreateDevice failed hr=0x{hr:X8}"); return false; }
            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] D3D11 device created");

            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.QueryInterface(_d3dDevice, ref dxgiGuid, out IntPtr dxgiDevice);

            hr = CallCreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            Marshal.Release(dxgiDevice);
            if (hr < 0 || inspectable == IntPtr.Zero)
            {
                ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}");
                return false;
            }

            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);

            if (hwnd == IntPtr.Zero)
            {
                var hMon = MonitorFromPoint(0, 0, 1);
                ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Creating capture for monitor 0x{hMon:X}");
                _item = CreateItemForMonitor(hMon);
            }
            else
            {
                _item = CreateItemForWindow(hwnd);
            }

            if (_item == null) { ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] CaptureItem is null"); return false; }

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
            _session.IsBorderRequired = false;
            _session.IsCursorCaptureEnabled = true;

            _session.StartCapture();

            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Init complete: {Width}x{Height}, hwnd={hwnd}");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Init failed: {ex.Message}");
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

    private int _frameLog;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        lock (_disposeLock)
        {
        if (_disposed) return;
        try
        {
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        var size = frame.ContentSize;
        int w = size.Width;
        int h = size.Height;
        _frameLog++;
        if (w <= 0 || h <= 0) return;

        if (w != Width || h != Height)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
            Width = w; Height = h;
            _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new SizeInt32 { Width = w, Height = h });
            return; // Skip this frame
        }

        IntPtr surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        if (surfaceAbi == IntPtr.Zero) return;

        var dxgiAccessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        int qiHr = Marshal.QueryInterface(surfaceAbi, ref dxgiAccessGuid, out IntPtr dxgiAccessPtr);
        if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) return;

        var texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        IntPtr srcTexture;
        unsafe
        {
            var vtable = *(IntPtr**)dxgiAccessPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            Guid localTexGuid = texGuid;
            IntPtr tex;
            int getHr = fn(dxgiAccessPtr, &localTexGuid, &tex);
            srcTexture = tex;
            if (getHr < 0) { Marshal.Release(dxgiAccessPtr); return; }
        }
        Marshal.Release(dxgiAccessPtr);

        try
        {
            // Copy to persistent encode texture (for NVENC + keepalive re-encoding)
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            EnsureEncodeTexture(w, h);
            ContextCopyResource(_d3dContext, _encodeTexture, srcTexture);

            // GPU encode callback — NVENC encodes from the persistent copy
            using (DesktopBuddyMod.Perf.Time("nvenc_encode"))
            {
                var gpuCb = OnGpuFrame;
                try { gpuCb?.Invoke(_d3dDevice, _encodeTexture, w, h); }
                catch (Exception gpuEx) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
            }

            // GPU BGRA→RGBA + Y-flip via compute shader, then copy to staging for CPU readback
            EnsureGpuConvertPipeline(w, h);
            EnsureStagingTexture(w, h);

            using (DesktopBuddyMod.Perf.Time("gpu_convert"))
                GpuConvertBgraToRgba(srcTexture, w, h);
            ContextCopyResource(_d3dContext, _stagingTexture, _convertedTexture);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            int hr;
            using (DesktopBuddyMod.Perf.Time("gpu_readback"))
                hr = ContextMap(_d3dContext, _stagingTexture, 0, 1, 0, ref mapped);
            if (hr < 0) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Map failed hr=0x{hr:X8}"); return; }

            try
            {
                int bufSize = w * h * 4;
                EnsureBuffer(bufSize);
                int srcPitch = (int)mapped.RowPitch;
                int dstStride = w * 4;

                // GPU already did BGRA→RGBA + Y-flip, just memcpy rows (handle pitch)
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

                lock (_frameLock)
                {
                    _lastWidth = w; _lastHeight = h;
                    _frameReady = true; _framesCaptured++;
                }
                if (_framesCaptured == 1) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] First frame ready: {w}x{h}, GPU compute shader active");

            }
            finally { ContextUnmap(_d3dContext, _stagingTexture, 0); }
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnFrameArrived error: {ex.Message}");
        }
        finally { Marshal.Release(srcTexture); }
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnFrameArrived OUTER error: {ex.Message}\n{ex.StackTrace}");
        }
        } // lock (_disposeLock)
    }

    /// <summary>
    /// Take the latest captured frame. Returns BGRA pixel data or null if no new frame.
    /// Buffer is valid until next call.
    /// </summary>
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
        if (_encodeTexture != IntPtr.Zero && w == _encodeTexW && h == _encodeTexH) return;
        if (_encodeTexture != IntPtr.Zero) { Marshal.Release(_encodeTexture); _encodeTexture = IntPtr.Zero; }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, SampleQuality = 0,
            Usage = 0, // D3D11_USAGE_DEFAULT — GPU read/write
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _encodeTexture);
        _encodeTexW = w; _encodeTexH = h;

    }

    private long _lastFrameTicks;

    private void EnsureStagingTexture(int w, int h)
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            if (w == _lastWidth && h == _lastHeight) return;
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_R32_UINT, // Matches GPU compute shader output
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _stagingTexture);
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
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] DEVICE REMOVED after {context}: hr=0x{hr:X8}");
    }

    // GPU compute shader setup for BGRA→RGBA + Y-flip
    private void EnsureGpuConvertPipeline(int w, int h)
    {
        if (_gpuConvertReady && w == _gpuConvertW && h == _gpuConvertH) return;

        // Release old resources if size changed
        ReleaseGpuConvertResources();

        try
        {
            // Load compiled shader from embedded resource
            if (_computeShader == IntPtr.Zero)
            {
                var asm = typeof(WgcCapture).Assembly;
                using var stream = asm.GetManifestResourceStream("DesktopBuddy.Shaders.BgraToRgba.cso");
                if (stream == null) { ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] GPU shader not found in resources, falling back to CPU"); return; }
                var bytecode = new byte[stream.Length];
                stream.Read(bytecode, 0, bytecode.Length);

                _computeShader = DeviceCreateComputeShader(_d3dDevice, bytecode);
                ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] GPU compute shader created ({bytecode.Length} bytes)");
            }

            // Create RGBA output texture (DEFAULT usage, UAV bindable)
            var texDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_R32_UINT, // R32_UINT for uint read/write in shader
                SampleCount = 1, SampleQuality = 0,
                Usage = D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_UNORDERED_ACCESS,
                CPUAccessFlags = 0, MiscFlags = 0
            };
            DeviceCreateTexture2D(_d3dDevice, ref texDesc, IntPtr.Zero, out _convertedTexture);
            ResoniteModLoader.ResoniteMod.Msg($"[GPU] UAV texture created: 0x{_convertedTexture:X}");

            // Create UAV on destination
            var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
            {
                Format = DXGI_FORMAT_R32_UINT,
                ViewDimension = 4, // D3D11_UAV_DIMENSION_TEXTURE2D
                MipSlice = 0
            };
            _destUAV = DeviceCreateUAV(_d3dDevice, _convertedTexture, ref uavDesc);
            ResoniteModLoader.ResoniteMod.Msg($"[GPU] UAV created: 0x{_destUAV:X}");

            _constantBuffer = DeviceCreateConstantBuffer(_d3dDevice, 16);
            ResoniteModLoader.ResoniteMod.Msg($"[GPU] Constant buffer created: 0x{_constantBuffer:X}");

            // Create a SRV-bindable copy of source texture — same BGRA format so CopyResource works
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
                ViewDimension = 4, // D3D11_SRV_DIMENSION_TEXTURE2D
                MostDetailedMip = 0,
                MipLevels = 1
            };
            _sourceSRV = DeviceCreateSRV(_d3dDevice, srvTexture, ref srvDesc);
            _convertedSRV = srvTexture;
            ResoniteModLoader.ResoniteMod.Msg($"[GPU] SRV texture created: 0x{srvTexture:X}, SRV: 0x{_sourceSRV:X}");

            _gpuConvertW = w;
            _gpuConvertH = h;
            _gpuConvertReady = true;
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] GPU convert pipeline ready: {w}x{h}");
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] GPU convert pipeline failed: {ex.Message}, falling back to CPU");
            ReleaseGpuConvertResources();
        }
    }

    private int _gpuDispatchCount;

    private unsafe void GpuConvertBgraToRgba(IntPtr srcTexture, int w, int h)
    {
        bool verbose = _gpuDispatchCount < 3; // Log first 3 dispatches in detail
        _gpuDispatchCount++;

        if (verbose) ResoniteModLoader.ResoniteMod.Msg($"[GPU] Dispatch #{_gpuDispatchCount}: {w}x{h} src=0x{srcTexture:X} srvTex=0x{_convertedSRV:X} shader=0x{_computeShader:X}");

        // Copy source BGRA texture to our SRV-bindable texture (same format, reinterpreted as R32_UINT)
        ContextCopyResource(_d3dContext, _convertedSRV, srcTexture);
        if (verbose) { CheckDevice("CopyResource->SRV"); ResoniteModLoader.ResoniteMod.Msg("[GPU] CopyResource to SRV texture OK"); }

        // Update constant buffer with dimensions
        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        int hr = ContextMap(_d3dContext, _constantBuffer, 0, 4, 0, ref mapped); // D3D11_MAP_WRITE_DISCARD = 4
        if (hr < 0) { ResoniteModLoader.ResoniteMod.Msg($"[GPU] ConstantBuffer Map failed hr=0x{hr:X8}"); return; }
        var constants = (GpuConstants*)mapped.pData;
        constants->Width = (uint)w;
        constants->Height = (uint)h;
        ContextUnmap(_d3dContext, _constantBuffer, 0);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] Constants updated");

        // Dispatch compute shader
        var vtable = *(IntPtr**)_d3dContext;

        var csSetShader = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)vtable[ID3D11DeviceContext_CSSetShader];
        csSetShader(_d3dContext, _computeShader, IntPtr.Zero, 0);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] CSSetShader OK");

        IntPtr srv = _sourceSRV;
        var csSetSRV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetShaderResources];
        csSetSRV(_d3dContext, 0, 1, &srv);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] CSSetShaderResources OK");

        IntPtr uav = _destUAV;
        var csSetUAV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, IntPtr, void>)vtable[ID3D11DeviceContext_CSSetUnorderedAccessViews];
        csSetUAV(_d3dContext, 0, 1, &uav, IntPtr.Zero);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] CSSetUnorderedAccessViews OK");

        IntPtr cb = _constantBuffer;
        var csSetCB = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[ID3D11DeviceContext_CSSetConstantBuffers];
        csSetCB(_d3dContext, 0, 1, &cb);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] CSSetConstantBuffers OK");

        uint groupsX = ((uint)w + 15) / 16, groupsY = ((uint)h + 15) / 16;
        if (verbose) ResoniteModLoader.ResoniteMod.Msg($"[GPU] Dispatching {groupsX}x{groupsY}x1...");
        var dispatch = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)vtable[ID3D11DeviceContext_Dispatch];
        dispatch(_d3dContext, groupsX, groupsY, 1);
        if (verbose) { CheckDevice("Dispatch"); ResoniteModLoader.ResoniteMod.Msg("[GPU] Dispatch OK"); }

        // Unbind resources
        IntPtr nullPtr = IntPtr.Zero;
        csSetSRV(_d3dContext, 0, 1, &nullPtr);
        csSetUAV(_d3dContext, 0, 1, &nullPtr, IntPtr.Zero);
        if (verbose) ResoniteModLoader.ResoniteMod.Msg("[GPU] Unbind OK, dispatch complete");
    }

    private void ReleaseGpuConvertResources()
    {
        _gpuConvertReady = false;
        if (_destUAV != IntPtr.Zero) { Marshal.Release(_destUAV); _destUAV = IntPtr.Zero; }
        if (_sourceSRV != IntPtr.Zero) { Marshal.Release(_sourceSRV); _sourceSRV = IntPtr.Zero; }
        if (_convertedSRV != IntPtr.Zero) { Marshal.Release(_convertedSRV); _convertedSRV = IntPtr.Zero; }
        if (_convertedTexture != IntPtr.Zero) { Marshal.Release(_convertedTexture); _convertedTexture = IntPtr.Zero; }
        if (_constantBuffer != IntPtr.Zero) { Marshal.Release(_constantBuffer); _constantBuffer = IntPtr.Zero; }
    }

    // D3D11 vtable calls via raw COM
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

    // Serializes access to the D3D11 immediate context. Used by OnFrameArrived (WGC thread)
    // and shared with FfmpegEncoder's encode thread. D3D11 contexts are NOT thread-safe.
    private readonly object _disposeLock = new();

    /// <summary>
    /// Lock object that serializes D3D11 immediate context access.
    /// Pass this to FfmpegEncoder.Initialize so the encode thread doesn't race with OnFrameArrived.
    /// </summary>
    public object D3dContextLock => _disposeLock;

    public void Dispose()
    {
        // Acquire _disposeLock to: (1) atomically check+set _disposed, and
        // (2) wait for any in-flight OnFrameArrived to finish before we touch anything.
        // After this lock releases, _disposed is volatile-true so new callbacks bail immediately.
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture:Dispose] === START === hwnd={_hwnd}");

        // Release WGC references — do NOT call Dispose() on session/framePool.
        // WinRT's IClosable.Close() on these objects can crash with a native access violation
        // when called from the ThreadPool thread. Instead, null the references and let the
        // GC finalizer handle the release. The capture is already stopped (_disposed = true
        // and lock barrier ensures no callbacks are running).
        _session = null;
        _framePool = null;
        _item = null;
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] WGC session/pool references released");

        // Release GPU resources — no lock needed, all callbacks are stopped
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] Releasing GPU resources");
        ReleaseGpuConvertResources();
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] GPU convert resources released");
        if (_computeShader != IntPtr.Zero) { Marshal.Release(_computeShader); _computeShader = IntPtr.Zero; }
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] Compute shader released");
        if (_encodeTexture != IntPtr.Zero) { Marshal.Release(_encodeTexture); _encodeTexture = IntPtr.Zero; }
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] Encode texture released");
        if (_stagingTexture != IntPtr.Zero) { Marshal.Release(_stagingTexture); _stagingTexture = IntPtr.Zero; }
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] Staging texture released");
        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] D3D context released");
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] D3D device released");

        ResoniteModLoader.ResoniteMod.Msg("[WgcCapture:Dispose] Disposing WinRT device");
        try { _winrtDevice?.Dispose(); } catch (Exception ex) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture:Dispose] winrtDevice dispose error: {ex.Message}"); }

        if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        _buffer = null;
        ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture:Dispose] === DONE === hwnd={_hwnd}");
    }
}

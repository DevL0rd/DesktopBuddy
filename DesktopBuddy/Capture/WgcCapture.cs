using System;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;

namespace DesktopBuddy;

public sealed class WgcCapture : IDisposable
{
    private static readonly object _sharedD3dLock = new();
    private static bool _sharedD3dReady;
    private static IntPtr _sharedD3dDevice;
    private static IntPtr _sharedD3dContext;
    private static IDirect3DDevice _sharedWinrtDevice;
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

    private const int ID3D11DeviceContext_ClearState = 110;
    private const int ID3D11DeviceContext_Flush = 111;
    private const int ID3D11Device_CreateTexture2D = 5;
    private const int ID3D11DeviceContext_CopyResource = 47;

    private const uint DXGI_FORMAT_B8G8R8A8_TYPELESS = 90;
    private const uint D3D11_USAGE_DEFAULT = 0;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_RESOURCE_MISC_SHARED = 0x2;

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

    internal static IntPtr SharedD3dDevice
    {
        get
        {
            EnsureSharedD3dDevice();
            return _sharedD3dDevice;
        }
    }

    internal static object SharedD3dContextLock => _sharedD3dLock;

    internal static bool PrewarmSharedDevice() => EnsureSharedD3dDevice();

    internal static bool PrewarmCaptureFactory() => EnsureCaptureInterop();

    private static bool EnsureSharedD3dDevice()
    {
        lock (_sharedD3dLock)
        {
            if (_sharedD3dReady) return true;

            uint deviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            IntPtr preferredAdapter = FindPreferredAdapter();
            int driverType = preferredAdapter != IntPtr.Zero ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE;
            int hr = D3D11CreateDevice(preferredAdapter, driverType, IntPtr.Zero,
                deviceFlags, IntPtr.Zero, 0, 7,
                out _sharedD3dDevice, out _, out _sharedD3dContext);
            if (preferredAdapter != IntPtr.Zero) Marshal.Release(preferredAdapter);
            if (hr < 0)
            {
                Log.Msg($"[WgcCapture] Shared D3D11CreateDevice failed hr=0x{hr:X8}");
                return false;
            }

            var mtGuid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            if (Marshal.QueryInterface(_sharedD3dDevice, ref mtGuid, out IntPtr mtPtr) >= 0)
            {
                unsafe
                {
                    var vtable = *(IntPtr**)mtPtr;
                    var setProtFn = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)vtable[4];
                    setProtFn(mtPtr, 1, null);
                }
                Marshal.Release(mtPtr);
                Log.Msg("[WgcCapture] Shared D3D11 multithread protection enabled");
            }

            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            hr = Marshal.QueryInterface(_sharedD3dDevice, ref dxgiGuid, out IntPtr dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] Shared IDXGIDevice QueryInterface failed hr=0x{hr:X8}");
                return false;
            }

            hr = CallCreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            Marshal.Release(dxgiDevice);
            if (hr < 0 || inspectable == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] Shared CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}");
                return false;
            }

            _sharedWinrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            _sharedD3dReady = true;
            Log.Msg($"[WgcCapture] Shared D3D11 device ready 0x{_sharedD3dDevice:X}");
            return true;
        }
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
    private TypedEventHandler<GraphicsCaptureItem, object> _itemClosedHandler;

    private volatile bool _closed;
    private int _framesCaptured;
    private volatile bool _disposed;
    private volatile bool _needsPoolRecreate;

    private static readonly Guid DxgiAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid TexGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiResourceGuid = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
    public IntPtr D3dDevice => _d3dDevice;
    public IntPtr SharedTexture => _sharedTexture;
    public IntPtr SharedTextureHandle { get; private set; }
    public int SharedTextureWidth { get; private set; }
    public int SharedTextureHeight { get; private set; }
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
                TryCreateSharedTexture(Width, Height);
                _needsPoolRecreate = false;
                Log.Msg($"[WgcCapture] FramePool/shared texture recreated for {Width}x{Height}");
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
            if (!EnsureSharedD3dDevice()) return false;
            _d3dDevice = _sharedD3dDevice;
            _d3dContext = _sharedD3dContext;
            _winrtDevice = _sharedWinrtDevice;

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

            _itemClosedHandler = (_, _) => { _closed = true; };
            _item.Closed += _itemClosedHandler;

            Width = _item.Size.Width;
            Height = _item.Size.Height;
            TryCreateSharedTexture(Width, Height);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Msg($"[WgcCapture] IsBorderRequired not supported (Win11+ only): {ex.Message}"); }
            TrySetIncludeSecondaryWindows(_session);
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

    private static void TrySetIncludeSecondaryWindows(GraphicsCaptureSession session)
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IncludeSecondaryWindows"))
            {
                Log.Msg("[WgcCapture] IncludeSecondaryWindows unsupported on this Windows API");
                return;
            }

            session.IncludeSecondaryWindows = true;
            Log.Msg("[WgcCapture] IncludeSecondaryWindows enabled");
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] IncludeSecondaryWindows failed: {ex.Message}");
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

    private static bool EnsureCaptureInterop()
    {
        lock (_captureInteropLock)
        {
            if (_captureInterop != null) return true;

            var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
            if (factoryPtr == IntPtr.Zero)
            {
                Log.Msg("[WgcCapture] GraphicsCaptureItem activation factory unavailable");
                return false;
            }

            _captureInterop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            Log.Msg("[WgcCapture] GraphicsCaptureItem interop factory ready");
            return true;
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        if (!EnsureCaptureInterop()) return null;
        try
        {
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var ptr = _captureInterop.CreateForWindow(hwnd, ref itemGuid);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
            Marshal.Release(ptr);
            return item;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] CreateForWindow failed: {ex.Message}");
            return null;
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        if (!EnsureCaptureInterop()) return null;
        try
        {
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var ptr = _captureInterop.CreateForMonitor(hmon, ref itemGuid);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
            Marshal.Release(ptr);
            return item;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] CreateForMonitor failed: {ex.Message}");
            return null;
        }
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
            CopyFrameToSharedTexture(srcTexture, w, h);

            using (DesktopBuddyMod.Perf.Time("queue_frame"))
            {
                var gpuCb = OnGpuFrame;
                try { gpuCb?.Invoke(_d3dDevice, srcTexture, w, h); }
                catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
            }

            _framesCaptured++;
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

    private readonly object _disposeLock = new();
    private IntPtr _sharedTexture;

    private unsafe void TryCreateSharedTexture(int width, int height)
    {
        if (_d3dDevice == IntPtr.Zero || width <= 0 || height <= 0)
            return;

        if (_sharedTexture != IntPtr.Zero && SharedTextureWidth == width && SharedTextureHeight == height)
            return;

        ReleaseSharedTexture();

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_TYPELESS,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        lock (_sharedD3dLock)
        {
            var vtable = *(IntPtr**)_d3dDevice;
            var createTexture = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtable[ID3D11Device_CreateTexture2D];
            IntPtr texture;
            int hr = createTexture(_d3dDevice, &desc, IntPtr.Zero, &texture);
            if (hr < 0 || texture == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] Shared texture CreateTexture2D failed hr=0x{hr:X8} size={width}x{height}");
                return;
            }

            var dxgiGuid = DxgiResourceGuid;
            hr = Marshal.QueryInterface(texture, ref dxgiGuid, out IntPtr dxgiResource);
            if (hr < 0 || dxgiResource == IntPtr.Zero)
            {
                Marshal.Release(texture);
                Log.Msg($"[WgcCapture] Shared texture IDXGIResource QueryInterface failed hr=0x{hr:X8}");
                return;
            }

            try
            {
                var dxgiVtable = *(IntPtr**)dxgiResource;
                var getSharedHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)dxgiVtable[8];
                IntPtr handle;
                hr = getSharedHandle(dxgiResource, &handle);
                if (hr < 0 || handle == IntPtr.Zero)
                {
                    Marshal.Release(texture);
                    Log.Msg($"[WgcCapture] GetSharedHandle failed hr=0x{hr:X8}");
                    return;
                }

                _sharedTexture = texture;
                SharedTextureHandle = handle;
                SharedTextureWidth = width;
                SharedTextureHeight = height;
                Log.Msg($"[WgcCapture] Shared texture ready handle=0x{handle:X} ptr=0x{texture:X} {width}x{height}");
            }
            finally
            {
                Marshal.Release(dxgiResource);
            }
        }
    }

    private unsafe void CopyFrameToSharedTexture(IntPtr srcTexture, int width, int height)
    {
        if (_sharedTexture == IntPtr.Zero || srcTexture == IntPtr.Zero)
            return;
        if (width != SharedTextureWidth || height != SharedTextureHeight)
            return;

        lock (_sharedD3dLock)
        {
            var vtable = *(IntPtr**)_d3dContext;
            var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[ID3D11DeviceContext_CopyResource];
            copyResource(_d3dContext, _sharedTexture, srcTexture);
        }
    }

    private void ReleaseSharedTexture()
    {
        SharedTextureHandle = IntPtr.Zero;
        SharedTextureWidth = 0;
        SharedTextureHeight = 0;
        if (_sharedTexture != IntPtr.Zero)
        {
            try { Marshal.Release(_sharedTexture); }
            catch { }
            _sharedTexture = IntPtr.Zero;
        }
    }

    public object D3dContextLock => _sharedD3dLock;

    public unsafe void FlushD3dContext()
    {
        lock (_sharedD3dLock)
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
        try { if (_item != null && _itemClosedHandler != null) _item.Closed -= _itemClosedHandler; } catch (Exception ex) { Log.Msg($"[WgcCapture:StopCapture] Item closed unhook error: {ex.Message}"); }

        // Do not explicitly dispose CsWinRT capture wrappers here. The crash evidence
        // points at WinRT.IObjectReference finalizers, so let the projection release
        // its wrappers naturally while the raw D3D refs remain alive.
        _session = null;
        _framePool = null;
        _itemClosedHandler = null;
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
            try { if (_item != null && _itemClosedHandler != null) _item.Closed -= _itemClosedHandler; }
            catch (Exception ex) { Log.Msg($"[WgcCapture:Dispose] Item closed unhook error: {ex.Message}"); }

            _session = null;
            _framePool = null;
        }
        _itemClosedHandler = null;
        _item = null;
        OnGpuFrame = null;

        _winrtDevice = null;
        _d3dDevice = IntPtr.Zero;
        _d3dContext = IntPtr.Zero;
        ReleaseSharedTexture();
        Log.Msg($"[WgcCapture:Dispose] Detached from shared D3D device hwnd={_hwnd}");
    }
}

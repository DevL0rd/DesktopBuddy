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
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    private const int ID3D11DeviceContext_ClearState = 110;
    private const int ID3D11DeviceContext_Flush = 111;

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

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame;

    private IntPtr _hwnd;
    private bool _isDesktop;
    private IDirect3DDevice _winrtDevice;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;
    private IDisposable _lastSurfaceObj;

    private volatile bool _closed;
    private int _framesCaptured;
    private volatile bool _disposed;
    private volatile bool _needsPoolRecreate;

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
        try
        {
            var pool = _framePool;
            if (pool != null && !_disposed)
            {
                pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                    new SizeInt32 { Width = Width, Height = Height });
                Log.Msg($"[WgcCapture] FramePool recreated for {Width}x{Height}");
            }
            _needsPoolRecreate = false;
        }
        catch (Exception ex)
        {
            _needsPoolRecreate = false;
            Log.Msg($"[WgcCapture] FramePool.Recreate failed: {ex.Message}");
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
        Marshal.ReleaseComObject(interop);
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
        Marshal.ReleaseComObject(interop);
        return item;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
        var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        try
        {
            var size = frame.ContentSize;
            int w = size.Width;
            int h = size.Height;
            if (w <= 0 || h <= 0) return;

            if (_needsPoolRecreate) return;

            if (w != Width || h != Height)
            {
                Log.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
                Width = w; Height = h;
                _needsPoolRecreate = true;
                return;
            }

            var surfaceObj = frame.Surface;
            try
            {
                IntPtr surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(surfaceObj);
                if (surfaceAbi == IntPtr.Zero) return;

                var dxgiAccessGuid = DxgiAccessGuid;
                int qiHr = Marshal.QueryInterface(surfaceAbi, ref dxgiAccessGuid, out IntPtr dxgiAccessPtr);
                Marshal.Release(surfaceAbi);
                if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) return;

                IntPtr srcTexture = IntPtr.Zero;
                try
                {
                    unsafe
                    {
                        var vtable = *(IntPtr**)dxgiAccessPtr;
                        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                        Guid localTexGuid = TexGuid;
                        IntPtr tex;
                        int getHr = fn(dxgiAccessPtr, &localTexGuid, &tex);
                        if (getHr >= 0) srcTexture = tex;
                    }
                }
                finally
                {
                    Marshal.Release(dxgiAccessPtr);
                }

                if (srcTexture == IntPtr.Zero) return;

                try
                {
                    using (DesktopBuddyMod.Perf.Time("queue_frame"))
                    {
                        var gpuCb = OnGpuFrame;
                        try { gpuCb?.Invoke(_d3dDevice, srcTexture, w, h); }
                        catch (Exception gpuEx) { Log.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }
                    }

                    _framesCaptured++;
                    if (_framesCaptured == 1) Log.Msg($"[WgcCapture] First frame: {w}x{h}");
                }
                finally 
                { 
                    Marshal.Release(srcTexture); 
                }

                var lso = _lastSurfaceObj;
                try { lso?.Dispose(); } catch { }
                if (lso != null) GC.SuppressFinalize(lso);
                
                _lastSurfaceObj = (IDisposable)surfaceObj;
                surfaceObj = null;
            }
            finally
            {
                if (surfaceObj != null)
                {
                    try { ((IDisposable)surfaceObj).Dispose(); } catch { }
                    GC.SuppressFinalize(surfaceObj);
                }
            }
        }
        finally
        {
            frame.Dispose();
            GC.SuppressFinalize(frame);
        }
        }
        catch (Exception ex)
        {
            Log.Msg($"[WgcCapture] OnFrameArrived OUTER error: {ex.Message}\n{ex.StackTrace}");
        }
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

        var lso = _lastSurfaceObj;
        var s = _session;
        var f = _framePool;

        try { lso?.Dispose(); } catch { }
        if (lso != null) GC.SuppressFinalize(lso);
        _lastSurfaceObj = null;

        try { s?.Dispose(); } catch { }
        if (s != null) GC.SuppressFinalize(s);
        _session = null;

        try { f?.Dispose(); } catch { }
        if (f != null) GC.SuppressFinalize(f);
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

            var lso = _lastSurfaceObj;
            var s = _session;
            var f = _framePool;

            try { lso?.Dispose(); } catch { }
            if (lso != null) GC.SuppressFinalize(lso);
            _lastSurfaceObj = null;

            try { s?.Dispose(); } catch { }
            if (s != null) GC.SuppressFinalize(s);
            _session = null;

            try { f?.Dispose(); } catch { }
            if (f != null) GC.SuppressFinalize(f);
            _framePool = null;
        }
        
        // Start delayed asynchronous native teardown
        // Wait 2 seconds to allow Desktop Window Manager (DWM) to asynchronously conclude
        // session shutdown, THEN safely dispose the internal WinRT proxies and D3D references.
        // This completely prevents both immediate DWM crashes AND delayed background GC crashes.
        var wDevice = _winrtDevice;
        var rItem = _item;
        var dCtx = _d3dContext;
        var dDev = _d3dDevice;
        
        _item = null;
        _winrtDevice = null;
        _d3dContext = IntPtr.Zero;
        _d3dDevice = IntPtr.Zero;

        if (wDevice != null) 
        {
            try { (wDevice as IDisposable)?.Dispose(); } catch { }
            GC.SuppressFinalize(wDevice);
        }
        
        if (rItem != null) 
        {
            try { (rItem as IDisposable)?.Dispose(); } catch { }
            GC.SuppressFinalize(rItem);
        }

        System.Threading.Tasks.Task.Run(async () => 
        {
            await System.Threading.Tasks.Task.Delay(3000);
            
            bool forceGC = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.ImmediateGC) ?? true;
            if (forceGC)
            {
                Log.Msg($"[WgcCapture:Dispose] Skipping forced GC collection (now safely handled)");
            }
            
            if (dCtx != IntPtr.Zero) { Marshal.Release(dCtx); }
            if (dDev != IntPtr.Zero) { Marshal.Release(dDev); }
            Log.Msg($"[WgcCapture:Dispose] Delayed native D3D device teardown complete");
        });
    }
}

using System;
using System.Runtime.InteropServices;
using Elements.Core;

namespace DesktopBuddy;

public sealed unsafe class D3D11AverageColorSampler : IDisposable
{
    private static readonly Guid IID_ID3D11VideoDevice = new(0x10EC4D5B, 0x975A, 0x4689, 0xB9, 0xE4, 0xD0, 0xAA, 0xC3, 0x0F, 0xE3, 0x33);
    private static readonly Guid IID_ID3D11VideoContext = new(0x61F21C45, 0x3C0E, 0x4A74, 0x9C, 0xEA, 0x67, 0x10, 0x0D, 0x9A, 0xD5, 0xE4);

    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_USAGE_DEFAULT = 0;
    private const int D3D11_USAGE_STAGING = 3;
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;

    private readonly object _gate = new();
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _vpDevice;
    private IntPtr _vpContext;
    private IntPtr _vpEnum;
    private IntPtr _vpProcessor;
    private IntPtr _inputTexture;
    private IntPtr _inputView;
    private IntPtr _outputTexture;
    private IntPtr _outputView;
    private IntPtr _stagingTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    public bool TrySample(IntPtr device, IntPtr context, IntPtr sourceTexture, int width, int height, out colorX color)
    {
        color = new colorX(1f, 1f, 1f, 1f);
        if (device == IntPtr.Zero || context == IntPtr.Zero || sourceTexture == IntPtr.Zero || width <= 0 || height <= 0)
            return false;

        lock (_gate)
        {
            if (_disposed)
                return false;

            EnsureResources(device, context, width, height);
            CopyResource(context, _inputTexture, sourceTexture);

            var stream = new VP_STREAM
            {
                Enable = 1,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                pInputSurface = _inputView
            };
            var vpCtxVt = *(IntPtr**)_vpContext;
            var bltFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VP_STREAM*, int>)vpCtxVt[53];
            int hr = bltFn(_vpContext, _vpProcessor, _outputView, 0, 1, &stream);
            if (hr < 0)
                throw new InvalidOperationException($"VideoProcessorBlt failed hr=0x{hr:X8}");

            CopyResource(context, _stagingTexture, _outputTexture);
            return ReadStagingPixel(context, _stagingTexture, out color);
        }
    }

    private void EnsureResources(IntPtr device, IntPtr context, int width, int height)
    {
        if (_device == device && _context == context && _width == width && _height == height && _inputTexture != IntPtr.Zero)
            return;

        ReleaseResources();
        _device = device;
        _context = context;
        _width = width;
        _height = height;

        int hr;
        var iidVD = IID_ID3D11VideoDevice;
        var iidVC = IID_ID3D11VideoContext;

        hr = Marshal.QueryInterface(device, in iidVD, out _vpDevice);
        if (hr < 0) throw new InvalidOperationException($"QueryInterface ID3D11VideoDevice failed hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(context, in iidVC, out _vpContext);
        if (hr < 0) throw new InvalidOperationException($"QueryInterface ID3D11VideoContext failed hr=0x{hr:X8}");

        var desc = new VP_CONTENT_DESC
        {
            InputFrameFormat = 0,
            InputFrameRateNum = 30,
            InputFrameRateDen = 1,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRateNum = 30,
            OutputFrameRateDen = 1,
            OutputWidth = 1,
            OutputHeight = 1,
            Usage = 1
        };

        var vpDevVt = *(IntPtr**)_vpDevice;
        var createEnumFn = (delegate* unmanaged[Stdcall]<IntPtr, VP_CONTENT_DESC*, out IntPtr, int>)vpDevVt[10];
        hr = createEnumFn(_vpDevice, &desc, out _vpEnum);
        if (hr < 0) throw new InvalidOperationException($"CreateVideoProcessorEnumerator failed hr=0x{hr:X8}");

        var createProcFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, out IntPtr, int>)vpDevVt[4];
        hr = createProcFn(_vpDevice, _vpEnum, 0, out _vpProcessor);
        if (hr < 0) throw new InvalidOperationException($"CreateVideoProcessor failed hr=0x{hr:X8}");

        _inputTexture = CreateTexture2D(device, (uint)width, (uint)height, D3D11_USAGE_DEFAULT, D3D11_BIND_RENDER_TARGET, 0);
        _outputTexture = CreateTexture2D(device, 1, 1, D3D11_USAGE_DEFAULT, D3D11_BIND_RENDER_TARGET, 0);
        _stagingTexture = CreateTexture2D(device, 1, 1, D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_READ);

        var ivDesc = new VP_INPUT_VIEW_DESC { FourCC = 0, ViewDimension = 1, MipSlice = 0, ArraySlice = 0 };
        var createIVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_INPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[8];
        hr = createIVFn(_vpDevice, _inputTexture, _vpEnum, &ivDesc, out _inputView);
        if (hr < 0) throw new InvalidOperationException($"CreateVideoProcessorInputView failed hr=0x{hr:X8}");

        var ovDesc = new VP_OUTPUT_VIEW_DESC { ViewDimension = 1, MipSlice = 0, FirstArraySlice = 0, ArraySize = 1 };
        var createOVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_OUTPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[9];
        hr = createOVFn(_vpDevice, _outputTexture, _vpEnum, &ovDesc, out _outputView);
        if (hr < 0) throw new InvalidOperationException($"CreateVideoProcessorOutputView failed hr=0x{hr:X8}");

        var vpCtxVt = *(IntPtr**)_vpContext;
        var colorSpace = new VP_COLOR_SPACE { Value = 0 };
        var setOutCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, VP_COLOR_SPACE*, void>)vpCtxVt[15];
        setOutCsFn(_vpContext, _vpProcessor, &colorSpace);

        var setFrameFmtFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)vpCtxVt[27];
        setFrameFmtFn(_vpContext, _vpProcessor, 0, 0);

        var setInCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, VP_COLOR_SPACE*, void>)vpCtxVt[28];
        setInCsFn(_vpContext, _vpProcessor, 0, &colorSpace);
    }

    private static IntPtr CreateTexture2D(IntPtr device, uint width, uint height, int usage, uint bindFlags, uint cpuAccessFlags)
    {
        var desc = new TEX2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = 0
        };

        var devVt = *(IntPtr**)device;
        var createTexFn = (delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, out IntPtr, int>)devVt[5];
        int hr = createTexFn(device, &desc, IntPtr.Zero, out IntPtr texture);
        if (hr < 0) throw new InvalidOperationException($"CreateTexture2D {width}x{height} failed hr=0x{hr:X8}");
        return texture;
    }

    private static void CopyResource(IntPtr context, IntPtr dst, IntPtr src)
    {
        var ctxVt = *(IntPtr**)context;
        var copyFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)ctxVt[47];
        copyFn(context, dst, src);
    }

    private static bool ReadStagingPixel(IntPtr context, IntPtr stagingTexture, out colorX color)
    {
        color = new colorX(1f, 1f, 1f, 1f);
        var ctxVt = *(IntPtr**)context;
        var mapFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)ctxVt[14];
        var unmapFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)ctxVt[15];
        D3D11_MAPPED_SUBRESOURCE mapped;
        int hr = mapFn(context, stagingTexture, 0, D3D11_MAP_READ, 0, &mapped);
        if (hr < 0)
            throw new InvalidOperationException($"Map 1x1 staging texture failed hr=0x{hr:X8}");

        try
        {
            byte* p = (byte*)mapped.pData;
            color = new colorX(p[2] / 255f, p[1] / 255f, p[0] / 255f, 1f);
            return true;
        }
        finally
        {
            unmapFn(context, stagingTexture, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        Release(ref _stagingTexture);
        Release(ref _outputView);
        Release(ref _outputTexture);
        Release(ref _inputView);
        Release(ref _inputTexture);
        Release(ref _vpProcessor);
        Release(ref _vpEnum);
        Release(ref _vpContext);
        Release(ref _vpDevice);
        _width = 0;
        _height = 0;
        _device = IntPtr.Zero;
        _context = IntPtr.Zero;
    }

    private static void Release(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return;

        Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VP_CONTENT_DESC
    {
        public int InputFrameFormat;
        public uint InputFrameRateNum, InputFrameRateDen;
        public uint InputWidth, InputHeight;
        public uint OutputFrameRateNum, OutputFrameRateDen;
        public uint OutputWidth, OutputHeight;
        public int Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VP_INPUT_VIEW_DESC { public uint FourCC; public int ViewDimension; public uint MipSlice, ArraySlice; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VP_OUTPUT_VIEW_DESC { public int ViewDimension; public uint MipSlice, FirstArraySlice, ArraySize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VP_STREAM
    {
        public int Enable;
        public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
        private uint _pad;
        public IntPtr ppPastSurfaces, pInputSurface, ppFutureSurfaces;
        public IntPtr ppPastSurfacesRight, pInputSurfaceRight, ppFutureSurfacesRight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VP_COLOR_SPACE { public uint Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TEX2D_DESC
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
}

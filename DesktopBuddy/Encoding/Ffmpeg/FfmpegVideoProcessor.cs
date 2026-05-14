using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private static readonly Guid IID_ID3D11VideoDevice = new(0x10EC4D5B, 0x975A, 0x4689, 0xB9, 0xE4, 0xD0, 0xAA, 0xC3, 0x0F, 0xE3, 0x33);
    private static readonly Guid IID_ID3D11VideoContext = new(0x61F21C45, 0x3C0E, 0x4A74, 0x9C, 0xEA, 0x67, 0x10, 0x0D, 0x9A, 0xD5, 0xE4);

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

    private void SetupVideoProcessor(IntPtr d3dDevice, uint inputW, uint inputH, uint outputW, uint outputH, bool outputNv12)
    {
        int hr;
        var iidVD = IID_ID3D11VideoDevice;
        var iidVC = IID_ID3D11VideoContext;

        hr = Marshal.QueryInterface(d3dDevice, in iidVD, out _vpDevice);
        if (hr < 0) throw new Exception($"QueryInterface ID3D11VideoDevice failed hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(_deviceContext, in iidVC, out _vpContext);
        if (hr < 0) throw new Exception($"QueryInterface ID3D11VideoContext failed hr=0x{hr:X8}");

        var desc = new VP_CONTENT_DESC
        {
            InputFrameFormat = 0,
            InputFrameRateNum = 30, InputFrameRateDen = 1,
            InputWidth = inputW, InputHeight = inputH,
            OutputFrameRateNum = 30, OutputFrameRateDen = 1,
            OutputWidth = outputW, OutputHeight = outputH,
            Usage = 1
        };
        var vpDevVt = *(IntPtr**)_vpDevice;
        var createEnumFn = (delegate* unmanaged[Stdcall]<IntPtr, VP_CONTENT_DESC*, out IntPtr, int>)vpDevVt[10];
        hr = createEnumFn(_vpDevice, &desc, out _vpEnum);
        if (hr < 0) throw new Exception($"CreateVideoProcessorEnumerator failed hr=0x{hr:X8}");

        var createProcFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, out IntPtr, int>)vpDevVt[4];
        hr = createProcFn(_vpDevice, _vpEnum, 0, out _vpProcessor);
        if (hr < 0) throw new Exception($"CreateVideoProcessor failed hr=0x{hr:X8}");

        var outputDesc = new TEX2D_DESC
        {
            Width = outputW, Height = outputH, MipLevels = 1, ArraySize = 1,
            Format = outputNv12 ? 103 : 87,
            SampleCount = 1, SampleQuality = 0,
            Usage = 0,
            BindFlags = 0x20,
            CPUAccessFlags = 0, MiscFlags = 0
        };
        var devVt = *(IntPtr**)d3dDevice;
        var createTexFn = (delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, out IntPtr, int>)devVt[5];
        var inputDesc = new TEX2D_DESC
        {
            Width = inputW, Height = inputH, MipLevels = 1, ArraySize = 1,
            Format = 87,
            SampleCount = 1, SampleQuality = 0,
            Usage = 0,
            BindFlags = 0x20,
            CPUAccessFlags = 0, MiscFlags = 0
        };
        hr = createTexFn(d3dDevice, &inputDesc, IntPtr.Zero, out _vpInputTexture);
        if (hr < 0) throw new Exception($"CreateTexture2D video processor input failed hr=0x{hr:X8}");

        hr = createTexFn(d3dDevice, &outputDesc, IntPtr.Zero, out _vpOutputTexture);
        if (hr < 0) throw new Exception($"CreateTexture2D video processor output failed hr=0x{hr:X8}");

        var ovDesc = new VP_OUTPUT_VIEW_DESC { ViewDimension = 1, MipSlice = 0 };
        var createOVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_OUTPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[9];
        hr = createOVFn(_vpDevice, _vpOutputTexture, _vpEnum, &ovDesc, out _vpOutputView);
        if (hr < 0) throw new Exception($"CreateVideoProcessorOutputView failed hr=0x{hr:X8}");

        var vpCtxVt = *(IntPtr**)_vpContext;
        var outCs = new VP_COLOR_SPACE { Value = outputNv12 ? 0x6u : 0u };
        var setOutCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, VP_COLOR_SPACE*, void>)vpCtxVt[15];
        setOutCsFn(_vpContext, _vpProcessor, &outCs);

        var setFrameFmtFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)vpCtxVt[27];
        setFrameFmtFn(_vpContext, _vpProcessor, 0, 0);

        var inCs = new VP_COLOR_SPACE { Value = 0 };
        var setInCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, VP_COLOR_SPACE*, void>)vpCtxVt[28];
        setInCsFn(_vpContext, _vpProcessor, 0, &inCs);

        Log.Msg($"[FfmpegEnc:{_streamId}] Video Processor ready: BGRA {inputW}x{inputH} -> {(outputNv12 ? "NV12" : "BGRA")} {outputW}x{outputH}, inCs=0, outCs=0x{outCs.Value:X}");
    }

    private bool VideoProcessorConvert(IntPtr bgraTexture)
    {
        if (_vpInputTexture == IntPtr.Zero)
            return false;

        CopyTextureToFrame(_deviceContext, _vpInputTexture, 0, bgraTexture, (int)_sourceWidth, (int)_sourceHeight);

        if (_vpInputView == IntPtr.Zero)
        {
            if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; }
            var ivDesc = new VP_INPUT_VIEW_DESC { FourCC = 0, ViewDimension = 1, MipSlice = 0, ArraySlice = 0 };
            var vpDevVt = *(IntPtr**)_vpDevice;
            var createIVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_INPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[8];
            int hr = createIVFn(_vpDevice, _vpInputTexture, _vpEnum, &ivDesc, out _vpInputView);
            if (hr < 0)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] CreateVideoProcessorInputView failed hr=0x{hr:X8}");
                _vpInputView = IntPtr.Zero;
                return false;
            }
        }

        var stream = new VP_STREAM
        {
            Enable = 1,
            OutputIndex = 0, InputFrameOrField = 0,
            PastFrames = 0, FutureFrames = 0,
            pInputSurface = _vpInputView
        };
        var vpCtxVt = *(IntPtr**)_vpContext;
        var bltFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VP_STREAM*, int>)vpCtxVt[53];
        int bltHr = bltFn(_vpContext, _vpProcessor, _vpOutputView, 0, 1, &stream);
        if (bltHr < 0)
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] VideoProcessorBlt failed hr=0x{bltHr:X8}");
            return false;
        }
        return true;
    }

}

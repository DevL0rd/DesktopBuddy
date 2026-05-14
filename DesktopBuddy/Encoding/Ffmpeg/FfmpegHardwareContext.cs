using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private void SetupHardwareContext(IntPtr d3dDevice, AVPixelFormat swFormat)
    {
        _hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_hwDeviceCtx == null) throw new Exception("av_hwdevice_ctx_alloc failed");

        var hwDevCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
        var d3d11DevCtx = (AVD3D11VADeviceContext*)hwDevCtx->hwctx;

        d3d11DevCtx->device = (ID3D11Device*)d3dDevice;

        Log.Msg($"[FfmpegEnc:{_streamId}] av_hwdevice_ctx_init: calling...");
        int ret = ffmpeg.av_hwdevice_ctx_init(_hwDeviceCtx);
        Log.Msg($"[FfmpegEnc:{_streamId}] av_hwdevice_ctx_init: returned {ret}");
        if (ret < 0) throw new Exception($"av_hwdevice_ctx_init failed: {FfmpegError(ret)}");

        Log.Msg($"[FfmpegEnc:{_streamId}] D3D11VA hardware context initialized with device 0x{d3dDevice:X}");

        _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
        if (_hwFramesCtx == null) throw new Exception("av_hwframe_ctx_alloc failed");

        var framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesCtx->sw_format = swFormat;
        framesCtx->width = (int)_width;
        framesCtx->height = (int)_height;
        // Some AMD drivers reject preallocated D3D11/NV12 pools; lazy allocation is slower to start but much more compatible.
        framesCtx->initial_pool_size = 0;

        Log.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_ctx_init: calling...");
        int ret2 = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
        Log.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_ctx_init: returned {ret2}");
        if (ret2 < 0) throw new Exception($"av_hwframe_ctx_init failed: {FfmpegError(ret2)}");

        _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
        Log.Msg($"[FfmpegEnc:{_streamId}] Hardware frames context initialized: {_width}x{_height} {swFormat}");
    }

}

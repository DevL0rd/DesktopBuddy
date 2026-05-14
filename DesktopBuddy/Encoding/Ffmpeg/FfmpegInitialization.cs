using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private readonly object _initLock = new();

    public bool Initialize(IntPtr d3dDevice, uint width, uint height, object d3dContextLock, AudioCapture audioCapture = null)
    {
        lock (_initLock)
        {
        if (_initialized) return true;
        if (_initFailed || _disposed) return false;
        _d3dContextLock = d3dContextLock;

        try
        {
            SetFfmpegPath();
            if (!_ffmpegPathSet) { _initFailed = true; return false; }

            _sourceWidth = width & ~1u;
            _sourceHeight = height & ~1u;
            CalculateEncoderSize(_sourceWidth, _sourceHeight, out _width, out _height);
            _needsGpuScale = _width != _sourceWidth || _height != _sourceHeight;

            if (_width < 128 || _height < 128)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] Window too small for encoding: source={_sourceWidth}x{_sourceHeight}, encode={_width}x{_height} (min 128x128)");
                _initFailed = true; return false;
            }

            int streamFps = GetStreamFps();
            _keepAliveFps = streamFps;
            Log.Msg($"[FfmpegEnc:{_streamId}] Initializing: source={_sourceWidth}x{_sourceHeight}, encode={_width}x{_height}, gpuScale={_needsGpuScale}, nominal encoder rate {streamFps}fps, keepalive {GetKeepAliveFps()}fps (capture remains event-driven)");

            uint adapterVendorId = WgcCapture.SharedD3dAdapterVendorId;
            string[] encoders = GetEncoderPreference(adapterVendorId);
            Log.Msg($"[FfmpegEnc:{_streamId}] Encoder preference for adapter vendor=0x{adapterVendorId:X4}: {string.Join(", ", encoders)}");

            AVCodec* codec = null;
            string codecName = null;
            int ret = -1;
            foreach (var name in encoders)
            {
                codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) { Log.Msg($"[FfmpegEnc:{_streamId}] {name} not available"); continue; }

                Log.Msg($"[FfmpegEnc:{_streamId}] Trying {name}...");

                if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }

                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null) continue;

                _codecCtx->width = (int)_width;
                _codecCtx->height = (int)_height;
                _codecCtx->time_base = new AVRational { num = 1, den = 90000 };
                _codecCtx->framerate = new AVRational { num = streamFps, den = 1 };
                long bitrate = Math.Max(1, DesktopBuddyMod.RuntimeBitrateMbps) * 1_000_000L;
                bool isAmf = name.Contains("amf");
                long peakBitrate = (long)(bitrate * 1.2);
                int vbvBuffer = (int)Math.Clamp(bitrate / 4, 500_000L, int.MaxValue);
                _codecCtx->max_b_frames = 0;
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY | ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                _codecCtx->gop_size = streamFps;
                _codecCtx->bit_rate = bitrate;
                _codecCtx->rc_max_rate = peakBitrate;
                _codecCtx->rc_buffer_size = vbvBuffer;

                var swFormat = isAmf
                    ? AVPixelFormat.AV_PIX_FMT_NV12
                    : AVPixelFormat.AV_PIX_FMT_BGRA;

                try
                {
                    SetupHardwareContext(d3dDevice, swFormat);
                }
                catch (Exception ex)
                {
                    Log.Msg($"[FfmpegEnc:{_streamId}] {name} setup failed: {ex.Message}");
                    ret = -1;
                    continue;
                }

                AVDictionary* opts = null;
                if (name.Contains("nvenc"))
                {
                    ffmpeg.av_dict_set(&opts, "preset", "p1", 0);
                    ffmpeg.av_dict_set(&opts, "tune", "ull", 0);
                    ffmpeg.av_dict_set(&opts, "rc", "vbr", 0);
                    ffmpeg.av_dict_set(&opts, "zerolatency", "1", 0);
                    ffmpeg.av_dict_set(&opts, "delay", "0", 0);
                    ffmpeg.av_dict_set(&opts, "rc-lookahead", "0", 0);
                    ffmpeg.av_dict_set(&opts, "forced-idr", "1", 0);
                    ffmpeg.av_dict_set(&opts, "repeat-headers", "1", 0);
                }
                else if (isAmf)
                {
                    ffmpeg.av_dict_set(&opts, "usage", "lowlatency_high_quality", 0);
                    ffmpeg.av_dict_set(&opts, "rc", "vbr_peak", 0);
                    ffmpeg.av_dict_set(&opts, "header_insertion_mode", "idr", 0);
                }

                Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_open2: calling...");
                ret = ffmpeg.avcodec_open2(_codecCtx, codec, &opts);
                Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_open2: returned {ret} ({(ret < 0 ? FfmpegError(ret) : "ok")})");
                ffmpeg.av_dict_free(&opts);

                if (ret >= 0) { codecName = name; _needsVideoProcessor = name.Contains("amf"); break; }
                Log.Msg($"[FfmpegEnc:{_streamId}] {name} failed: {FfmpegError(ret)}");
            }

            if (ret < 0 || codecName == null)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] No GPU encoder available (need NVIDIA, AMD, or Intel GPU)");
                if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }
                _initFailed = true; return false;
            }

                Log.Msg($"[FfmpegEnc:{_streamId}] Codec opened: {codecName}, fps={streamFps}, gop={_codecCtx->gop_size}");

            _hwFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            _audioCapture = audioCapture;
            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _audioReadPos = _audioCapture?.WritePosition ?? 0;
            _audioSamplesEncoded = 0;
            _lastAudioPts = -1;

            SetupMuxer();
            StartAudioEncodeThreadIfReady();

            var hwDevCtxData = (AVHWDeviceContext*)_hwDeviceCtx->data;
            var d3d11DevCtxData = (AVD3D11VADeviceContext*)hwDevCtxData->hwctx;
            _deviceContext = (IntPtr)d3d11DevCtxData->device_context;

            if (_needsVideoProcessor || _needsGpuScale)
                SetupVideoProcessor(d3dDevice, _sourceWidth, _sourceHeight, _width, _height, _needsVideoProcessor);

            if (_rtspUrl == null)
            {
                _ringBuffer = new byte[RING_SIZE];
                _ringWritePos = 0;
                Interlocked.Exchange(ref _lastKeyframeRingPos, -1);
            }
            _initialized = true;

            _encodeThread = new Thread(EncodeLoop) { Name = $"FfmpegEnc:{_streamId}:Encode", IsBackground = true };
            _encodeThread.Start();
            Log.Msg($"[FfmpegEnc:{_streamId}] Encode thread started");

            Log.Msg($"[FfmpegEnc:{_streamId}] Ready: {_width}x{_height} {codecName}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] Initialize FAILED: {ex}");
            _initFailed = true;
            return false;
        }
        }
    }

    public void StartInitializeAsync(IntPtr d3dDevice, uint width, uint height, object d3dContextLock, AudioCapture audioCapture = null)
    {
        if (Interlocked.Exchange(ref _initStarted, 1) != 0) return;
        _initThread = new Thread(() => Initialize(d3dDevice, width, height, d3dContextLock, audioCapture))
        { Name = $"FfmpegEnc:{_streamId}:Init", IsBackground = true };
        _initThread.Start();
    }

    private static void CalculateEncoderSize(uint sourceWidth, uint sourceHeight, out uint encoderWidth, out uint encoderHeight)
    {
        encoderWidth = sourceWidth & ~1u;
        encoderHeight = sourceHeight & ~1u;

        uint maxResolution = (uint)DesktopBuddyMod.RuntimeMaxStreamResolution & ~1u;
        uint longestEdge = Math.Max(encoderWidth, encoderHeight);

        if (longestEdge <= maxResolution)
            return;

        double scale = (double)maxResolution / longestEdge;

        encoderWidth = Math.Max(2u, ((uint)Math.Floor(encoderWidth * scale)) & ~1u);
        encoderHeight = Math.Max(2u, ((uint)Math.Floor(encoderHeight * scale)) & ~1u);
    }

    private static string[] GetEncoderPreference(uint adapterVendorId)
    {
        string configured = DesktopBuddyMod.NormalizeEncoderPreference(
            DesktopBuddyMod.RuntimeEncoderPreference);

        if (configured == "libx264" || configured == "libx265")
        {
            Log.Msg($"[FfmpegEnc] Software encoder '{configured}' is configured, but CPU readback encoding is not enabled in this build; falling back to automatic GPU encoders");
            configured = "auto";
        }

        string[] automatic = adapterVendorId switch
        {
            0x10DE => new[] { "hevc_nvenc", "h264_nvenc", "hevc_qsv", "h264_qsv", "hevc_amf", "h264_amf" },
            0x1002 => new[] { "hevc_amf", "h264_amf", "hevc_qsv", "h264_qsv", "hevc_nvenc", "h264_nvenc" },
            0x8086 => new[] { "hevc_qsv", "h264_qsv", "hevc_nvenc", "h264_nvenc", "hevc_amf", "h264_amf" },
            _ => new[] { "hevc_qsv", "h264_qsv", "hevc_nvenc", "hevc_amf", "h264_nvenc", "h264_amf" }
        };

        if (configured == "auto")
            return automatic;

        return new[] { configured }
            .Concat(automatic.Where(name => !string.Equals(name, configured, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

}

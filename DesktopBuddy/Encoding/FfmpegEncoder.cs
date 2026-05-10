using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed unsafe class FfmpegEncoder : IDisposable
{
    private readonly int _streamId;
    private bool _initialized;
    private bool _initFailed;

    private AVCodecContext* _codecCtx;
    private AVFormatContext* _fmtCtx;
    private AVIOContext* _ioCtx;
    private AVStream* _stream;
    private AVBufferRef* _hwDeviceCtx;
    private AVBufferRef* _hwFramesCtx;
    private AVFrame* _hwFrame;
    private AVPacket* _pkt;

    private AVCodecContext* _audioCodecCtx;
    private AVStream* _audioStream;
    private AVFrame* _audioFrame;
    private AudioCapture _audioCapture;
    private long _audioReadPos;
    private long _audioSamplesEncoded;
    private float[] _audioScratch;
    private Thread _audioEncodeThread;
    private AVPacket* _audioPkt;

    private byte[] _ringBuffer;
    private long _ringWritePos;
    private readonly object _ringLock = new();
    private readonly object _muxerLock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);

    private uint _sourceWidth, _sourceHeight;
    private uint _width, _height;
    private int _totalFrames;

    private const int RING_SIZE = 16 * 1024 * 1024;
    private const int AVIO_BUFFER_SIZE = 65536;

    private volatile bool _disposed;
    private int _disposeGuard;
    private IntPtr _deviceContext;
    private object _d3dContextLock;

    private Thread _encodeThread;
    private Thread _initThread;
    private volatile int _initStarted;
    private readonly AutoResetEvent _encodeEvent = new(false);
    private volatile IntPtr _pendingTexture;
    private volatile uint _pendingWidth, _pendingHeight;

    private bool _needsVideoProcessor;
    private bool _needsGpuScale;
    private IntPtr _vpDevice, _vpContext, _vpEnum, _vpProcessor;
    private IntPtr _vpOutputView, _vpOutputTexture;
    private IntPtr _vpInputView, _vpInputViewTex;
    private long _startTicks;
    private long _lastVideoPts = -1;
    private long _lastKeyframeRingPos = -1;
    private long _readerOverrunEvents;
    private long _readerOverrunMaxBacklogBytes;
    private long _readerLastOverrunLogTicks;
    private long _lastResourceLogTicks;
    private long _lastResourceLogRingPos;

    private avio_alloc_context_write_packet _writeCallbackDelegate;
    private GCHandle _selfHandle;

    private volatile bool _rtspBroken;
    private IntPtr _keepAliveTexture;
    private uint _keepAliveW, _keepAliveH;
    private long _lastEncodeTicks;
    private long _nextKeepAliveTicks;
    private int _pendingHttpKeyframeRequests;
    private int _keepAliveFramesEncoded;
    private int _httpJoinKeyframesEncoded;
    private int _keepAliveFps = 60;

    public bool IsInitialized => _initialized;
    public bool IsRunning => _initialized;
    public bool HasReadableVideoKeyframe
    {
        get
        {
            if (_rtspUrl != null) return _initialized && !_rtspBroken;
            return HasReadableVideoKeyframeAtOrAfter(0);
        }
    }

    public bool HasReadableVideoKeyframeAtOrAfter(long minimumKeyframePos)
    {
        if (_rtspUrl != null) return _initialized && !_rtspBroken;
        lock (_ringLock)
        {
            long keyframePos = Interlocked.Read(ref _lastKeyframeRingPos);
            return _initialized &&
                _ringBuffer != null &&
                keyframePos >= minimumKeyframePos &&
                keyframePos >= 0 &&
                keyframePos < _ringWritePos &&
                keyframePos >= _ringWritePos - RING_SIZE;
        }
    }

    public string ReadableStreamState
    {
        get
        {
            if (_rtspUrl != null)
                return $"rtsp initialized={_initialized} broken={_rtspBroken} lastVideoPts={Interlocked.Read(ref _lastVideoPts)} frames={_totalFrames}";

            lock (_ringLock)
            {
                long keyframePos = Interlocked.Read(ref _lastKeyframeRingPos);
                return $"http initialized={_initialized} ringReady={_ringBuffer != null} keyframePos={keyframePos} writePos={_ringWritePos} lastVideoPts={Interlocked.Read(ref _lastVideoPts)} frames={_totalFrames}";
            }
        }
    }

    public string GetReaderDiagnostics(long readPos, bool aligned)
    {
        lock (_ringLock)
        {
            long writePos = _ringWritePos;
            long backlog = writePos - readPos;
            long latestKeyframePos = Interlocked.Read(ref _lastKeyframeRingPos);
            long keyframeAgeBytes = latestKeyframePos >= 0 ? writePos - latestKeyframePos : -1;
            return $"readPos={readPos} writePos={writePos} backlog={backlog} aligned={aligned} latestKeyframe={latestKeyframePos} keyframeAgeBytes={keyframeAgeBytes} ringSize={RING_SIZE} frames={_totalFrames} keepAlive={_keepAliveFramesEncoded}";
        }
    }

    public void Stop()
    {
        _disposed = true;
        _initialized = false;
    }

    public long CurrentWritePosition
    {
        get
        {
            lock (_ringLock) return _ringWritePos;
        }
    }

    public void RequestHttpKeyframe(string reason)
    {
        if (_rtspUrl != null || _disposed) return;
        Interlocked.Exchange(ref _pendingHttpKeyframeRequests, 1);
        try { _encodeEvent.Set(); } catch { }
        Log.Msg($"[FfmpegEnc:{_streamId}] HTTP keyframe requested: {reason}");
    }

    private static bool _ffmpegPathSet;
    private static bool _hardwareEncoderPrewarmed;
    private static readonly object _ffmpegInitLock = new();

    public static void SetFfmpegPath()
    {
        lock (_ffmpegInitLock)
        {
            if (_ffmpegPathSet) return;

            string dllDir = FindFfmpegDlls();
            if (dllDir == null)
            {
                Log.Msg("[FFmpeg] FATAL: Could not find FFmpeg shared libraries (avcodec, avformat, avutil)");
                return;
            }

            ffmpeg.RootPath = dllDir;
            DynamicallyLoadedBindings.Initialize();
            Log.Msg($"[FFmpeg] Library path: {dllDir}");

            uint ver = ffmpeg.avcodec_version();
            Log.Msg($"[FFmpeg] avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

            _ffmpegPathSet = true;
        }
    }

    public static string FindFfmpegDlls()
    {
        var modDir = Path.GetDirectoryName(typeof(FfmpegEncoder).Assembly.Location) ?? "";
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(modDir, "..", "DesktopBuddyNative")),
            Path.GetFullPath(Path.Combine(modDir, "DesktopBuddyNative")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopBuddyNative")),
        };
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "avcodec-62.dll")))
                return dir;
        }
        return null;
    }

    public static void PrewarmHardwareEncoder(IntPtr d3dDevice, object d3dContextLock)
    {
        lock (_ffmpegInitLock)
        {
            if (_hardwareEncoderPrewarmed) return;
            if (d3dDevice == IntPtr.Zero)
            {
                Log.Msg("[FFmpeg] Hardware encoder prewarm skipped: no D3D device");
                return;
            }

            using var encoder = new FfmpegEncoder(0);
            if (encoder.Initialize(d3dDevice, 640, 360, d3dContextLock))
            {
                _hardwareEncoderPrewarmed = true;
                Log.Msg("[FFmpeg] Hardware encoder prewarmed");
            }
            else
            {
                Log.Msg("[FFmpeg] Hardware encoder prewarm failed");
            }
        }
    }

    public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
    {
        return _dataAvailable.WaitAsync(Math.Max(1, timeoutMs));
    }

    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool startsAtKeyframe)
    {
        startsAtKeyframe = false;
        lock (_ringLock)
        {
            long available = _ringWritePos - readPos;
            if (available <= 0) return 0;

            if (available > RING_SIZE)
            {
                RecordReaderOverrun(readPos, _ringWritePos, available);
                return -1;
            }

            long latestKeyframePos = Interlocked.Read(ref _lastKeyframeRingPos);
            if (!aligned)
            {
                long kfPos = latestKeyframePos;
                if (kfPos >= minimumKeyframePos && kfPos >= 0 && kfPos >= _ringWritePos - RING_SIZE && kfPos < _ringWritePos)
                {
                    readPos = kfPos;
                    available = _ringWritePos - readPos;
                    aligned = true;
                    Log.Msg($"[FfmpegEnc:{_streamId}] Reader aligned to latest keyframe at ringPos={readPos}, liveWritePos={_ringWritePos}, backlog={available} bytes");
                }
                if (!aligned) return 0;
            }

            int toRead = (int)Math.Min(available, buffer.Length);
            startsAtKeyframe = latestKeyframePos >= 0 && readPos == latestKeyframePos;

            int ringPos = (int)(readPos % RING_SIZE);
            int firstChunk = Math.Min(toRead, RING_SIZE - ringPos);
            Buffer.BlockCopy(_ringBuffer, ringPos, buffer, 0, firstChunk);
            if (firstChunk < toRead)
                Buffer.BlockCopy(_ringBuffer, 0, buffer, firstChunk, toRead - firstChunk);
            readPos += toRead;
            return toRead;
        }
    }

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
                long bitrate = Math.Max(1, DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.Bitrate) ?? 10) * 1_000_000L;
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
            _audioReadPos = 0;
            _audioSamplesEncoded = 0;

            SetupMuxer();

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

            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

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

        uint maxResolution = (uint)Math.Clamp(DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.MaxStreamResolution) ?? 2560, 128, 8192) & ~1u;
        uint longestEdge = Math.Max(encoderWidth, encoderHeight);

        if (longestEdge <= maxResolution)
            return;

        double scale = (double)maxResolution / longestEdge;

        encoderWidth = Math.Max(2u, ((uint)Math.Floor(encoderWidth * scale)) & ~1u);
        encoderHeight = Math.Max(2u, ((uint)Math.Floor(encoderHeight * scale)) & ~1u);
    }

    private static string[] GetEncoderPreference(uint adapterVendorId)
    {
        return adapterVendorId switch
        {
            0x10DE => new[] { "hevc_nvenc", "h264_nvenc" },
            0x1002 => new[] { "hevc_amf", "h264_amf" },
            _ => new[] { "hevc_nvenc", "hevc_amf", "h264_nvenc", "h264_amf" }
        };
    }

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

    private void SetupMuxer()
    {
        if (_rtspUrl != null)
        {
            SetupRtspMuxer();
            return;
        }

        _selfHandle = GCHandle.Alloc(this);

        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mpegts", null);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        byte* ioBuffer = (byte*)ffmpeg.av_malloc(AVIO_BUFFER_SIZE);
        _writeCallbackDelegate = WriteCallback;
        _ioCtx = ffmpeg.avio_alloc_context(
            ioBuffer, AVIO_BUFFER_SIZE,
            1,
            (void*)GCHandle.ToIntPtr(_selfHandle),
            null,
            _writeCallbackDelegate,
            null
        );
        if (_ioCtx == null) throw new Exception("avio_alloc_context failed");

        _fmtCtx->pb = _ioCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        _fmtCtx->max_delay = 0;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        if (_audioCapture != null && _audioCapture.IsCapturing)
        {
            SetupAudioStream();
        }

        AVDictionary* muxerOpts = null;
        ffmpeg.av_dict_set(&muxerOpts, "mpegts_flags", "pat_pmt_at_frames", 0);
        ffmpeg.av_dict_set(&muxerOpts, "flush_packets", "1", 0);
        ffmpeg.av_dict_set(&muxerOpts, "muxdelay", "0", 0);
        ffmpeg.av_dict_set(&muxerOpts, "muxpreload", "0", 0);
        ret = ffmpeg.avformat_write_header(_fmtCtx, &muxerOpts);
        ffmpeg.av_dict_free(&muxerOpts);
        if (ret < 0) throw new Exception($"avformat_write_header failed: {FfmpegError(ret)}");

        Log.Msg($"[FfmpegEnc:{_streamId}] MPEG-TS muxer ready (in-process, no external ffmpeg)");
    }

    private void SetupRtspMuxer()
    {
        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "rtsp", _rtspUrl);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 (rtsp) failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream (rtsp) failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        if (_audioCapture != null && _audioCapture.IsCapturing)
        {
            SetupAudioStream();
        }

        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
        ret = ffmpeg.avformat_write_header(_fmtCtx, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0) throw new Exception($"avformat_write_header (rtsp) failed: {FfmpegError(ret)}");

        Log.Msg($"[FfmpegEnc:{_streamId}] RTSP muxer ready: {_rtspUrl}");
    }

    private void RtspReconnect()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] RTSP broken, reconnecting in 3s...");
        Thread.Sleep(3000);
        if (_disposed) return;

        lock (_muxerLock)
        {
            try
            {
                if (_fmtCtx != null)
                {
                    try { ffmpeg.av_write_trailer(_fmtCtx); } catch { }
                    if (_fmtCtx->pb != null)
                    {
                        var pb = _fmtCtx->pb;
                        ffmpeg.avio_closep(&pb);
                        _fmtCtx->pb = null;
                    }
                    ffmpeg.avformat_free_context(_fmtCtx);
                    _fmtCtx = null;
                }
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP cleanup error: {ex.Message}");
                _fmtCtx = null;
            }

            _stream = null;
            _audioStream = null;

            try
            {
                SetupRtspMuxer();
                _rtspBroken = false;
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP reconnected");
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] RTSP reconnect failed: {ex.Message}");
            }
        }
    }

    private void SetupAudioStream()
    {
        var audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (audioCodec == null) { Log.Msg($"[FfmpegEnc:{_streamId}] AAC encoder not found, audio disabled"); return; }

        _audioCodecCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
        _audioCodecCtx->sample_rate = 48000;
        _audioCodecCtx->ch_layout = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE, nb_channels = 2, u = new AVChannelLayout_u { mask = ffmpeg.AV_CH_LAYOUT_STEREO } };
        _audioCodecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioCodecCtx->bit_rate = 128000;
        _audioCodecCtx->time_base = new AVRational { num = 1, den = 48000 };
        _audioCodecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        int ret = ffmpeg.avcodec_open2(_audioCodecCtx, audioCodec, null);
        if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] Audio codec open failed: {FfmpegError(ret)}"); return; }

        _audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCodecCtx);
        _audioStream->time_base = _audioCodecCtx->time_base;

        _audioFrame = ffmpeg.av_frame_alloc();
        _audioFrame->nb_samples = _audioCodecCtx->frame_size;
        _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioFrame->ch_layout = _audioCodecCtx->ch_layout;
        _audioFrame->sample_rate = 48000;
        ffmpeg.av_frame_get_buffer(_audioFrame, 0);

        _audioScratch = new float[48000 * 2];
        _audioPkt = ffmpeg.av_packet_alloc();

        _audioEncodeThread = new Thread(AudioEncodeLoop)
        { Name = $"FfmpegEnc:{_streamId}:Audio", IsBackground = true };
        _audioEncodeThread.Start();

        Log.Msg($"[FfmpegEnc:{_streamId}] Audio stream added: AAC 48kHz stereo 128kbps (own thread)");
    }

    private static int WriteCallback(void* opaque, byte* buf, int buf_size)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        var encoder = (FfmpegEncoder)handle.Target;
        return encoder.OnMpegTsData(buf, buf_size);
    }

    private int OnMpegTsData(byte* buf, int buf_size)
    {
        if (buf_size <= 0) return 0;

        lock (_ringLock)
        {
            int ringPos = (int)(_ringWritePos % RING_SIZE);
            int firstChunk = Math.Min(buf_size, RING_SIZE - ringPos);

            Marshal.Copy((IntPtr)buf, _ringBuffer, ringPos, firstChunk);
            if (firstChunk < buf_size)
                Marshal.Copy((IntPtr)(buf + firstChunk), _ringBuffer, 0, buf_size - firstChunk);

            _ringWritePos += buf_size;
        }

        try { _dataAvailable.Release(); }
        catch (SemaphoreFullException) { }

        return buf_size;
    }

    public void QueueFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_disposed || _initFailed || !_initialized) return;
        if ((width & ~1u) != _sourceWidth || (height & ~1u) != _sourceHeight)
        {
            if (_totalFrames == 0)
                Log.Msg($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch source={_sourceWidth}x{_sourceHeight} encode={_width}x{_height} frame={width}x{height}");
            return;
        }

        if (_startTicks != 0)
        {
            double elapsedSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
            long videoPts = (long)(elapsedSec * 90000);
            if (videoPts <= Interlocked.Read(ref _lastVideoPts))
                return;
        }

        Marshal.AddRef(srcTexture);
        var prev = Interlocked.Exchange(ref _pendingTexture, srcTexture);
        if (prev != IntPtr.Zero) Marshal.Release(prev);
        _pendingWidth = width;
        _pendingHeight = height;
        _encodeEvent.Set();
    }

    private void EncodeLoop()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] Encode thread started");
        while (!_disposed)
        {
            if (_rtspBroken && _rtspUrl != null)
            {
                RtspReconnect();
                continue;
            }

            _encodeEvent.WaitOne(GetEncodeWaitMs());
            if (_disposed) break;

            var tex = Interlocked.Exchange(ref _pendingTexture, IntPtr.Zero);
            var w = _pendingWidth;
            var h = _pendingHeight;
            bool keepAliveFrame = false;

            if (tex == IntPtr.Zero)
            {
                bool keyframeRequested = _rtspUrl == null && Interlocked.CompareExchange(ref _pendingHttpKeyframeRequests, 0, 0) > 0;
                if (_keepAliveTexture != IntPtr.Zero && (keyframeRequested || IsKeepAliveDue(System.Diagnostics.Stopwatch.GetTimestamp())))
                {
                    Marshal.AddRef(_keepAliveTexture);
                    tex = _keepAliveTexture;
                    w = _keepAliveW;
                    h = _keepAliveH;
                    keepAliveFrame = true;
                }
                if (tex == IntPtr.Zero) continue;
            }

            try
            {
                EncodeFrameInternalLocked(tex, w, h, keepAliveFrame);
                long encodedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastEncodeTicks = encodedTicks;
                ScheduleNextKeepAlive(encodedTicks);

                var prev = _keepAliveTexture;
                Marshal.AddRef(tex);
                _keepAliveTexture = tex;
                _keepAliveW = w;
                _keepAliveH = h;
                if (prev != IntPtr.Zero) Marshal.Release(prev);
            }
            catch (Exception ex)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] Encode error (frame {_totalFrames}): {ex}");
            }
            finally
            {
                Marshal.Release(tex);
            }

        }
        if (_keepAliveTexture != IntPtr.Zero) { Marshal.Release(_keepAliveTexture); _keepAliveTexture = IntPtr.Zero; }
        Log.Msg($"[FfmpegEnc:{_streamId}] Encode thread stopped");
    }

    private int GetEncodeWaitMs()
    {
        long nextTicks = Interlocked.Read(ref _nextKeepAliveTicks);
        int intervalMs = GetKeepAliveIntervalMs();
        if (nextTicks == 0 || _keepAliveTexture == IntPtr.Zero) return intervalMs;

        long remainingTicks = nextTicks - System.Diagnostics.Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) return 0;

        double remainingMs = (double)remainingTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return Math.Clamp((int)Math.Ceiling(remainingMs), 1, intervalMs);
    }

    private bool IsKeepAliveDue(long nowTicks)
    {
        long nextTicks = Interlocked.Read(ref _nextKeepAliveTicks);
        return nextTicks != 0 && nowTicks >= nextTicks;
    }

    private void ScheduleNextKeepAlive(long nowTicks)
    {
        long intervalTicks = System.Diagnostics.Stopwatch.Frequency / GetKeepAliveFps();
        Interlocked.Exchange(ref _nextKeepAliveTicks, nowTicks + intervalTicks);
    }

    private int GetKeepAliveFps()
    {
        return Math.Clamp(_keepAliveFps, 1, 240);
    }

    private int GetKeepAliveIntervalMs()
    {
        return Math.Max(1, (int)Math.Ceiling(1000.0 / GetKeepAliveFps()));
    }

    private void EncodeFrameInternalLocked(IntPtr srcTexture, uint width, uint height, bool keepAliveFrame)
    {
        int ret;
        long ringBefore = _ringWritePos;
        bool requestHttpKeyframe = false;

        try
        {
            if (_disposed) return;

            using (DesktopBuddyMod.Perf.Time("ffmpeg_get_buffer"))
            {
                ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }
            }

            if (_needsVideoProcessor || _needsGpuScale)
            {
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    CopyWithD3dContextLock(() =>
                    {
                        VideoProcessorConvert(srcTexture);
                        IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                        int dstIndex = (int)_hwFrame->data[1];
                        CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, _vpOutputTexture, (int)_width, (int)_height);
                    });
                }
            }
            else
            {
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    CopyWithD3dContextLock(() =>
                    {
                        IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                        int dstIndex = (int)_hwFrame->data[1];
                        CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
                    });
                }
            }

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSec = (double)(nowTicks - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
            long videoPts = (long)(elapsedSec * 90000);
            if (videoPts <= Interlocked.Read(ref _lastVideoPts))
            {
                ffmpeg.av_frame_unref(_hwFrame);
                return;
            }
            Interlocked.Exchange(ref _lastVideoPts, videoPts);
            _hwFrame->pts = videoPts;
            _hwFrame->width = (int)_width;
            _hwFrame->height = (int)_height;
            requestHttpKeyframe = _rtspUrl == null && Interlocked.Exchange(ref _pendingHttpKeyframeRequests, 0) > 0;
            _hwFrame->pict_type = requestHttpKeyframe
                ? AVPictureType.AV_PICTURE_TYPE_I
                : AVPictureType.AV_PICTURE_TYPE_NONE;

            using (DesktopBuddyMod.Perf.Time("ffmpeg_encode"))
            {
                ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                ffmpeg.av_frame_unref(_hwFrame);
                if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_send_frame failed: {FfmpegError(ret)}"); return; }
            }

            using (DesktopBuddyMod.Perf.Time("ffmpeg_mux"))
            {
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] avcodec_receive_packet failed: {FfmpegError(ret)}"); break; }

                    _pkt->stream_index = _stream->index;
                    ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _stream->time_base);

                    bool isKey = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    long httpKeyframeRingPos = -1;

                    lock (_muxerLock)
                    {
                        if (_rtspBroken) break;
                        if (isKey && _rtspUrl == null)
                        {
                            ffmpeg.avio_flush(_fmtCtx->pb);
                            httpKeyframeRingPos = _ringWritePos;
                        }
                        ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _pkt);
                        if (ret < 0)
                        {
                            Log.Msg($"[FfmpegEnc:{_streamId}] av_interleaved_write_frame (video) failed: {FfmpegError(ret)}");
                            if (_rtspUrl != null) _rtspBroken = true;
                        }
                        else if (httpKeyframeRingPos >= 0)
                        {
                            ffmpeg.avio_flush(_fmtCtx->pb);
                            Interlocked.Exchange(ref _lastKeyframeRingPos, httpKeyframeRingPos);
                        }
                    }

                    ffmpeg.av_packet_unref(_pkt);
                }
            }

            if (_rtspUrl == null)
            {
                lock (_muxerLock)
                {
                    ffmpeg.avio_flush(_fmtCtx->pb);
                }
            }
        }
        finally
        {
            if (_hwFrame != null)
                ffmpeg.av_frame_unref(_hwFrame);
        }

        _totalFrames++;
        if (keepAliveFrame) _keepAliveFramesEncoded++;
        if (requestHttpKeyframe) _httpJoinKeyframesEncoded++;
        long ringAfter = _ringWritePos;
        bool logFrame = _totalFrames <= 5 ||
            _totalFrames % 300 == 0 ||
            (keepAliveFrame && _keepAliveFramesEncoded <= 8) ||
            (requestHttpKeyframe && _httpJoinKeyframesEncoded <= 8);
        if (logFrame)
            Log.Msg($"[FfmpegEnc:{_streamId}] Frame #{_totalFrames} ({width}x{height}), keepAlive={_keepAliveFramesEncoded}, keepAliveFrame={keepAliveFrame}, joinKeyframe={requestHttpKeyframe}, joinKeyframes={_httpJoinKeyframesEncoded}, bytesWritten={ringAfter - ringBefore}, ringPos={ringAfter}");
        LogResourcesIfDue();
    }

    private void LogResourcesIfDue()
    {
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long previousTicks = Interlocked.Read(ref _lastResourceLogTicks);
        double elapsedMs = previousTicks != 0
            ? (double)(nowTicks - previousTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            : 0.0;
        if (previousTicks != 0)
        {
            if (elapsedMs < 2000.0) return;
        }

        if (Interlocked.CompareExchange(ref _lastResourceLogTicks, nowTicks, previousTicks) != previousTicks)
            return;

        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            double privateMb = process.PrivateMemorySize64 / 1048576.0;
            double workingMb = process.WorkingSet64 / 1048576.0;
            double managedMb = GC.GetTotalMemory(false) / 1048576.0;
            long ringPos = Interlocked.Read(ref _ringWritePos);
            long previousRingPos = Interlocked.Exchange(ref _lastResourceLogRingPos, ringPos);
            double muxMbps = previousRingPos > 0 && ringPos >= previousRingPos && previousTicks != 0
                ? (ringPos - previousRingPos) * 8.0 / elapsedMs / 1000.0
                : 0.0;
            Log.Msg($"[FfmpegEnc:{_streamId}] Resources: private={privateMb:F1}MB working={workingMb:F1}MB managed={managedMb:F1}MB frames={_totalFrames} keepAlive={_keepAliveFramesEncoded} ringPos={ringPos} muxMbps={muxMbps:F2}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] Resource log failed: {ex.Message}");
        }
    }

    private static int GetStreamFps()
    {
        int configured = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.StreamFps) ?? 60;
        return Math.Clamp(configured, 1, 240);
    }

    private void AudioEncodeLoop()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread started");
        while (!_disposed)
        {
            Thread.Sleep(33);
            if (_disposed) break;
            try { EncodeAudio(); }
            catch (Exception ex) { if (!_disposed) Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode error: {ex.Message}"); }
        }
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread stopped");
    }

    private void EncodeAudio()
    {
        if (_audioScratch == null || _audioFrame == null || _audioPkt == null) return;

        int frameSize = _audioCodecCtx->frame_size;
        int channels = 2;
        int samplesPerFrame = frameSize * channels;

        int read = _audioCapture.ReadSamples(_audioScratch, _audioScratch.Length, ref _audioReadPos);
        if (read <= 0) return;

        int offset = 0;
        while (offset + samplesPerFrame <= read)
        {
            ffmpeg.av_frame_make_writable(_audioFrame);
            _audioFrame->nb_samples = frameSize;

            float* left = (float*)_audioFrame->data[0];
            float* right = (float*)_audioFrame->data[1];
            fixed (float* src = &_audioScratch[offset])
            {
                for (int i = 0; i < frameSize; i++)
                {
                    left[i] = src[i * channels];
                    right[i] = src[i * channels + 1];
                }
            }

            _audioFrame->pts = _audioSamplesEncoded;
            _audioSamplesEncoded += frameSize;

            int ret = ffmpeg.avcodec_send_frame(_audioCodecCtx, _audioFrame);
            if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] Audio send_frame failed: {FfmpegError(ret)}"); break; }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_audioCodecCtx, _audioPkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) break;

                _audioPkt->stream_index = _audioStream->index;
                ffmpeg.av_packet_rescale_ts(_audioPkt, _audioCodecCtx->time_base, _audioStream->time_base);
                lock (_muxerLock)
                {
                    if (!_rtspBroken)
                    {
                        ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _audioPkt);
                        if (ret < 0)
                        {
                            Log.Msg($"[FfmpegEnc:{_streamId}] av_interleaved_write_frame (audio) failed: {FfmpegError(ret)}");
                            if (_rtspUrl != null) _rtspBroken = true;
                        }
                    }
                }
                ffmpeg.av_packet_unref(_audioPkt);
            }

            offset += samplesPerFrame;
        }

        int unconsumed = read - offset;
        if (unconsumed > 0)
            _audioReadPos -= unconsumed;
    }

    private static void CopyTextureToFrame(IntPtr deviceContext, IntPtr dstTexture, int dstArrayIndex, IntPtr srcTexture, int width, int height)
    {
        const int Ctx_CopySubresourceRegion = 46;

        var box = stackalloc uint[6];
        box[0] = 0; box[1] = 0; box[2] = 0;
        box[3] = (uint)width; box[4] = (uint)height; box[5] = 1;

        var vtable = *(IntPtr**)deviceContext;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)vtable[Ctx_CopySubresourceRegion];
        fn(deviceContext, dstTexture, (uint)dstArrayIndex, 0, 0, 0, srcTexture, 0, box);
    }

    private void CopyWithD3dContextLock(Action copy)
    {
        if (_d3dContextLock == null)
        {
            copy();
            return;
        }

        lock (_d3dContextLock)
            copy();
    }

    private void RecordReaderOverrun(long readPos, long liveWritePos, long backlogBytes)
    {
        DesktopBuddyMod.Perf.IncrementCounter("stream_reader_overrun");
        Interlocked.Increment(ref _readerOverrunEvents);
        UpdateMax(ref _readerOverrunMaxBacklogBytes, backlogBytes);

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long previousLogTicks = Interlocked.Read(ref _readerLastOverrunLogTicks);
        if (previousLogTicks != 0)
        {
            double elapsedMs = (double)(nowTicks - previousLogTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMs < 5000.0) return;
        }

        if (Interlocked.CompareExchange(ref _readerLastOverrunLogTicks, nowTicks, previousLogTicks) != previousLogTicks)
            return;

        long events = Interlocked.Exchange(ref _readerOverrunEvents, 0);
        long maxBacklog = Interlocked.Exchange(ref _readerOverrunMaxBacklogBytes, 0);
        Log.Msg($"[FfmpegEnc:{_streamId}] Reader overrun summary: events={events}, maxBacklog={maxBacklog} bytes, ringSize={RING_SIZE} bytes, lastReadPos={readPos}, liveWritePos={liveWritePos}, backlog={backlogBytes} bytes; closing reader for clean reconnect");
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

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

        hr = Marshal.QueryInterface(d3dDevice, ref iidVD, out _vpDevice);
        if (hr < 0) throw new Exception($"QueryInterface ID3D11VideoDevice failed hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(_deviceContext, ref iidVC, out _vpContext);
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

    private void VideoProcessorConvert(IntPtr bgraTexture)
    {
        if (_vpInputView == IntPtr.Zero || _vpInputViewTex != bgraTexture)
        {
            if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; }
            var ivDesc = new VP_INPUT_VIEW_DESC { FourCC = 0, ViewDimension = 1, MipSlice = 0, ArraySlice = 0 };
            var vpDevVt = *(IntPtr**)_vpDevice;
            var createIVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_INPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[8];
            int hr = createIVFn(_vpDevice, bgraTexture, _vpEnum, &ivDesc, out _vpInputView);
            if (hr < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] CreateVideoProcessorInputView failed hr=0x{hr:X8}"); _vpInputView = IntPtr.Zero; return; }
            _vpInputViewTex = bgraTexture;
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
        if (bltHr < 0) Log.Msg($"[FfmpegEnc:{_streamId}] VideoProcessorBlt failed hr=0x{bltHr:X8}");
    }

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    private readonly string _rtspUrl;

    public FfmpegEncoder(int streamId, string rtspUrl = null)
    {
        _streamId = streamId;
        _rtspUrl = rtspUrl;
    }


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose === START ===");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose ENTER stream={_streamId} initialized={_initialized} disposed={_disposed}");
        _initialized = false;
        _disposed = true;
        try { _encodeEvent.Set(); } catch { }

        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose initThread.Join START stream={_streamId}");
        _initThread?.Join(5000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose initThread.Join DONE stream={_streamId}");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose audioThread.Join START stream={_streamId}");
        _audioEncodeThread?.Join(2000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose audioThread.Join DONE stream={_streamId}");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose encodeThread.Join START stream={_streamId}");
        _encodeThread?.Join(2000);
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose encodeThread.Join DONE stream={_streamId}");

        var ctxLock = _d3dContextLock;
        bool gotLock = false;
        if (ctxLock != null)
        {
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter START stream={_streamId}");
            gotLock = Monitor.TryEnter(ctxLock, 5000);
            if (!gotLock)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] WARNING: could not acquire D3D lock, skipping FFmpeg cleanup to avoid crash");
                Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter TIMEOUT stream={_streamId}");
                _fmtCtx = null; _codecCtx = null; _pkt = null; _hwFrame = null;
                return;
            }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D TryEnter DONE stream={_streamId}");
        }
        try
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: writing trailer");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose trailer START stream={_streamId}");
            if (_fmtCtx != null)
            {
                try { ffmpeg.av_write_trailer(_fmtCtx); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: trailer error: {ex.Message}"); }
            }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose trailer DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing packets/frames");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose packets/frames START stream={_streamId}");
            try { if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: pkt free error: {ex.Message}"); _pkt = null; }
            try { if (_audioPkt != null) { var p = _audioPkt; ffmpeg.av_packet_free(&p); _audioPkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioPkt free error: {ex.Message}"); _audioPkt = null; }
            try { if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrame free error: {ex.Message}"); _hwFrame = null; }
            try { if (_audioFrame != null) { var f = _audioFrame; ffmpeg.av_frame_free(&f); _audioFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioFrame free error: {ex.Message}"); _audioFrame = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose packets/frames DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing VP resources");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose VP START stream={_streamId}");
            try
            {
                if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; _vpInputViewTex = IntPtr.Zero; }
                if (_vpOutputView != IntPtr.Zero) { Marshal.Release(_vpOutputView); _vpOutputView = IntPtr.Zero; }
                if (_vpOutputTexture != IntPtr.Zero) { Marshal.Release(_vpOutputTexture); _vpOutputTexture = IntPtr.Zero; }
                if (_vpProcessor != IntPtr.Zero) { Marshal.Release(_vpProcessor); _vpProcessor = IntPtr.Zero; }
                if (_vpEnum != IntPtr.Zero) { Marshal.Release(_vpEnum); _vpEnum = IntPtr.Zero; }
                if (_vpContext != IntPtr.Zero) { Marshal.Release(_vpContext); _vpContext = IntPtr.Zero; }
                if (_vpDevice != IntPtr.Zero) { Marshal.Release(_vpDevice); _vpDevice = IntPtr.Zero; }
            }
            catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: VP cleanup error: {ex.Message}"); }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose VP DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing codec contexts");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose codec contexts START stream={_streamId}");
            try { if (_audioCodecCtx != null) { var c = _audioCodecCtx; ffmpeg.avcodec_free_context(&c); _audioCodecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioCodec free error: {ex.Message}"); _audioCodecCtx = null; }
            try { if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: codec free error: {ex.Message}"); _codecCtx = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose codec contexts DONE stream={_streamId}");

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing hw contexts");
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose hw contexts START stream={_streamId}");
            try { if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrames free error: {ex.Message}"); _hwFramesCtx = null; }
            try { if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwDevice free error: {ex.Message}"); _hwDeviceCtx = null; }
            Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose hw contexts DONE stream={_streamId}");
        }
        finally
        {
            if (gotLock)
            {
                Monitor.Exit(ctxLock);
                Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose D3D lock EXIT stream={_streamId}");
            }
        }
        _audioCapture = null;
        if (_keepAliveTexture != IntPtr.Zero) { try { Marshal.Release(_keepAliveTexture); } catch { } _keepAliveTexture = IntPtr.Zero; }

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing format context");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose format context START stream={_streamId}");
        try
        {
            if (_fmtCtx != null)
            {
                if (_rtspUrl != null)
                {
                    if (_fmtCtx->pb != null)
                    {
                        var pb = _fmtCtx->pb;
                        ffmpeg.avio_closep(&pb);
                        _fmtCtx->pb = null;
                    }
                }
                else if (_fmtCtx->pb != null)
                {
                    var pb = _fmtCtx->pb;
                    ffmpeg.avio_context_free(&pb);
                    _fmtCtx->pb = null;
                }
                ffmpeg.avformat_free_context(_fmtCtx);
                _fmtCtx = null;
            }
        }
        catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: fmtCtx free error: {ex.Message}"); _fmtCtx = null; }
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose format context DONE stream={_streamId}");

        if (_selfHandle.IsAllocated) _selfHandle.Free();

        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose events START stream={_streamId}");
        try { _dataAvailable.Dispose(); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: dataAvailable dispose error: {ex.Message}"); }
        try { _encodeEvent.Dispose(); } catch { }
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose events DONE stream={_streamId}");

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose === DONE === {_totalFrames} total frames");
        Log.MsgImmediate($"[CleanupTrace] FfmpegEncoder.Dispose EXIT stream={_streamId} totalFrames={_totalFrames}");
    }
}

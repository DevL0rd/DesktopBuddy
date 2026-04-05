using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// GPU-accelerated H.264 encoder using FFmpeg's h264_nvenc + in-process MPEG-TS muxing.
/// No external ffmpeg.exe process. No NvEncSharp. D3D11 textures stay on GPU.
/// MPEG-TS data written directly to ring buffer via custom AVIOContext callback.
/// </summary>
public sealed unsafe class FfmpegEncoder : IDisposable
{
    private readonly int _streamId;
    private bool _initialized;
    private bool _initFailed;

    // FFmpeg video contexts
    private AVCodecContext* _codecCtx;
    private AVFormatContext* _fmtCtx;
    private AVIOContext* _ioCtx;
    private AVStream* _stream;
    private AVBufferRef* _hwDeviceCtx;
    private AVBufferRef* _hwFramesCtx;
    private AVFrame* _hwFrame;
    private AVPacket* _pkt;

    // FFmpeg audio contexts
    private AVCodecContext* _audioCodecCtx;
    private AVStream* _audioStream;
    private AVFrame* _audioFrame;
    private SwrContext* _swrCtx; // Resampler for sample rate/format conversion
    private AudioCapture _audioCapture;
    private long _audioReadPos; // Position in AudioCapture ring buffer
    private long _audioSamplesEncoded;
    private float[] _audioScratch; // Temp buffer for reading from AudioCapture

    // Ring buffer: MPEG-TS data written here by AVIO callback. HTTP clients read from it.
    private byte[] _ringBuffer;
    private long _ringWritePos;
    private readonly object _ringLock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);

    private uint _width, _height;
    private int _totalFrames;
    private readonly uint _fps = 30;

    private const int RING_SIZE = 4 * 1024 * 1024; // 4MB
    private const int AVIO_BUFFER_SIZE = 65536;
    private const byte MPEGTS_SYNC = 0x47;
    private const int MPEGTS_PACKET_SIZE = 188;

    // Async encode: WGC callback signals new frame, encode thread does the work
    private Thread _encodeThread;
    private volatile bool _disposed;
    private int _disposeGuard; // Interlocked guard for idempotent Dispose
    private readonly AutoResetEvent _frameSignal = new(false);
    private volatile IntPtr _pendingTexture; // Set by WGC callback, consumed by encode thread
    private volatile uint _pendingWidth, _pendingHeight;
    private IntPtr _deviceContext; // FFmpeg's D3D11 device context for encode thread
    private object _d3dContextLock; // Shared lock with WgcCapture to serialize D3D11 immediate context access
    private IntPtr _lastTexture; // Last encoded texture for keepalive re-encode

    // D3D11 Video Processor for BGRA→NV12 conversion (AMF path)
    private bool _needsVideoProcessor;
    private IntPtr _vpDevice, _vpContext, _vpEnum, _vpProcessor;
    private IntPtr _vpInputView, _vpOutputView, _vpNv12Texture;
    private uint _lastWidth, _lastHeight;
    private long _startTicks;
    private long _lastVideoPts = -1; // Ensure monotonic pts
    private long _lastKeyframeRingPos; // Ring buffer position of the latest keyframe start (written under _ringLock)

    // Pin this delegate so GC doesn't collect it while AVIO holds a pointer
    private avio_alloc_context_write_packet _writeCallbackDelegate;
    private GCHandle _selfHandle;

    public bool IsInitialized => _initialized;
    public bool IsRunning => _initialized;

    // Static init — set FFmpeg DLL search path once
    private static bool _ffmpegPathSet;
    private static readonly object _ffmpegInitLock = new();

    public static void SetFfmpegPath()
    {
        lock (_ffmpegInitLock)
        {
            if (_ffmpegPathSet) return;

            string dllDir = FindFfmpegDlls();
            if (dllDir == null)
            {
                ResoniteMod.Msg("[FFmpeg] FATAL: Could not find FFmpeg shared libraries (avcodec, avformat, avutil)");
                return;
            }

            ffmpeg.RootPath = dllDir;
            DynamicallyLoadedBindings.Initialize();
            ResoniteMod.Msg($"[FFmpeg] Library path: {dllDir}");

            // Log version
            uint ver = ffmpeg.avcodec_version();
            ResoniteMod.Msg($"[FFmpeg] avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

            _ffmpegPathSet = true;
        }
    }

    public static string FindFfmpegDlls()
    {
        // FFmpeg DLLs live in {Resonite}/ffmpeg/ — deployed by the build
        var modDir = Path.GetDirectoryName(typeof(FfmpegEncoder).Assembly.Location) ?? "";
        var ffmpegDir = Path.GetFullPath(Path.Combine(modDir, "..", "ffmpeg"));
        if (File.Exists(Path.Combine(ffmpegDir, "avcodec-61.dll")))
            return ffmpegDir;
        return null;
    }

    /// <summary>Wait for new data (up to timeoutMs).</summary>
    public void WaitForData(int timeoutMs)
    {
        _dataAvailable.Wait(timeoutMs);
    }

    /// <summary>Async wait for new data.</summary>
    public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
    {
        return _dataAvailable.WaitAsync(timeoutMs);
    }

    /// <summary>
    /// Read MPEG-TS data from the ring buffer. Returns bytes read.
    /// </summary>
    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned)
    {
        lock (_ringLock)
        {
            long available = _ringWritePos - readPos;
            if (available <= 0) return 0;

            if (available > RING_SIZE)
            {
                readPos = _ringWritePos - RING_SIZE;
                available = RING_SIZE;
                aligned = false;
            }

            if (!aligned)
            {
                // Jump to the latest keyframe so VLC gets an IDR + SPS/PPS on reconnect.
                // Without this, VLC starts on a random P-frame it can't decode and dies
                // within its 300ms cache window.
                long kfPos = Interlocked.Read(ref _lastKeyframeRingPos);
                if (kfPos > 0 && kfPos >= _ringWritePos - RING_SIZE && kfPos < _ringWritePos)
                {
                    readPos = kfPos;
                    available = _ringWritePos - readPos;
                    aligned = true;
                }
                else
                {
                    // Fallback: scan for MPEG-TS sync bytes
                    for (long s = readPos; s < _ringWritePos - MPEGTS_PACKET_SIZE; s++)
                    {
                        byte b = _ringBuffer[(int)(s % RING_SIZE)];
                        if (b == MPEGTS_SYNC)
                        {
                            byte next = _ringBuffer[(int)((s + MPEGTS_PACKET_SIZE) % RING_SIZE)];
                            if (next == MPEGTS_SYNC)
                            {
                                readPos = s;
                                available = _ringWritePos - readPos;
                                aligned = true;
                                break;
                            }
                        }
                    }
                }
                if (!aligned) return 0;
            }

            int toRead = (int)Math.Min(available, buffer.Length);
            int ringPos = (int)(readPos % RING_SIZE);
            int firstChunk = Math.Min(toRead, RING_SIZE - ringPos);
            Buffer.BlockCopy(_ringBuffer, ringPos, buffer, 0, firstChunk);
            if (firstChunk < toRead)
                Buffer.BlockCopy(_ringBuffer, 0, buffer, firstChunk, toRead - firstChunk);
            readPos += toRead;
            return toRead;
        }
    }

    /// <summary>
    /// Initialize encoder. Call on first frame with the D3D11 device pointer.
    /// </summary>
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

            _width = width & ~1u;
            _height = height & ~1u;

            // GPU encoders have minimum dimension requirements. AMF crashes internally (AVERROR_BUG)
            // on dimensions below ~128px. Use 128 as minimum for all encoders.
            if (_width < 128 || _height < 128)
            {
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Window too small for encoding: {_width}x{_height} (min 128x128)");
                _initFailed = true; return false;
            }

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Initializing: {_width}x{_height} @ {_fps}fps");

            // Try GPU-accelerated encoders: NVENC (NVIDIA) > AMF (AMD)
            // QSV (Intel) excluded — requires AV_HWDEVICE_TYPE_QSV + AV_PIX_FMT_QSV, incompatible with our D3D11VA setup
            bool useHevc = width > 4096 || height > 4096;
            string[] encoders = useHevc
                ? new[] { "hevc_nvenc", "hevc_amf" }
                : new[] { "h264_nvenc", "h264_amf" };

            AVCodec* codec = null;
            string codecName = null;
            int ret = -1;

            foreach (var name in encoders)
            {
                codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] {name} not available"); continue; }

                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Trying {name}...");

                // Clean up previous failed attempt
                if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }

                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null) continue;

                _codecCtx->width = (int)_width;
                _codecCtx->height = (int)_height;
                _codecCtx->time_base = new AVRational { num = 1, den = (int)_fps };
                _codecCtx->framerate = new AVRational { num = (int)_fps, den = 1 };
                // Frequent keyframes (every ~0.33s) so VLC can recover quickly after tunnel
                // reconnects — Resonite's libVLC has only 300ms cache, so it needs an IDR
                // within that window when starting mid-stream.
                _codecCtx->gop_size = (int)_fps / 3;
                _codecCtx->max_b_frames = 0;
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY | ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                bool isAmf = name.Contains("amf");

                // AMF uses CBR instead of vbr_peak to avoid massive overshoot (vbr_peak hit 9+Mbps).
                // Same target as NVENC — bandwidth isn't the issue, tunnel QUIC stream recycling is.
                if (isAmf)
                {
                    _codecCtx->bit_rate = 8_000_000;
                    _codecCtx->rc_max_rate = 10_000_000;
                    _codecCtx->rc_buffer_size = 8_000_000;
                }
                else
                {
                    _codecCtx->bit_rate = 8_000_000;
                    _codecCtx->rc_max_rate = 12_000_000;
                    _codecCtx->rc_buffer_size = 8_000_000;
                }

                // NVENC accepts BGRA as sw_format directly. AMF needs NV12 (BGRA not in AMF format_map,
                // BGR0 not in D3D11VA hwcontext). For AMF, we create NV12 hw frames and use D3D11
                // Video Processor to convert BGRA→NV12 before encoding.
                var swFormat = isAmf
                    ? AVPixelFormat.AV_PIX_FMT_NV12
                    : AVPixelFormat.AV_PIX_FMT_BGRA;
                SetupHardwareContext(d3dDevice, swFormat);

                AVDictionary* opts = null;
                if (name.Contains("nvenc"))
                {
                    ffmpeg.av_dict_set(&opts, "preset", "p1", 0);
                    ffmpeg.av_dict_set(&opts, "tune", "ull", 0);
                    ffmpeg.av_dict_set(&opts, "rc", "vbr", 0);
                    ffmpeg.av_dict_set(&opts, "zerolatency", "1", 0);
                    ffmpeg.av_dict_set(&opts, "delay", "0", 0);
                    ffmpeg.av_dict_set(&opts, "rc-lookahead", "0", 0);
                }
                else if (isAmf)
                {
                    ffmpeg.av_dict_set(&opts, "usage", "ultralowlatency", 0);
                    ffmpeg.av_dict_set(&opts, "quality", "speed", 0);
                    // CBR keeps bitrate predictable — vbr_peak spiked to 9+ Mbps on AMF,
                    // overwhelming tunnel bandwidth and causing ring buffer overflows
                    ffmpeg.av_dict_set(&opts, "rc", "cbr", 0);
                    ffmpeg.av_dict_set(&opts, "enforce_hrd", "1", 0);
                }

                lock (_d3dContextLock)
                {
                    ret = ffmpeg.avcodec_open2(_codecCtx, codec, &opts);
                }
                ffmpeg.av_dict_free(&opts);

                if (ret >= 0) { codecName = name; _needsVideoProcessor = name.Contains("amf"); break; }
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] {name} failed: {FfmpegError(ret)}");
            }

            if (ret < 0 || codecName == null)
            {
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] No GPU encoder available (need NVIDIA, AMD, or Intel GPU)");
                // Clean up partially-allocated contexts from the last failed encoder attempt.
                // If left dangling, Dispose() may crash freeing a codec context left in a bad
                // state (e.g. AMF AVERROR_BUG leaves corrupt internal pointers).
                if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }
                _initFailed = true; return false;
            }

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Codec opened: {codecName}");

            // Allocate frame and packet
            _hwFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            // Store audio capture reference before muxer setup (muxer adds audio stream if available)
            _audioCapture = audioCapture;
            _audioReadPos = 0;
            _audioSamplesEncoded = 0;

            // Set up MPEG-TS muxer with custom I/O to ring buffer
            SetupMuxer();

            // Cache device context for encode thread
            var hwDevCtxData = (AVHWDeviceContext*)_hwDeviceCtx->data;
            var d3d11DevCtxData = (AVD3D11VADeviceContext*)hwDevCtxData->hwctx;
            _deviceContext = (IntPtr)d3d11DevCtxData->device_context;

            // AMF: set up D3D11 Video Processor for BGRA→NV12 conversion (needs _deviceContext)
            if (_needsVideoProcessor)
                SetupVideoProcessor(d3dDevice, _width, _height);

            _ringBuffer = new byte[RING_SIZE];
            _ringWritePos = 0;
            _initialized = true;

            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            // Start background encode thread
            _encodeThread = new Thread(EncodeThreadLoop) { IsBackground = true, Name = $"FfmpegEnc_{_streamId}" };
            _encodeThread.Start();

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Ready: {_width}x{_height} {codecName}");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Initialize FAILED: {ex}");
            _initFailed = true;
            return false;
        }
        } // lock
    }

    private void SetupHardwareContext(IntPtr d3dDevice, AVPixelFormat swFormat)
    {
        // Create hw device context and inject the WGC D3D11 device.
        // IMPORTANT: We share the same D3D11 device as WgcCapture so textures are compatible.
        // D3D11 immediate contexts are NOT thread-safe, so all D3D context operations in the
        // encode thread MUST be wrapped in _d3dContextLock (same lock as WgcCapture._disposeLock).
        _hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_hwDeviceCtx == null) throw new Exception("av_hwdevice_ctx_alloc failed");

        var hwDevCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
        var d3d11DevCtx = (AVD3D11VADeviceContext*)hwDevCtx->hwctx;

        d3d11DevCtx->device = (ID3D11Device*)d3dDevice;

        lock (_d3dContextLock)
        {
            int ret = ffmpeg.av_hwdevice_ctx_init(_hwDeviceCtx);
            if (ret < 0) throw new Exception($"av_hwdevice_ctx_init failed: {FfmpegError(ret)}");
        }

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] D3D11VA hardware context initialized with device 0x{d3dDevice:X}");

        // Create hardware frames context
        // sw_format differs per encoder: NVENC accepts BGRA, AMF requires NV12
        _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
        if (_hwFramesCtx == null) throw new Exception("av_hwframe_ctx_alloc failed");

        var framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesCtx->sw_format = swFormat;
        framesCtx->width = (int)_width;
        framesCtx->height = (int)_height;
        framesCtx->initial_pool_size = 0;

        lock (_d3dContextLock)
        {
            int ret2 = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret2 < 0) throw new Exception($"av_hwframe_ctx_init failed: {FfmpegError(ret2)}");
        }

        _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Hardware frames context initialized: {_width}x{_height} {swFormat}");
    }

    private void SetupMuxer()
    {
        // Pin ourselves so the write callback can reference us
        _selfHandle = GCHandle.Alloc(this);

        // Allocate MPEG-TS format context
        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mpegts", null);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        // Create custom AVIO context — write callback feeds ring buffer
        byte* ioBuffer = (byte*)ffmpeg.av_malloc(AVIO_BUFFER_SIZE);
        _writeCallbackDelegate = WriteCallback;
        _ioCtx = ffmpeg.avio_alloc_context(
            ioBuffer, AVIO_BUFFER_SIZE,
            1, // write_flag
            (void*)GCHandle.ToIntPtr(_selfHandle),
            null, // read
            _writeCallbackDelegate,
            null  // seek
        );
        if (_ioCtx == null) throw new Exception("avio_alloc_context failed");

        _fmtCtx->pb = _ioCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

        // Add video stream
        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        // Add audio stream if audio capture is available
        if (_audioCapture != null && _audioCapture.IsCapturing)
        {
            SetupAudioStream();
        }

        // Write MPEG-TS header (must be after all streams are added)
        ret = ffmpeg.avformat_write_header(_fmtCtx, null);
        if (ret < 0) throw new Exception($"avformat_write_header failed: {FfmpegError(ret)}");

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] MPEG-TS muxer ready (in-process, no external ffmpeg)");
    }

    private void SetupAudioStream()
    {
        var audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (audioCodec == null) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] AAC encoder not found, audio disabled"); return; }

        _audioCodecCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
        _audioCodecCtx->sample_rate = 48000;
        _audioCodecCtx->ch_layout = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE, nb_channels = 2, u = new AVChannelLayout_u { mask = ffmpeg.AV_CH_LAYOUT_STEREO } };
        _audioCodecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; // AAC needs planar float
        _audioCodecCtx->bit_rate = 128000;
        _audioCodecCtx->time_base = new AVRational { num = 1, den = 48000 };
        _audioCodecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER; // MPEG-TS needs this

        int ret = ffmpeg.avcodec_open2(_audioCodecCtx, audioCodec, null);
        if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Audio codec open failed: {FfmpegError(ret)}"); return; }

        _audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCodecCtx);
        _audioStream->time_base = _audioCodecCtx->time_base;

        // Allocate audio frame (1024 samples per AAC frame)
        _audioFrame = ffmpeg.av_frame_alloc();
        _audioFrame->nb_samples = _audioCodecCtx->frame_size; // Usually 1024 for AAC
        _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioFrame->ch_layout = _audioCodecCtx->ch_layout;
        _audioFrame->sample_rate = 48000;
        ffmpeg.av_frame_get_buffer(_audioFrame, 0);

        // Source is 48kHz float32 stereo — matches encoder, no resampling needed

        _audioScratch = new float[48000 * 2]; // 1 second scratch buffer
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Audio stream added: AAC 48kHz stereo 128kbps");
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

        // Copy MPEG-TS data to ring buffer
        lock (_ringLock)
        {
            // Copy from unmanaged buffer to managed ring buffer
            int ringPos = (int)(_ringWritePos % RING_SIZE);
            int firstChunk = Math.Min(buf_size, RING_SIZE - ringPos);

            Marshal.Copy((IntPtr)buf, _ringBuffer, ringPos, firstChunk);
            if (firstChunk < buf_size)
                Marshal.Copy((IntPtr)(buf + firstChunk), _ringBuffer, 0, buf_size - firstChunk);

            _ringWritePos += buf_size;
        }

        if (_dataAvailable.CurrentCount == 0)
            try { _dataAvailable.Release(); } catch { }

        return buf_size;
    }

    /// <summary>
    /// Queue a D3D11 texture for encoding. Returns immediately — encoding happens on background thread.
    /// The texture must be persistent (not released after this call). NVENC path.
    /// </summary>
    public void EncodeFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_disposed || _initFailed || !_initialized) return;
        if ((width & ~1u) != _width || (height & ~1u) != _height)
        {
            if (_totalFrames == 0)
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch init={_width}x{_height} frame={width}x{height}");
            return;
        }

        // Queue frame for encode thread — overwrites previous if it hasn't been consumed yet (drop old frame)
        _pendingTexture = srcTexture;
        _pendingWidth = width;
        _pendingHeight = height;
        _frameSignal.Set();
    }

    private void EncodeThreadLoop()
    {
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread started");
        long frameInterval = System.Diagnostics.Stopwatch.Frequency / _fps; // ~33ms at 30fps
        long lastEncodeTicks = 0;

        while (!_disposed)
        {
            // Wait for new frame signal — short timeout to check keepalive
            _frameSignal.WaitOne(5);
            if (_disposed) break;

            // Throttle to 30fps max
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if ((now - lastEncodeTicks) < frameInterval)
                continue;

            IntPtr texture = _pendingTexture;
            if (texture != IntPtr.Zero)
            {
                _pendingTexture = IntPtr.Zero;
                _lastTexture = texture;
                _lastWidth = _pendingWidth;
                _lastHeight = _pendingHeight;
            }
            else if (_lastTexture != IntPtr.Zero)
            {
                texture = _lastTexture;
            }
            else
            {
                continue;
            }
            lastEncodeTicks = now;

            if (_disposed) break;
            try
            {
                EncodeFrameInternal(texture, _lastWidth, _lastHeight);
            }
            catch (Exception ex)
            {
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread error (frame {_totalFrames}): {ex}");
            }
        }
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread ended");
    }

    /// <summary>D3D11 texture → copy to hw frame → encode.</summary>
    private void EncodeFrameInternal(IntPtr srcTexture, uint width, uint height)
    {
        int ret;
        AVFrame* frameToEncode;

        lock (_d3dContextLock)
        {
            using (DesktopBuddyMod.Perf.Time("ffmpeg_get_buffer"))
            {
                ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }
            }

            if (_needsVideoProcessor)
            {
                // AMF: convert BGRA source → NV12 via D3D11 Video Processor, then copy NV12 to hw frame
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    VideoProcessorConvert(srcTexture);
                    IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                    int dstIndex = (int)_hwFrame->data[1];
                    CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, _vpNv12Texture, (int)_width, (int)_height);
                }
            }
            else
            {
                // NVENC: direct BGRA copy (hw frame is BGRA, same format as source)
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                    int dstIndex = (int)_hwFrame->data[1];
                    CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
                }
            }
        }
        frameToEncode = _hwFrame;

        // Wall clock pts
        double elapsedSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
        long videoPts = (long)(elapsedSec * _fps);
        if (videoPts <= _lastVideoPts) videoPts = _lastVideoPts + 1;
        _lastVideoPts = videoPts;
        frameToEncode->pts = videoPts;
        frameToEncode->width = (int)_width;
        frameToEncode->height = (int)_height;

        // Encode
        using (DesktopBuddyMod.Perf.Time("ffmpeg_encode"))
        {
            ret = ffmpeg.avcodec_send_frame(_codecCtx, frameToEncode);
            ffmpeg.av_frame_unref(frameToEncode);
            if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] avcodec_send_frame failed: {FfmpegError(ret)}"); return; }
        }

        // Receive encoded packets and mux to MPEG-TS → ring buffer
        using (DesktopBuddyMod.Perf.Time("ffmpeg_mux"))
        {
            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] avcodec_receive_packet failed: {FfmpegError(ret)}"); break; }

                _pkt->stream_index = _stream->index;
                ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _stream->time_base);

                // Record ring buffer position before writing keyframe so new readers
                // can start from a decodable point (IDR + SPS/PPS via MPEG-TS muxer)
                bool isKey = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                if (isKey)
                {
                    ffmpeg.avio_flush(_fmtCtx->pb);
                    Interlocked.Exchange(ref _lastKeyframeRingPos, _ringWritePos);
                }

                ret = ffmpeg.av_write_frame(_fmtCtx, _pkt);
                if (ret < 0) ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] av_write_frame (video) failed: {FfmpegError(ret)}");

                ffmpeg.av_packet_unref(_pkt);
            }
        }

        // Encode audio samples accumulated since last video frame
        if (_audioCodecCtx != null && _audioCapture != null)
        {
            using (DesktopBuddyMod.Perf.Time("ffmpeg_audio"))
                EncodeAudio();
        }

        // Flush AVIO buffer so packets reach the ring buffer immediately
        ffmpeg.avio_flush(_fmtCtx->pb);

        _totalFrames++;
        if (_totalFrames <= 5 || _totalFrames % 300 == 0)
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Frame #{_totalFrames} ({width}x{height}), ringPos={_ringWritePos}");
    }

    private void EncodeAudio()
    {
        if (_audioScratch == null || _audioFrame == null) return;

        int frameSize = _audioCodecCtx->frame_size; // 1024 for AAC
        int channels = 2;
        int samplesPerFrame = frameSize * channels; // interleaved samples needed

        // Read all available audio — no cap, encode everything that's accumulated
        int read = _audioCapture.ReadSamples(_audioScratch, _audioScratch.Length, ref _audioReadPos);
        if (read <= 0) return;

        // Process in AAC frame-sized chunks
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
            if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Audio send_frame failed: {FfmpegError(ret)}"); break; }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_audioCodecCtx, _pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) break;

                _pkt->stream_index = _audioStream->index;
                ffmpeg.av_packet_rescale_ts(_pkt, _audioCodecCtx->time_base, _audioStream->time_base);
                ffmpeg.av_write_frame(_fmtCtx, _pkt);
                ffmpeg.av_packet_unref(_pkt);
            }

            offset += samplesPerFrame;
        }

        // Put back unconsumed samples — only whole AAC frames are encoded,
        // remainder is picked up next call
        int unconsumed = read - offset;
        if (unconsumed > 0)
            _audioReadPos -= unconsumed;
    }

    /// <summary>
    /// Copy source D3D11 texture to the frame's texture array via CopySubresourceRegion.
    /// The source is a single texture, the dest is a texture array (frame->data[0] is texture, data[1] is index).
    /// </summary>
    private static void CopyTextureToFrame(IntPtr deviceContext, IntPtr dstTexture, int dstArrayIndex, IntPtr srcTexture, int width, int height)
    {
        // ID3D11DeviceContext::CopySubresourceRegion vtable index = 46
        const int Ctx_CopySubresourceRegion = 46;

        // D3D11_BOX struct
        var box = stackalloc uint[6]; // left, top, front, right, bottom, back
        box[0] = 0; box[1] = 0; box[2] = 0;
        box[3] = (uint)width; box[4] = (uint)height; box[5] = 1;

        var vtable = *(IntPtr**)deviceContext;
        // CopySubresourceRegion(context, dst, dstSubresource, dstX, dstY, dstZ, src, srcSubresource, pSrcBox)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)vtable[Ctx_CopySubresourceRegion];
        fn(deviceContext, dstTexture, (uint)dstArrayIndex, 0, 0, 0, srcTexture, 0, box);
    }

    // --- D3D11 Video Processor for BGRA→NV12 conversion (AMF) ---

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
        private uint _pad; // align pointers to 8 bytes on x64
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

    private void SetupVideoProcessor(IntPtr d3dDevice, uint w, uint h)
    {
        int hr;
        var iidVD = IID_ID3D11VideoDevice;
        var iidVC = IID_ID3D11VideoContext;

        // Get video interfaces
        hr = Marshal.QueryInterface(d3dDevice, ref iidVD, out _vpDevice);
        if (hr < 0) throw new Exception($"QueryInterface ID3D11VideoDevice failed hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(_deviceContext, ref iidVC, out _vpContext);
        if (hr < 0) throw new Exception($"QueryInterface ID3D11VideoContext failed hr=0x{hr:X8}");

        // Create enumerator
        var desc = new VP_CONTENT_DESC
        {
            InputFrameFormat = 0, // Progressive
            InputFrameRateNum = 30, InputFrameRateDen = 1,
            InputWidth = w, InputHeight = h,
            OutputFrameRateNum = 30, OutputFrameRateDen = 1,
            OutputWidth = w, OutputHeight = h,
            Usage = 1 // OptimalSpeed
        };
        var vpDevVt = *(IntPtr**)_vpDevice;
        var createEnumFn = (delegate* unmanaged[Stdcall]<IntPtr, VP_CONTENT_DESC*, out IntPtr, int>)vpDevVt[10];
        hr = createEnumFn(_vpDevice, &desc, out _vpEnum);
        if (hr < 0) throw new Exception($"CreateVideoProcessorEnumerator failed hr=0x{hr:X8}");

        // Create processor
        var createProcFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, out IntPtr, int>)vpDevVt[4];
        hr = createProcFn(_vpDevice, _vpEnum, 0, out _vpProcessor);
        if (hr < 0) throw new Exception($"CreateVideoProcessor failed hr=0x{hr:X8}");

        // Create NV12 output texture (needs RENDER_TARGET bind flag for VP output)
        var nv12Desc = new TEX2D_DESC
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = 103, // DXGI_FORMAT_NV12
            SampleCount = 1, SampleQuality = 0,
            Usage = 0, // D3D11_USAGE_DEFAULT
            BindFlags = 0x20, // D3D11_BIND_RENDER_TARGET
            CPUAccessFlags = 0, MiscFlags = 0
        };
        var devVt = *(IntPtr**)d3dDevice;
        var createTexFn = (delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, out IntPtr, int>)devVt[5]; // CreateTexture2D
        hr = createTexFn(d3dDevice, &nv12Desc, IntPtr.Zero, out _vpNv12Texture);
        if (hr < 0) throw new Exception($"CreateTexture2D NV12 failed hr=0x{hr:X8}");

        // Create output view for NV12 texture
        var ovDesc = new VP_OUTPUT_VIEW_DESC { ViewDimension = 1, MipSlice = 0 };
        var createOVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_OUTPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[9];
        hr = createOVFn(_vpDevice, _vpNv12Texture, _vpEnum, &ovDesc, out _vpOutputView);
        if (hr < 0) throw new Exception($"CreateVideoProcessorOutputView failed hr=0x{hr:X8}");

        // Set output color space: YCbCr BT.709 studio range
        var vpCtxVt = *(IntPtr**)_vpContext;
        var outCs = new VP_COLOR_SPACE { Value = 0x6 }; // bit1=1(studio RGB range), bit2=1(BT.709)
        var setOutCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, VP_COLOR_SPACE*, void>)vpCtxVt[15];
        setOutCsFn(_vpContext, _vpProcessor, &outCs);

        // Set stream frame format: progressive
        var setFrameFmtFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)vpCtxVt[27];
        setFrameFmtFn(_vpContext, _vpProcessor, 0, 0); // stream 0, progressive

        // Set input color space: RGB full range
        var inCs = new VP_COLOR_SPACE { Value = 0 }; // all zeros = playback, full RGB range
        var setInCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, VP_COLOR_SPACE*, void>)vpCtxVt[28];
        setInCsFn(_vpContext, _vpProcessor, 0, &inCs);

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Video Processor ready: BGRA {w}x{h} → NV12");
    }

    private void VideoProcessorConvert(IntPtr bgraTexture)
    {
        // Create input view for this BGRA texture (created per-frame since texture can change)
        var ivDesc = new VP_INPUT_VIEW_DESC { FourCC = 0, ViewDimension = 1, MipSlice = 0, ArraySlice = 0 };
        var vpDevVt = *(IntPtr**)_vpDevice;
        var createIVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_INPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[8];
        int hr = createIVFn(_vpDevice, bgraTexture, _vpEnum, &ivDesc, out IntPtr inputView);
        if (hr < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] CreateVideoProcessorInputView failed hr=0x{hr:X8}"); return; }

        // Blt: BGRA → NV12
        var stream = new VP_STREAM
        {
            Enable = 1,
            OutputIndex = 0, InputFrameOrField = 0,
            PastFrames = 0, FutureFrames = 0,
            pInputSurface = inputView
        };
        var vpCtxVt = *(IntPtr**)_vpContext;
        var bltFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VP_STREAM*, int>)vpCtxVt[53];
        hr = bltFn(_vpContext, _vpProcessor, _vpOutputView, 0, 1, &stream);
        if (hr < 0) ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] VideoProcessorBlt failed hr=0x{hr:X8}");

        Marshal.Release(inputView);
    }

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    public FfmpegEncoder(int streamId)
    {
        _streamId = streamId;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose === START ===");
        _initialized = false;
        _disposed = true;
        _frameSignal.Set();
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: joining encode thread...");
        if (_encodeThread != null && !_encodeThread.Join(5000))
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] WARNING: encode thread did not exit in 5s");
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: encode thread joined");

        // Lock the D3D context during all FFmpeg cleanup — OnFrameArrived on the WGC
        // callback thread may still be using the same D3D11 immediate context concurrently.
        // The streamer (WgcCapture) hasn't been disposed yet at this point.
        // av_write_trailer flushes the codec which can use the D3D context via NVENC.
        var ctxLock = _d3dContextLock;
        if (ctxLock != null) Monitor.Enter(ctxLock);
        try
        {
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: writing trailer");
            if (_fmtCtx != null)
            {
                try { ffmpeg.av_write_trailer(_fmtCtx); } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: trailer error: {ex.Message}"); }
            }

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing packets/frames");
            try { if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: pkt free error: {ex.Message}"); _pkt = null; }
            try { if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrame free error: {ex.Message}"); _hwFrame = null; }
            try { if (_audioFrame != null) { var f = _audioFrame; ffmpeg.av_frame_free(&f); _audioFrame = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioFrame free error: {ex.Message}"); _audioFrame = null; }
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing codec contexts");
            try { if (_audioCodecCtx != null) { var c = _audioCodecCtx; ffmpeg.avcodec_free_context(&c); _audioCodecCtx = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioCodec free error: {ex.Message}"); _audioCodecCtx = null; }
            try { if (_swrCtx != null) { var s = _swrCtx; ffmpeg.swr_free(&s); _swrCtx = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: swr free error: {ex.Message}"); _swrCtx = null; }
            try { if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: codec free error: {ex.Message}"); _codecCtx = null; }
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing hw contexts");
            try { if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrames free error: {ex.Message}"); _hwFramesCtx = null; }
            try { if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; } } catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwDevice free error: {ex.Message}"); _hwDeviceCtx = null; }
            // Video Processor cleanup
            try
            {
                if (_vpOutputView != IntPtr.Zero) { Marshal.Release(_vpOutputView); _vpOutputView = IntPtr.Zero; }
                if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; }
                if (_vpNv12Texture != IntPtr.Zero) { Marshal.Release(_vpNv12Texture); _vpNv12Texture = IntPtr.Zero; }
                if (_vpProcessor != IntPtr.Zero) { Marshal.Release(_vpProcessor); _vpProcessor = IntPtr.Zero; }
                if (_vpEnum != IntPtr.Zero) { Marshal.Release(_vpEnum); _vpEnum = IntPtr.Zero; }
                if (_vpContext != IntPtr.Zero) { Marshal.Release(_vpContext); _vpContext = IntPtr.Zero; }
                if (_vpDevice != IntPtr.Zero) { Marshal.Release(_vpDevice); _vpDevice = IntPtr.Zero; }
            }
            catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: VP cleanup error: {ex.Message}"); }
        }
        finally { if (ctxLock != null) Monitor.Exit(ctxLock); }
        _audioCapture = null;

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing format context");
        try
        {
            if (_fmtCtx != null)
            {
                if (_fmtCtx->pb != null)
                {
                    var pb = _fmtCtx->pb;
                    ffmpeg.avio_context_free(&pb);
                    _fmtCtx->pb = null;
                }
                ffmpeg.avformat_free_context(_fmtCtx);
                _fmtCtx = null;
            }
        }
        catch (Exception ex) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose: fmtCtx free error: {ex.Message}"); _fmtCtx = null; }

        if (_selfHandle.IsAllocated) _selfHandle.Free();

        try { if (_dataAvailable.CurrentCount == 0) _dataAvailable.Release(); } catch { }
        try { _dataAvailable.Dispose(); } catch { }

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Dispose === DONE === {_totalFrames} total frames");
    }
}

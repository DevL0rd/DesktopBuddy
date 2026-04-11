using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using FFmpeg.AutoGen;

namespace DesktopBuddyRenderer
{
    public sealed unsafe class FfmpegEncoder : IDisposable
    {
        private static ManualLogSource Log => DesktopBuddyRendererPlugin.Log;

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
        private AudioBridge _audioBridge;
        private long _audioSamplesEncoded;
        private float[] _audioScratch;
        private Thread _audioEncodeThread;
        private AVPacket* _audioPkt;

        private byte[] _ringBuffer;
        private long _ringWritePos;
        private readonly object _ringLock = new object();
        private readonly object _muxerLock = new object();
        private readonly ManualResetEventSlim _dataAvailable = new ManualResetEventSlim(false);

        private uint _width, _height;
        private int _totalFrames;
        private readonly uint _fps = 30;

        private const int RING_SIZE = 4 * 1024 * 1024;
        private const int AVIO_BUFFER_SIZE = 65536;
        private const byte MPEGTS_SYNC = 0x47;
        private const int MPEGTS_PACKET_SIZE = 188;
        private const uint D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX = 0x100;

        private volatile bool _disposed;
        private int _disposeGuard;
        private IntPtr _deviceContext;
        private object _d3dContextLock;

        private long _perfQueueFrameTotal, _perfQueueFrameCount;
        private long _perfBridgeAcquireTotal, _perfBridgeCopySourceTotal, _perfBridgeCopyEncTotal;
        private long _perfHwFrameGetTotal, _perfSendFrameTotal, _perfReceivePacketTotal;
        private long _perfFrameIntervalTotal;
        private long _perfLastFrameTick;
        private int _perfReportInterval = 300;

        private Thread _encodeThread;
        private Thread _initThread;
        private volatile int _initStarted;
        private readonly AutoResetEvent _encodeEvent = new AutoResetEvent(false);
        private volatile IntPtr _pendingTexture;
        private volatile uint _pendingWidth, _pendingHeight;

        private bool _needsVideoProcessor;
        private bool _usingExternalD3DDevice = true;
        private IntPtr _sourceDeviceContext;
        private IntPtr _sharedBridgeTextureSource;
        private IntPtr _sharedBridgeTextureEncoder;
        private IntPtr _sharedBridgeMutexSource;
        private IntPtr _sharedBridgeMutexEncoder;
        private IntPtr _vpDevice, _vpContext, _vpEnum, _vpProcessor;
        private IntPtr _vpOutputView, _vpNv12Texture;
        private IntPtr _vpInputView, _vpInputViewTex;
        private long _startTicks;
        private long _lastVideoPts = -1;
        private long _lastKeyframeRingPos;

        private avio_alloc_context_write_packet _writeCallbackDelegate;
        private GCHandle _selfHandle;

        public bool IsInitialized => _initialized;
        public bool IsRunning => _initialized;

        public void Stop()
        {
            _disposed = true;
            _initialized = false;
        }

        private static bool _ffmpegPathSet;
        private static readonly object _ffmpegInitLock = new object();
        private static av_log_set_callback_callback _logCallback;
        private static GCHandle _logCallbackHandle;
        private static readonly Guid IID_IDXGIResource = new Guid(0x035F3AB4, 0x482E, 0x4E50, 0xB4, 0x1F, 0x8A, 0x7F, 0x8B, 0xD8, 0x96, 0x0B);
        private static readonly Guid IID_IDXGIKeyedMutex = new Guid(0x9D8E1289, 0xD7B3, 0x465F, 0x81, 0x26, 0x25, 0x0E, 0x34, 0x9A, 0xF8, 0x5D);
        private static readonly Guid IID_ID3D11Texture2D = new Guid(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);

        private static unsafe void FfmpegLogCallback(void* avcl, int level, string fmt, byte* vl)
        {
            if (level > ffmpeg.AV_LOG_VERBOSE) return;
            // Use av_log_format_line2 to format the message
            var lineBuffer = stackalloc byte[2048];
            int printPrefix = 1;
            int len = ffmpeg.av_log_format_line2(avcl, level, fmt, vl, lineBuffer, 2048, &printPrefix);
            if (len > 0)
            {
                string msg = Marshal.PtrToStringAnsi((IntPtr)lineBuffer, len).TrimEnd('\n', '\r');
                if (!string.IsNullOrEmpty(msg))
                {
                    if (level <= ffmpeg.AV_LOG_ERROR)
                        Log.LogError($"[FFmpeg] {msg}");
                    else if (level <= ffmpeg.AV_LOG_WARNING)
                        Log.LogWarning($"[FFmpeg] {msg}");
                    else
                        Log.LogInfo($"[FFmpeg] {msg}");
                }
            }
        }

        public static void SetFfmpegPath()
        {
            lock (_ffmpegInitLock)
            {
                if (_ffmpegPathSet) return;

                string dllDir = FindFfmpegDlls();
                if (dllDir == null)
                {
                    Log.LogError("[FFmpeg] FATAL: Could not find FFmpeg shared libraries (avcodec, avformat, avutil)");
                    return;
                }

                ffmpeg.RootPath = dllDir;
                DynamicallyLoadedBindings.Initialize();
                Log.LogInfo($"[FFmpeg] Library path: {dllDir}");

                // Install FFmpeg log callback so internal errors are visible in BepInEx log
                _logCallback = FfmpegLogCallback;
                _logCallbackHandle = GCHandle.Alloc(_logCallback);
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_VERBOSE);
                ffmpeg.av_log_set_callback(_logCallback);

                uint ver = ffmpeg.avcodec_version();
                Log.LogInfo($"[FFmpeg] avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

                _ffmpegPathSet = true;
            }
        }

        public static string FindFfmpegDlls()
        {
            // Renderer DLL is in Renderer/BepInEx/plugins — ffmpeg is at Resonite/ffmpeg
            var modDir = Path.GetDirectoryName(typeof(FfmpegEncoder).Assembly.Location) ?? "";
            // Try ../../ffmpeg (from plugins/ → BepInEx/ → Renderer/ → Resonite/ffmpeg)
            string[] candidates = new[]
            {
                Path.GetFullPath(Path.Combine(modDir, "..", "..", "..", "ffmpeg")),
                Path.GetFullPath(Path.Combine(modDir, "..", "..", "ffmpeg")),
                Path.GetFullPath(Path.Combine(modDir, "..", "ffmpeg")),
            };
            foreach (var dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "avcodec-61.dll")))
                    return dir;
            }
            return null;
        }

        public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
        {
            if (_dataAvailable.IsSet)
            {
                _dataAvailable.Reset();
                return System.Threading.Tasks.Task.FromResult(0);
            }
            return System.Threading.Tasks.Task.Run(() =>
            {
                _dataAvailable.Wait(timeoutMs);
                _dataAvailable.Reset();
            });
        }

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
                    long kfPos = Interlocked.Read(ref _lastKeyframeRingPos);
                    if (kfPos > 0 && kfPos >= _ringWritePos - RING_SIZE && kfPos < _ringWritePos)
                    {
                        readPos = kfPos;
                        available = _ringWritePos - readPos;
                        aligned = true;
                    }
                    else
                    {
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

        private readonly object _initLock = new object();

        public bool Initialize(IntPtr d3dDevice, uint width, uint height, object d3dContextLock, AudioBridge audioBridge = null)
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

                    if (_width < 128 || _height < 128)
                    {
                        Log.LogWarning($"[FfmpegEnc:{_streamId}] Window too small for encoding: {_width}x{_height} (min 128x128)");
                        _initFailed = true; return false;
                    }

                    Log.LogInfo($"[FfmpegEnc:{_streamId}] Initializing: {_width}x{_height} @ {_fps}fps");

                    if (_sourceDeviceContext == IntPtr.Zero)
                    {
                        var devVt = *(IntPtr**)d3dDevice;
                        var getImmCtxFn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, void>)devVt[40];
                        getImmCtxFn(d3dDevice, out IntPtr immCtx);
                        if (immCtx != IntPtr.Zero)
                        {
                            _sourceDeviceContext = immCtx;
                            EnableD3D11ContextMultithread(_sourceDeviceContext);
                        }
                        else
                            Log.LogWarning($"[FfmpegEnc:{_streamId}] GetImmediateContext returned null");
                    }

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
                        if (codec == null) { Log.LogInfo($"[FfmpegEnc:{_streamId}] {name} not available"); continue; }

                        Log.LogInfo($"[FfmpegEnc:{_streamId}] Trying {name}...");

                        if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                        if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                        if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }

                        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                        if (_codecCtx == null) continue;

                        _codecCtx->width = (int)_width;
                        _codecCtx->height = (int)_height;
                        _codecCtx->time_base = new AVRational { num = 1, den = 90000 };
                        _codecCtx->framerate = new AVRational { num = (int)_fps, den = 1 };
                        _codecCtx->gop_size = (int)_fps / 3;
                        _codecCtx->max_b_frames = 0;
                        _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY | ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                        bool isAmf = name.Contains("amf");

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

                        var swFormat = isAmf
                            ? AVPixelFormat.AV_PIX_FMT_NV12
                            : AVPixelFormat.AV_PIX_FMT_BGRA;
                        bool useExternalDevice = isAmf;

                        if (_codecCtx->hw_frames_ctx != null)
                        {
                            var hwRef = _codecCtx->hw_frames_ctx;
                            ffmpeg.av_buffer_unref(&hwRef);
                            _codecCtx->hw_frames_ctx = null;
                        }
                        if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                        if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }

                        Log.LogInfo($"[FfmpegEnc:{_streamId}] Calling SetupHardwareContext for {name}, mode={(useExternalDevice ? "unity-device" : "ffmpeg-device")}, swFmt={swFormat}");
                        SetupHardwareContext(d3dDevice, swFormat, useExternalDevice);
                        Log.LogInfo($"[FfmpegEnc:{_streamId}] SetupHardwareContext OK (mode={(useExternalDevice ? "unity-device" : "ffmpeg-device")})");

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
                            ffmpeg.av_dict_set(&opts, "rc", "vbr_peak", 0);
                            ffmpeg.av_dict_set(&opts, "header_insertion_mode", "idr", 0);
                            ffmpeg.av_dict_set(&opts, "log_to_dbg", "1", 0);
                        }

                        Log.LogInfo($"[FfmpegEnc:{_streamId}] Calling avcodec_open2 for {name} (mode={(useExternalDevice ? "unity-device" : "ffmpeg-device")})...");
                        lock (_d3dContextLock)
                        {
                            ret = ffmpeg.avcodec_open2(_codecCtx, codec, &opts);
                        }
                        ffmpeg.av_dict_free(&opts);
                        Log.LogInfo($"[FfmpegEnc:{_streamId}] avcodec_open2 for {name} returned {ret} (mode={(useExternalDevice ? "unity-device" : "ffmpeg-device")})");

                        if (ret >= 0)
                            _usingExternalD3DDevice = useExternalDevice;

                        if (ret >= 0) { codecName = name; _needsVideoProcessor = isAmf; break; }
                        Log.LogInfo($"[FfmpegEnc:{_streamId}] {name} failed: {FfmpegError(ret)}");
                    }

                    if (ret < 0 || codecName == null)
                    {
                        Log.LogError($"[FfmpegEnc:{_streamId}] No GPU encoder available (need NVIDIA, AMD, or Intel GPU)");
                        if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
                        if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
                        if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }
                        _initFailed = true; return false;
                    }

                    Log.LogInfo($"[FfmpegEnc:{_streamId}] Codec opened: {codecName}");

                    _hwFrame = ffmpeg.av_frame_alloc();
                    _pkt = ffmpeg.av_packet_alloc();

                    _audioBridge = audioBridge;
                    _audioSamplesEncoded = 0;

                    SetupMuxer();

                    var hwDevCtxData = (AVHWDeviceContext*)_hwDeviceCtx->data;
                    var d3d11DevCtxData = (AVD3D11VADeviceContext*)hwDevCtxData->hwctx;
                    _deviceContext = (IntPtr)d3d11DevCtxData->device_context;

                    if (_needsVideoProcessor)
                        SetupVideoProcessor(d3dDevice, _width, _height);

                    _ringBuffer = new byte[RING_SIZE];
                    _ringWritePos = 0;
                    _initialized = true;

                    _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                    _encodeThread = new Thread(EncodeLoop) { Name = $"FfmpegEnc:{_streamId}:Encode", IsBackground = true };
                    _encodeThread.Start();
                    Log.LogInfo($"[FfmpegEnc:{_streamId}] Encode thread started");

                    Log.LogInfo($"[FfmpegEnc:{_streamId}] Ready: {_width}x{_height} {codecName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.LogError($"[FfmpegEnc:{_streamId}] Initialize FAILED: {ex}");
                    _initFailed = true;
                    return false;
                }
            }
        }

        public void StartInitializeAsync(IntPtr d3dDevice, uint width, uint height, object d3dContextLock, AudioBridge audioBridge = null)
        {
            if (Interlocked.Exchange(ref _initStarted, 1) != 0) return;
            _initThread = new Thread(() => Initialize(d3dDevice, width, height, d3dContextLock, audioBridge))
            { Name = $"FfmpegEnc:{_streamId}:Init", IsBackground = true };
            _initThread.Start();
        }

        private void SetupHardwareContext(IntPtr d3dDevice, AVPixelFormat swFormat, bool useExternalDevice)
        {
            if (useExternalDevice)
            {
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

                if (_sourceDeviceContext == IntPtr.Zero && d3d11DevCtx->device_context != null)
                {
                    _sourceDeviceContext = (IntPtr)d3d11DevCtx->device_context;
                    Marshal.AddRef(_sourceDeviceContext);
                }

                Log.LogInfo($"[FfmpegEnc:{_streamId}] D3D11VA hardware context initialized with Unity device 0x{d3dDevice:X}");
            }
            else
            {
                AVBufferRef* created = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&created, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (ret < 0 || created == null)
                    throw new Exception($"av_hwdevice_ctx_create failed: {FfmpegError(ret)}");
                _hwDeviceCtx = created;

                var createdHwDevCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
                var createdD3d11DevCtx = (AVD3D11VADeviceContext*)createdHwDevCtx->hwctx;
                EnsureSharedTextureBridge(d3dDevice, (IntPtr)createdD3d11DevCtx->device);
                Log.LogInfo($"[FfmpegEnc:{_streamId}] D3D11VA hardware context initialized with FFmpeg-created device");
            }

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
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Hardware frames context initialized: {_width}x{_height} {swFormat}");
        }

        private void SetupMuxer()
        {
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
            _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
            if (_stream == null) throw new Exception("avformat_new_stream failed");

            ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
            _stream->time_base = _codecCtx->time_base;

            if (_audioBridge != null)
            {
                SetupAudioStream();
            }

            ret = ffmpeg.avformat_write_header(_fmtCtx, null);
            if (ret < 0) throw new Exception($"avformat_write_header failed: {FfmpegError(ret)}");

            Log.LogInfo($"[FfmpegEnc:{_streamId}] MPEG-TS muxer ready");
        }

        private void SetupAudioStream()
        {
            var audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (audioCodec == null) { Log.LogWarning($"[FfmpegEnc:{_streamId}] AAC encoder not found, audio disabled"); return; }

            _audioCodecCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
            _audioCodecCtx->sample_rate = 48000;
            _audioCodecCtx->ch_layout = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE, nb_channels = 2, u = new AVChannelLayout_u { mask = ffmpeg.AV_CH_LAYOUT_STEREO } };
            _audioCodecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _audioCodecCtx->bit_rate = 128000;
            _audioCodecCtx->time_base = new AVRational { num = 1, den = 48000 };
            _audioCodecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            int ret = ffmpeg.avcodec_open2(_audioCodecCtx, audioCodec, null);
            if (ret < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Audio codec open failed: {FfmpegError(ret)}"); return; }

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

            Log.LogInfo($"[FfmpegEnc:{_streamId}] Audio stream added: AAC 48kHz stereo 128kbps");
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

            _dataAvailable.Set();

            return buf_size;
        }

        public void QueueFrame(IntPtr srcTexture, uint width, uint height)
        {
            if (_disposed || _initFailed || !_initialized) return;
            long _queueStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (width == 0 || height == 0)
            {
                if (_totalFrames == 0)
                    Log.LogWarning($"[FfmpegEnc:{_streamId}] Skipping frame: invalid dimensions {width}x{height}");
                return;
            }
            if ((width & ~1u) != _width || (height & ~1u) != _height)
            {
                if (_totalFrames == 0)
                    Log.LogWarning($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch init={_width}x{_height} frame={width}x{height}");
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

            Interlocked.Add(ref _perfQueueFrameTotal, System.Diagnostics.Stopwatch.GetTimestamp() - _queueStart);
            Interlocked.Increment(ref _perfQueueFrameCount);
        }

        private void EncodeLoop()
        {
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Encode thread running");
            while (!_disposed)
            {
                _encodeEvent.WaitOne(100);
                if (_disposed) break;

                var tex = Interlocked.Exchange(ref _pendingTexture, IntPtr.Zero);
                var w = _pendingWidth;
                var h = _pendingHeight;
                if (tex == IntPtr.Zero) continue;

                try
                {
                    EncodeFrameInternalLocked(tex, w, h);
                }
                catch (Exception ex)
                {
                    Log.LogError($"[FfmpegEnc:{_streamId}] Encode error (frame {_totalFrames}): {ex}");
                }
                finally
                {
                    Marshal.Release(tex);
                }
            }
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Encode thread stopped");
        }

        private void EncodeFrameInternalLocked(IntPtr srcTexture, uint width, uint height)
        {
            int ret;
            long t0, t1, t4;
            double freq = System.Diagnostics.Stopwatch.Frequency;

            if (_disposed) return;

            long frameIntervalTicks = 0;
            if (_perfLastFrameTick != 0)
                frameIntervalTicks = System.Diagnostics.Stopwatch.GetTimestamp() - _perfLastFrameTick;
            _perfLastFrameTick = System.Diagnostics.Stopwatch.GetTimestamp();

            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
            t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfHwFrameGetTotal, t1 - t0);
            if (ret < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }

            IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
            int dstIndex = (int)_hwFrame->data[1];

            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_needsVideoProcessor)
            {
                VideoProcessorConvert(srcTexture);
                CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, _vpNv12Texture, (int)_width, (int)_height);
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _perfBridgeCopyEncTotal, t1 - t0);
            }
            else if (_usingExternalD3DDevice)
            {
                CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _perfBridgeCopyEncTotal, t1 - t0);
            }
            else
            {
                CopyTextureToFrameViaSharedBridge(srcTexture, dstTexture, dstIndex);
            }

            double elapsedSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / freq;
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

            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
            t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfSendFrameTotal, t1 - t0);
            ffmpeg.av_frame_unref(_hwFrame);
            if (ret < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] avcodec_send_frame failed: {FfmpegError(ret)}"); return; }

            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] avcodec_receive_packet failed: {FfmpegError(ret)}"); break; }

                _pkt->stream_index = _stream->index;
                ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _stream->time_base);

                bool isKey = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                lock (_muxerLock)
                {
                    if (isKey)
                    {
                        ffmpeg.avio_flush(_fmtCtx->pb);
                        Interlocked.Exchange(ref _lastKeyframeRingPos, _ringWritePos);
                    }
                    ret = ffmpeg.av_write_frame(_fmtCtx, _pkt);
                    if (ret < 0) Log.LogWarning($"[FfmpegEnc:{_streamId}] av_write_frame (video) failed: {FfmpegError(ret)}");
                }

                ffmpeg.av_packet_unref(_pkt);
            }
            t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfReceivePacketTotal, t4 - t0);

            lock (_muxerLock) ffmpeg.avio_flush(_fmtCtx->pb);

            if (frameIntervalTicks > 0)
                Interlocked.Add(ref _perfFrameIntervalTotal, frameIntervalTicks);

            _totalFrames++;
            if (_totalFrames <= 5 || _totalFrames % _perfReportInterval == 0)
            {
                int n = _perfReportInterval;
                double ms(long ticks) => ticks * 1000.0 / freq / n;
                double intervalMs = _perfFrameIntervalTotal * 1000.0 / freq / n;
                Log.LogInfo($"[FfmpegEnc:{_streamId}] Frame #{_totalFrames} ({width}x{height}) ringPos={_ringWritePos} " +
                    $"interval={intervalMs:F1}ms " +
                    $"hwFrameGet={ms(_perfHwFrameGetTotal):F2}ms " +
                    $"bridgeAcquire={ms(_perfBridgeAcquireTotal):F2}ms " +
                    $"bridgeCopySrc={ms(_perfBridgeCopySourceTotal):F2}ms " +
                    $"bridgeCopyEnc={ms(_perfBridgeCopyEncTotal):F2}ms " +
                    $"sendFrame={ms(_perfSendFrameTotal):F2}ms " +
                    $"receivePacket={ms(_perfReceivePacketTotal):F2}ms " +
                    $"queueFrame={_perfQueueFrameTotal * 1000.0 / freq / Math.Max(1, _perfQueueFrameCount):F2}ms");
                _perfHwFrameGetTotal = _perfBridgeAcquireTotal = _perfBridgeCopySourceTotal = _perfBridgeCopyEncTotal = 0;
                _perfSendFrameTotal = _perfReceivePacketTotal = _perfFrameIntervalTotal = 0;
                _perfQueueFrameTotal = _perfQueueFrameCount = 0;
            }
        }

        private void AudioEncodeLoop()
        {
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Audio encode thread started");
            while (!_disposed)
            {
                _encodeEvent.WaitOne(33);
                if (_disposed) break;
                try { EncodeAudio(); }
                catch (Exception ex) { if (!_disposed) Log.LogWarning($"[FfmpegEnc:{_streamId}] Audio encode error: {ex.Message}"); }
            }
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Audio encode thread stopped");
        }

        private void EncodeAudio()
        {
            if (_audioScratch == null || _audioFrame == null || _audioPkt == null || _audioBridge == null) return;

            int frameSize = _audioCodecCtx->frame_size;
            int channels = 2;
            int samplesPerFrame = frameSize * channels;

            int read = _audioBridge.ReadSamples(_audioScratch, _audioScratch.Length);
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
                if (ret < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Audio send_frame failed: {FfmpegError(ret)}"); break; }

                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_audioCodecCtx, _audioPkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;

                    _audioPkt->stream_index = _audioStream->index;
                    ffmpeg.av_packet_rescale_ts(_audioPkt, _audioCodecCtx->time_base, _audioStream->time_base);
                    lock (_muxerLock) ffmpeg.av_write_frame(_fmtCtx, _audioPkt);
                    ffmpeg.av_packet_unref(_audioPkt);
                }

                offset += samplesPerFrame;
            }
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

        private void CopyTextureToFrameViaSharedBridge(IntPtr srcTexture, IntPtr dstTexture, int dstArrayIndex)
        {
            if (_sourceDeviceContext == IntPtr.Zero ||
                _sharedBridgeTextureSource == IntPtr.Zero ||
                _sharedBridgeTextureEncoder == IntPtr.Zero ||
                _sharedBridgeMutexSource == IntPtr.Zero ||
                _sharedBridgeMutexEncoder == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cross-device texture bridge not initialized");
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            AcquireKeyedMutex(_sharedBridgeMutexSource, 0, 1000);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfBridgeAcquireTotal, t1 - t0);
            try
            {
                CopyTextureToFrame(_sourceDeviceContext, _sharedBridgeTextureSource, 0, srcTexture, (int)_width, (int)_height);
                FlushD3DContext(_sourceDeviceContext);
            }
            finally
            {
                ReleaseKeyedMutex(_sharedBridgeMutexSource, 1);
            }
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfBridgeCopySourceTotal, t2 - t1);

            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            AcquireKeyedMutex(_sharedBridgeMutexEncoder, 1, 1000);
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfBridgeAcquireTotal, t4 - t3);
            try
            {
                CopyTextureToFrame(_deviceContext, dstTexture, dstArrayIndex, _sharedBridgeTextureEncoder, (int)_width, (int)_height);
                FlushD3DContext(_deviceContext);
            }
            finally
            {
                ReleaseKeyedMutex(_sharedBridgeMutexEncoder, 0);
            }
            long t5 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _perfBridgeCopyEncTotal, t5 - t4);
        }

        private unsafe void EnsureSharedTextureBridge(IntPtr sourceDevice, IntPtr encoderDevice)
        {
            if (_sharedBridgeTextureSource != IntPtr.Zero && _sharedBridgeTextureEncoder != IntPtr.Zero)
                return;

            if (_sourceDeviceContext == IntPtr.Zero)
                throw new Exception("Source D3D11 context unavailable for shared texture bridge");

            IntPtr sourceTexture = IntPtr.Zero;
            IntPtr encoderTexture = IntPtr.Zero;
            IntPtr sourceMutex = IntPtr.Zero;
            IntPtr encoderMutex = IntPtr.Zero;
            IntPtr dxgiResource = IntPtr.Zero;

            try
            {
                var desc = new TEX2D_DESC
                {
                    Width = _width,
                    Height = _height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = 87,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = 0,
                    BindFlags = 0x20,
                    CPUAccessFlags = 0,
                    MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX,
                };

                var srcDevVt = *(IntPtr**)sourceDevice;
                var createTexFn = (delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, out IntPtr, int>)srcDevVt[5];
                int hr = createTexFn(sourceDevice, &desc, IntPtr.Zero, out sourceTexture);
                if (hr < 0 || sourceTexture == IntPtr.Zero)
                    throw new Exception($"CreateTexture2D shared bridge failed hr=0x{hr:X8}");

                var iidDxgiResource = IID_IDXGIResource;
                hr = Marshal.QueryInterface(sourceTexture, ref iidDxgiResource, out dxgiResource);
                if (hr < 0 || dxgiResource == IntPtr.Zero)
                    throw new Exception($"QueryInterface IDXGIResource failed hr=0x{hr:X8}");

                var resourceVt = *(IntPtr**)dxgiResource;
                var getSharedHandle = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)resourceVt[8];
                hr = getSharedHandle(dxgiResource, out IntPtr sharedHandle);
                if (hr < 0 || sharedHandle == IntPtr.Zero)
                    throw new Exception($"IDXGIResource::GetSharedHandle failed hr=0x{hr:X8}");

                var encDevVt = *(IntPtr**)encoderDevice;
                var openSharedResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, out IntPtr, int>)encDevVt[28];
                var iidTex = IID_ID3D11Texture2D;
                hr = openSharedResource(encoderDevice, sharedHandle, &iidTex, out encoderTexture);
                if (hr < 0 || encoderTexture == IntPtr.Zero)
                    throw new Exception($"ID3D11Device::OpenSharedResource failed hr=0x{hr:X8}");

                var iidKeyedMutex = IID_IDXGIKeyedMutex;
                hr = Marshal.QueryInterface(sourceTexture, ref iidKeyedMutex, out sourceMutex);
                if (hr < 0 || sourceMutex == IntPtr.Zero)
                    throw new Exception($"QueryInterface IDXGIKeyedMutex (source) failed hr=0x{hr:X8}");

                iidKeyedMutex = IID_IDXGIKeyedMutex;
                hr = Marshal.QueryInterface(encoderTexture, ref iidKeyedMutex, out encoderMutex);
                if (hr < 0 || encoderMutex == IntPtr.Zero)
                    throw new Exception($"QueryInterface IDXGIKeyedMutex (encoder) failed hr=0x{hr:X8}");

                _sharedBridgeTextureSource = sourceTexture;
                _sharedBridgeTextureEncoder = encoderTexture;
                _sharedBridgeMutexSource = sourceMutex;
                _sharedBridgeMutexEncoder = encoderMutex;

                sourceTexture = IntPtr.Zero;
                encoderTexture = IntPtr.Zero;
                sourceMutex = IntPtr.Zero;
                encoderMutex = IntPtr.Zero;

                Log.LogInfo($"[FfmpegEnc:{_streamId}] Shared texture bridge ready: Unity device -> FFmpeg device ({_width}x{_height})");
            }
            finally
            {
                if (dxgiResource != IntPtr.Zero) Marshal.Release(dxgiResource);
                if (encoderMutex != IntPtr.Zero) Marshal.Release(encoderMutex);
                if (sourceMutex != IntPtr.Zero) Marshal.Release(sourceMutex);
                if (encoderTexture != IntPtr.Zero) Marshal.Release(encoderTexture);
                if (sourceTexture != IntPtr.Zero) Marshal.Release(sourceTexture);
            }
        }

        private static unsafe void AcquireKeyedMutex(IntPtr keyedMutex, ulong key, uint timeoutMs)
        {
            var vtable = *(IntPtr**)keyedMutex;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)vtable[8];
            int hr = fn(keyedMutex, key, timeoutMs);
            if (hr != 0)
                throw new Exception($"IDXGIKeyedMutex::AcquireSync failed hr=0x{hr:X8}");
        }

        private static unsafe void ReleaseKeyedMutex(IntPtr keyedMutex, ulong key)
        {
            var vtable = *(IntPtr**)keyedMutex;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)vtable[9];
            int hr = fn(keyedMutex, key);
            if (hr != 0)
                throw new Exception($"IDXGIKeyedMutex::ReleaseSync failed hr=0x{hr:X8}");
        }

        private static unsafe void FlushD3DContext(IntPtr deviceContext)
        {
            const int Ctx_FlushLocal = 111;
            var vtable = *(IntPtr**)deviceContext;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[Ctx_FlushLocal];
            fn(deviceContext);
        }

        private static unsafe void EnableD3D11ContextMultithread(IntPtr deviceContext)
        {
            var iidMultithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            int hr = Marshal.QueryInterface(deviceContext, ref iidMultithread, out IntPtr mtPtr);
            if (hr >= 0 && mtPtr != IntPtr.Zero)
            {
                var vtable = *(IntPtr**)mtPtr;
                var setProtFn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtable[4];
                setProtFn(mtPtr, 1);
                Marshal.Release(mtPtr);
            }
        }

        private static readonly Guid IID_ID3D11VideoDevice = new Guid(0x10EC4D5B, 0x975A, 0x4689, 0xB9, 0xE4, 0xD0, 0xAA, 0xC3, 0x0F, 0xE3, 0x33);
        private static readonly Guid IID_ID3D11VideoContext = new Guid(0x61F21C45, 0x3C0E, 0x4A74, 0x9C, 0xEA, 0x67, 0x10, 0x0D, 0x9A, 0xD5, 0xE4);

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

        private void SetupVideoProcessor(IntPtr d3dDevice, uint w, uint h)
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
                InputWidth = w, InputHeight = h,
                OutputFrameRateNum = 30, OutputFrameRateDen = 1,
                OutputWidth = w, OutputHeight = h,
                Usage = 1
            };
            var vpDevVt = *(IntPtr**)_vpDevice;
            var createEnumFn = (delegate* unmanaged[Stdcall]<IntPtr, VP_CONTENT_DESC*, out IntPtr, int>)vpDevVt[10];
            hr = createEnumFn(_vpDevice, &desc, out _vpEnum);
            if (hr < 0) throw new Exception($"CreateVideoProcessorEnumerator failed hr=0x{hr:X8}");

            var createProcFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, out IntPtr, int>)vpDevVt[4];
            hr = createProcFn(_vpDevice, _vpEnum, 0, out _vpProcessor);
            if (hr < 0) throw new Exception($"CreateVideoProcessor failed hr=0x{hr:X8}");

            var nv12Desc = new TEX2D_DESC
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                Format = 103,
                SampleCount = 1, SampleQuality = 0,
                Usage = 0,
                BindFlags = 0x20,
                CPUAccessFlags = 0, MiscFlags = 0
            };
            var devVt = *(IntPtr**)d3dDevice;
            var createTexFn = (delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, out IntPtr, int>)devVt[5];
            hr = createTexFn(d3dDevice, &nv12Desc, IntPtr.Zero, out _vpNv12Texture);
            if (hr < 0) throw new Exception($"CreateTexture2D NV12 failed hr=0x{hr:X8}");

            var ovDesc = new VP_OUTPUT_VIEW_DESC { ViewDimension = 1, MipSlice = 0 };
            var createOVFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VP_OUTPUT_VIEW_DESC*, out IntPtr, int>)vpDevVt[9];
            hr = createOVFn(_vpDevice, _vpNv12Texture, _vpEnum, &ovDesc, out _vpOutputView);
            if (hr < 0) throw new Exception($"CreateVideoProcessorOutputView failed hr=0x{hr:X8}");

            var vpCtxVt = *(IntPtr**)_vpContext;
            var outCs = new VP_COLOR_SPACE { Value = 0x6 };
            var setOutCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, VP_COLOR_SPACE*, void>)vpCtxVt[15];
            setOutCsFn(_vpContext, _vpProcessor, &outCs);

            var setFrameFmtFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)vpCtxVt[27];
            setFrameFmtFn(_vpContext, _vpProcessor, 0, 0);

            var inCs = new VP_COLOR_SPACE { Value = 0 };
            var setInCsFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, VP_COLOR_SPACE*, void>)vpCtxVt[28];
            setInCsFn(_vpContext, _vpProcessor, 0, &inCs);

            Log.LogInfo($"[FfmpegEnc:{_streamId}] Video Processor ready: BGRA {w}x{h} → NV12");
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
                if (hr < 0) { Log.LogWarning($"[FfmpegEnc:{_streamId}] CreateVideoProcessorInputView failed hr=0x{hr:X8}"); _vpInputView = IntPtr.Zero; return; }
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
            if (bltHr < 0) Log.LogWarning($"[FfmpegEnc:{_streamId}] VideoProcessorBlt failed hr=0x{bltHr:X8}");
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

        private const int Ctx_ClearState = 110;
        private const int Ctx_Flush = 111;

        private void FlushAndClearD3DContext()
        {
            if (_deviceContext == IntPtr.Zero) return;
            try
            {
                var vtable = *(IntPtr**)_deviceContext;
                var clearFn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[Ctx_ClearState];
                clearFn(_deviceContext);
                var flushFn = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[Ctx_Flush];
                flushFn(_deviceContext);
                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: D3D11 ClearState+Flush OK");
            }
            catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: D3D11 flush error: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
            Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose === START ===");
            _initialized = false;
            _disposed = true;

            _initThread?.Join(5000);
            _audioEncodeThread?.Join(2000);
            _encodeThread?.Join(2000);

            var ctxLock = _d3dContextLock;
            bool gotLock = false;
            if (ctxLock != null)
            {
                gotLock = Monitor.TryEnter(ctxLock, 5000);
                if (!gotLock)
                {
                    Log.LogWarning($"[FfmpegEnc:{_streamId}] WARNING: could not acquire D3D lock, skipping FFmpeg cleanup to avoid crash");
                    _fmtCtx = null; _codecCtx = null; _pkt = null; _hwFrame = null;
                    return;
                }
            }
            try
            {
                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: writing trailer");
                if (_fmtCtx != null)
                {
                    try { ffmpeg.av_write_trailer(_fmtCtx); } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: trailer error: {ex.Message}"); }
                }

                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: freeing packets/frames");
                try { if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: pkt free error: {ex.Message}"); _pkt = null; }
                try { if (_audioPkt != null) { var p = _audioPkt; ffmpeg.av_packet_free(&p); _audioPkt = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: audioPkt free error: {ex.Message}"); _audioPkt = null; }
                try { if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: hwFrame free error: {ex.Message}"); _hwFrame = null; }
                try { if (_audioFrame != null) { var f = _audioFrame; ffmpeg.av_frame_free(&f); _audioFrame = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: audioFrame free error: {ex.Message}"); _audioFrame = null; }

                FlushAndClearD3DContext();

                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: freeing VP resources");
                try
                {
                    if (_sharedBridgeMutexEncoder != IntPtr.Zero) { Marshal.Release(_sharedBridgeMutexEncoder); _sharedBridgeMutexEncoder = IntPtr.Zero; }
                    if (_sharedBridgeMutexSource != IntPtr.Zero) { Marshal.Release(_sharedBridgeMutexSource); _sharedBridgeMutexSource = IntPtr.Zero; }
                    if (_sharedBridgeTextureEncoder != IntPtr.Zero) { Marshal.Release(_sharedBridgeTextureEncoder); _sharedBridgeTextureEncoder = IntPtr.Zero; }
                    if (_sharedBridgeTextureSource != IntPtr.Zero) { Marshal.Release(_sharedBridgeTextureSource); _sharedBridgeTextureSource = IntPtr.Zero; }
                    if (_sourceDeviceContext != IntPtr.Zero) { Marshal.Release(_sourceDeviceContext); _sourceDeviceContext = IntPtr.Zero; }
                    if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; _vpInputViewTex = IntPtr.Zero; }
                    if (_vpOutputView != IntPtr.Zero) { Marshal.Release(_vpOutputView); _vpOutputView = IntPtr.Zero; }
                    if (_vpNv12Texture != IntPtr.Zero) { Marshal.Release(_vpNv12Texture); _vpNv12Texture = IntPtr.Zero; }
                    if (_vpProcessor != IntPtr.Zero) { Marshal.Release(_vpProcessor); _vpProcessor = IntPtr.Zero; }
                    if (_vpEnum != IntPtr.Zero) { Marshal.Release(_vpEnum); _vpEnum = IntPtr.Zero; }
                    if (_vpContext != IntPtr.Zero) { Marshal.Release(_vpContext); _vpContext = IntPtr.Zero; }
                    if (_vpDevice != IntPtr.Zero) { Marshal.Release(_vpDevice); _vpDevice = IntPtr.Zero; }
                }
                catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: VP cleanup error: {ex.Message}"); }

                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: freeing codec contexts");
                try { if (_audioCodecCtx != null) { var c = _audioCodecCtx; ffmpeg.avcodec_free_context(&c); _audioCodecCtx = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: audioCodec free error: {ex.Message}"); _audioCodecCtx = null; }
                try { if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: codec free error: {ex.Message}"); _codecCtx = null; }

                Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: freeing hw contexts");
                try { if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: hwFrames free error: {ex.Message}"); _hwFramesCtx = null; }
                try { if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; } } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: hwDevice free error: {ex.Message}"); _hwDeviceCtx = null; }
            }
            finally { if (gotLock) Monitor.Exit(ctxLock); }
            _audioBridge = null;

            Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose: freeing format context");
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
            catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: fmtCtx free error: {ex.Message}"); _fmtCtx = null; }

            if (_selfHandle.IsAllocated) _selfHandle.Free();

            try { _dataAvailable.Set(); } catch { }
            try { _dataAvailable.Dispose(); } catch (Exception ex) { Log.LogWarning($"[FfmpegEnc:{_streamId}] Dispose: dataAvailable dispose error: {ex.Message}"); }
            try { _encodeEvent.Set(); } catch { }
            try { _encodeEvent.Dispose(); } catch { }

            Log.LogInfo($"[FfmpegEnc:{_streamId}] Dispose === DONE === {_totalFrames} total frames");
        }
    }
}

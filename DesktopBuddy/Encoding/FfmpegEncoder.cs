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
    private readonly ManualResetEventSlim _dataAvailable = new(false);

    private uint _width, _height;
    private int _totalFrames;
    private readonly uint _fps = 30;

    private const int RING_SIZE = 4 * 1024 * 1024;
    private const int AVIO_BUFFER_SIZE = 65536;
    private const byte MPEGTS_SYNC = 0x47;
    private const int MPEGTS_PACKET_SIZE = 188;

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
    private IntPtr _vpDevice, _vpContext, _vpEnum, _vpProcessor;
    private IntPtr _vpOutputView, _vpNv12Texture;
    private IntPtr _vpInputView, _vpInputViewTex;
    private long _startTicks;
    private long _lastVideoPts = -1;
    private long _lastKeyframeRingPos;

    private avio_alloc_context_write_packet _writeCallbackDelegate;
    private GCHandle _selfHandle;

    private volatile bool _rtspBroken;
    private IntPtr _keepAliveTexture;
    private uint _keepAliveW, _keepAliveH;
    private long _lastEncodeTicks;
    private const int KEEP_ALIVE_FPS = 4;
    private const int KEEP_ALIVE_INTERVAL_MS = 1000 / KEEP_ALIVE_FPS;
    private const double KEEP_ALIVE_IDLE_SECONDS = KEEP_ALIVE_INTERVAL_MS / 1000.0;

    public bool IsInitialized => _initialized;
    public bool IsRunning => _initialized;

    public void Stop()
    {
        _disposed = true;
        _initialized = false;
    }

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
        var rmlLibsDir = Path.GetFullPath(Path.Combine(modDir, "..", "rml_libs"));
        if (File.Exists(Path.Combine(rmlLibsDir, "avcodec-62.dll")))
            return rmlLibsDir;
        return null;
    }

    public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
    {
        if (_dataAvailable.IsSet)
        {
            _dataAvailable.Reset();
            return System.Threading.Tasks.Task.CompletedTask;
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
                if (readPos == 0 && _ringWritePos < RING_SIZE)
                {
                    aligned = true;
                }
                else
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

            if (_width < 128 || _height < 128)
            {
                Log.Msg($"[FfmpegEnc:{_streamId}] Window too small for encoding: {_width}x{_height} (min 128x128)");
                _initFailed = true; return false;
            }

            Log.Msg($"[FfmpegEnc:{_streamId}] Initializing: {_width}x{_height} @ {_fps}fps");

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
                _codecCtx->framerate = new AVRational { num = (int)_fps, den = 1 };
                long bitrate = Math.Max(1, DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.Bitrate) ?? 10) * 1_000_000L;
                _codecCtx->max_b_frames = 0;
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY | ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                bool isAmf = name.Contains("amf");

                if (isAmf)
                {
                    _codecCtx->gop_size = (int)_fps / 2;
                    _codecCtx->keyint_min = (int)_fps / 2;
                    _codecCtx->bit_rate = bitrate;
                    _codecCtx->rc_buffer_size = (int)bitrate;
                }
                else
                {
                    _codecCtx->gop_size = (int)_fps / 3;
                    _codecCtx->bit_rate = bitrate;
                    _codecCtx->rc_max_rate = (long)(bitrate * 1.2);
                    _codecCtx->rc_buffer_size = (int)bitrate;
                }

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
                    ffmpeg.av_dict_set(&opts, "usage", "lowlatency_high_quality", 0);
                    ffmpeg.av_dict_set(&opts, "rc", "cbr", 0);
                    ffmpeg.av_dict_set(&opts, "header_insertion_mode", "idr", 0);
                    ffmpeg.av_dict_set(&opts, "log_to_dbg", "1", 0);
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

            Log.Msg($"[FfmpegEnc:{_streamId}] Codec opened: {codecName}");

            _hwFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            _audioCapture = audioCapture;
            _audioReadPos = 0;
            _audioSamplesEncoded = 0;

            SetupMuxer();

            var hwDevCtxData = (AVHWDeviceContext*)_hwDeviceCtx->data;
            var d3d11DevCtxData = (AVD3D11VADeviceContext*)hwDevCtxData->hwctx;
            _deviceContext = (IntPtr)d3d11DevCtxData->device_context;

            if (_needsVideoProcessor)
                SetupVideoProcessor(d3dDevice, _width, _height);

            if (_rtspUrl == null)
            {
                _ringBuffer = new byte[RING_SIZE];
                _ringWritePos = 0;
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
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

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
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;

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

        _dataAvailable.Set();

        return buf_size;
    }

    public void QueueFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_disposed || _initFailed || !_initialized) return;
        if ((width & ~1u) != _width || (height & ~1u) != _height)
        {
            if (_totalFrames == 0)
                Log.Msg($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch init={_width}x{_height} frame={width}x{height}");
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

            _encodeEvent.WaitOne(KEEP_ALIVE_INTERVAL_MS);
            if (_disposed) break;

            var tex = Interlocked.Exchange(ref _pendingTexture, IntPtr.Zero);
            var w = _pendingWidth;
            var h = _pendingHeight;

            if (tex == IntPtr.Zero)
            {
                if (_keepAliveTexture != IntPtr.Zero && _lastEncodeTicks != 0)
                {
                    double idleSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _lastEncodeTicks) / System.Diagnostics.Stopwatch.Frequency;
                    if (idleSec >= KEEP_ALIVE_IDLE_SECONDS)
                    {
                        Marshal.AddRef(_keepAliveTexture);
                        tex = _keepAliveTexture;
                        w = _keepAliveW;
                        h = _keepAliveH;
                    }
                }
                if (tex == IntPtr.Zero) continue;
            }

            try
            {
                EncodeFrameInternalLocked(tex, w, h);
                _lastEncodeTicks = System.Diagnostics.Stopwatch.GetTimestamp();

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

    private void EncodeFrameInternalLocked(IntPtr srcTexture, uint width, uint height)
    {
        int ret;

        {
            if (_disposed) return;

            using (DesktopBuddyMod.Perf.Time("ffmpeg_get_buffer"))
            {
                ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }
            }

            if (_needsVideoProcessor)
            {
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
                using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
                {
                    IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
                    int dstIndex = (int)_hwFrame->data[1];
                    CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
                }
            }

            double elapsedSec = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / System.Diagnostics.Stopwatch.Frequency;
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

                    lock (_muxerLock)
                    {
                        if (_rtspBroken) break;
                        if (isKey && _rtspUrl == null)
                        {
                            ffmpeg.avio_flush(_fmtCtx->pb);
                            Interlocked.Exchange(ref _lastKeyframeRingPos, _ringWritePos);
                        }
                        ret = ffmpeg.av_write_frame(_fmtCtx, _pkt);
                        if (ret < 0)
                        {
                            Log.Msg($"[FfmpegEnc:{_streamId}] av_write_frame (video) failed: {FfmpegError(ret)}");
                            if (_rtspUrl != null) _rtspBroken = true;
                        }
                    }

                    ffmpeg.av_packet_unref(_pkt);
                }
            }

            if (_rtspUrl == null)
                lock (_muxerLock) ffmpeg.avio_flush(_fmtCtx->pb);
        }

        _totalFrames++;
        if (_totalFrames <= 5 || _totalFrames % 300 == 0)
            Log.Msg($"[FfmpegEnc:{_streamId}] Frame #{_totalFrames} ({width}x{height}), ringPos={_ringWritePos}");
    }

    private void AudioEncodeLoop()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread started");
        while (!_disposed)
        {
            _encodeEvent.WaitOne(33);
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
                        ffmpeg.av_write_frame(_fmtCtx, _audioPkt);
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

        Log.Msg($"[FfmpegEnc:{_streamId}] Video Processor ready: BGRA {w}x{h} → NV12");
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
                Log.Msg($"[FfmpegEnc:{_streamId}] WARNING: could not acquire D3D lock, skipping FFmpeg cleanup to avoid crash");
                _fmtCtx = null; _codecCtx = null; _pkt = null; _hwFrame = null;
                return;
            }
        }
        try
        {
            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: writing trailer");
            if (_fmtCtx != null)
            {
                try { ffmpeg.av_write_trailer(_fmtCtx); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: trailer error: {ex.Message}"); }
            }

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing packets/frames");
            try { if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: pkt free error: {ex.Message}"); _pkt = null; }
            try { if (_audioPkt != null) { var p = _audioPkt; ffmpeg.av_packet_free(&p); _audioPkt = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioPkt free error: {ex.Message}"); _audioPkt = null; }
            try { if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrame free error: {ex.Message}"); _hwFrame = null; }
            try { if (_audioFrame != null) { var f = _audioFrame; ffmpeg.av_frame_free(&f); _audioFrame = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioFrame free error: {ex.Message}"); _audioFrame = null; }

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing VP resources");
            try
            {
                if (_vpInputView != IntPtr.Zero) { Marshal.Release(_vpInputView); _vpInputView = IntPtr.Zero; _vpInputViewTex = IntPtr.Zero; }
                if (_vpOutputView != IntPtr.Zero) { Marshal.Release(_vpOutputView); _vpOutputView = IntPtr.Zero; }
                if (_vpNv12Texture != IntPtr.Zero) { Marshal.Release(_vpNv12Texture); _vpNv12Texture = IntPtr.Zero; }
                if (_vpProcessor != IntPtr.Zero) { Marshal.Release(_vpProcessor); _vpProcessor = IntPtr.Zero; }
                if (_vpEnum != IntPtr.Zero) { Marshal.Release(_vpEnum); _vpEnum = IntPtr.Zero; }
                if (_vpContext != IntPtr.Zero) { Marshal.Release(_vpContext); _vpContext = IntPtr.Zero; }
                if (_vpDevice != IntPtr.Zero) { Marshal.Release(_vpDevice); _vpDevice = IntPtr.Zero; }
            }
            catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: VP cleanup error: {ex.Message}"); }

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing codec contexts");
            try { if (_audioCodecCtx != null) { var c = _audioCodecCtx; ffmpeg.avcodec_free_context(&c); _audioCodecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: audioCodec free error: {ex.Message}"); _audioCodecCtx = null; }
            try { if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: codec free error: {ex.Message}"); _codecCtx = null; }

            Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing hw contexts");
            try { if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwFrames free error: {ex.Message}"); _hwFramesCtx = null; }
            try { if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; } } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: hwDevice free error: {ex.Message}"); _hwDeviceCtx = null; }
        }
        finally { if (gotLock) Monitor.Exit(ctxLock); }
        _audioCapture = null;
        if (_keepAliveTexture != IntPtr.Zero) { try { Marshal.Release(_keepAliveTexture); } catch { } _keepAliveTexture = IntPtr.Zero; }

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: freeing format context");
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

        if (_selfHandle.IsAllocated) _selfHandle.Free();

        try { _dataAvailable.Set(); } catch { }
        try { _dataAvailable.Dispose(); } catch (Exception ex) { Log.Msg($"[FfmpegEnc:{_streamId}] Dispose: dataAvailable dispose error: {ex.Message}"); }
        try { _encodeEvent.Set(); } catch { }
        try { _encodeEvent.Dispose(); } catch { }

        Log.Msg($"[FfmpegEnc:{_streamId}] Dispose === DONE === {_totalFrames} total frames");
    }
}

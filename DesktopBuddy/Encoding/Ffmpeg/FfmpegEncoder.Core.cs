using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
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
    private long _lastAudioPts = -1;
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
    private IntPtr _vpInputView, _vpInputTexture;
    private long _startTicks;
    private long _lastVideoPts = -1;
    private long _lastKeyframeRingPos = -1;
    private long _readerOverrunEvents;
    private long _readerOverrunMaxBacklogBytes;
    private long _readerLastOverrunLogTicks;

    private avio_alloc_context_write_packet _writeCallbackDelegate;
    private GCHandle _selfHandle;

    private volatile bool _rtspBroken;
    private IntPtr _keepAliveTexture;
    private uint _keepAliveW, _keepAliveH;
    private long _lastEncodeTicks;
    private long _nextKeepAliveTicks;
    private int _keepAliveFramesEncoded;
    private int _keepAliveFps = 60;
    private long _readerLiveCatchupEvents;
    private long _readerLastCatchupLogTicks;
    private long _audioLiveCatchupEvents;
    private long _audioLastCatchupLogTicks;

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

    private static bool _ffmpegPathSet;
    private static bool _hardwareEncoderPrewarmed;

    public FfmpegEncoder(int streamId, string rtspUrl = null)
    {
        _streamId = streamId;
        _rtspUrl = rtspUrl;
    }


}

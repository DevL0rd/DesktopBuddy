using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuddy;

internal sealed class LinuxGstMpegTsStreamSource : ILiveStreamSource
{
    private const int RingSize = 16 * 1024 * 1024;
    private const int ReadChunkSize = 64 * 1024;

    private readonly int _streamId;
    private readonly object _ringLock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);
    private byte[] _ringBuffer;
    private long _ringWritePos;
    private long _lastKeyframeRingPos = -1;
    private Process _process;
    private Thread _stdoutThread;
    private Thread _stderrThread;
    private volatile bool _running;
    private int _disposed;
    private uint _nodeId;

    public bool IsRunning => _running;
    public long CurrentWritePosition { get { lock (_ringLock) return _ringWritePos; } }
    public string ReadableStreamState
    {
        get
        {
            lock (_ringLock)
                return $"gst running={_running} node={_nodeId} ringReady={_ringBuffer != null} keyframePos={_lastKeyframeRingPos} writePos={_ringWritePos}";
        }
    }

    public LinuxGstMpegTsStreamSource(int streamId)
    {
        _streamId = streamId;
    }

    public bool Start(uint pipeWireNodeId, int width, int height)
    {
        if (_running) return true;
        if (pipeWireNodeId == 0)
        {
            Log.Msg($"[LinuxGst:{_streamId}] Cannot start: missing PipeWire node id");
            return false;
        }

        if (!IsGstLaunchAvailable())
            return false;

        _nodeId = pipeWireNodeId;
        _ringBuffer = new byte[RingSize];
        _ringWritePos = 0;
        _lastKeyframeRingPos = -1;

        var args = BuildGstArgs(pipeWireNodeId, width, height);
        Log.Msg($"[LinuxGst:{_streamId}] Starting gst-launch-1.0 {args}");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gst-launch-1.0",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _process.Exited += (_, _) =>
        {
            _running = false;
            Log.Msg($"[LinuxGst:{_streamId}] gst-launch exited code={SafeExitCode()} state={ReadableStreamState}");
        };

        try
        {
            if (!_process.Start())
                return false;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxGst:{_streamId}] Failed to start gst-launch: {ex.Message}");
            return false;
        }

        _running = true;
        _stdoutThread = new Thread(ReadStdoutLoop) { Name = $"LinuxGst:{_streamId}:stdout", IsBackground = true };
        _stderrThread = new Thread(ReadStderrLoop) { Name = $"LinuxGst:{_streamId}:stderr", IsBackground = true };
        _stdoutThread.Start();
        _stderrThread.Start();
        return true;
    }

    private static bool IsGstLaunchAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gst-launch-1.0",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process == null) return false;
            process.WaitForExit(1500);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxGst] gst-launch-1.0 unavailable: {ex.Message}");
            return false;
        }
    }

    private static string BuildGstArgs(uint nodeId, int width, int height)
    {
        int fps = Math.Clamp(DesktopBuddyMod.RuntimeStreamFps, 1, 240);
        int bitrateKbps = Math.Max(1, DesktopBuddyMod.RuntimeBitrateMbps) * 1000;
        int keyMax = Math.Clamp(fps, 1, 240);
        string scaleCaps = width > 0 && height > 0
            ? $"video/x-raw,width={width},height={height},framerate={fps}/1"
            : $"video/x-raw,framerate={fps}/1";

        return
            $"-q pipewiresrc path={nodeId} do-timestamp=true " +
            $"! queue leaky=downstream max-size-buffers=2 max-size-bytes=0 max-size-time=0 " +
            $"! videoconvert n-threads=2 " +
            $"! videoscale method=0 " +
            $"! {scaleCaps} " +
            $"! x264enc tune=zerolatency speed-preset=ultrafast key-int-max={keyMax} bframes=0 bitrate={bitrateKbps} byte-stream=true " +
            $"! h264parse config-interval=1 " +
            $"! mpegtsmux alignment=7 " +
            $"! fdsink fd=1 sync=false";
    }

    private int SafeExitCode()
    {
        try { return _process?.ExitCode ?? int.MinValue; }
        catch { return int.MinValue; }
    }

    private void ReadStdoutLoop()
    {
        var buffer = new byte[ReadChunkSize];
        try
        {
            Stream output = _process.StandardOutput.BaseStream;
            while (_running && !_process.HasExited)
            {
                int read = output.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                WriteRing(buffer, read);
            }
        }
        catch (Exception ex)
        {
            if (_running)
                Log.Msg($"[LinuxGst:{_streamId}] stdout read error: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private void ReadStderrLoop()
    {
        try
        {
            string line;
            while ((line = _process.StandardError.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Log.Msg($"[LinuxGst:{_streamId}] {line}");
            }
        }
        catch (Exception ex)
        {
            if (_running)
                Log.Msg($"[LinuxGst:{_streamId}] stderr read error: {ex.Message}");
        }
    }

    private void WriteRing(byte[] source, int count)
    {
        lock (_ringLock)
        {
            int offset = 0;
            while (offset < count)
            {
                int ringOffset = (int)(_ringWritePos % RingSize);
                int chunk = Math.Min(count - offset, RingSize - ringOffset);
                Buffer.BlockCopy(source, offset, _ringBuffer, ringOffset, chunk);
                offset += chunk;
                _ringWritePos += chunk;
            }

            _lastKeyframeRingPos = _ringWritePos;
        }

        try { _dataAvailable.Release(); }
        catch { }
    }

    public bool HasReadableVideoKeyframeAtOrAfter(long minimumKeyframePos)
    {
        lock (_ringLock)
            return _running && _ringBuffer != null && _lastKeyframeRingPos >= minimumKeyframePos;
    }

    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool keyframeAligned)
    {
        keyframeAligned = aligned;
        lock (_ringLock)
        {
            long writePos = _ringWritePos;
            if (_ringBuffer == null || writePos <= readPos)
                return 0;

            long oldest = Math.Max(0, writePos - RingSize);
            if (!aligned || readPos < oldest || readPos < minimumKeyframePos)
            {
                readPos = Math.Max(oldest, minimumKeyframePos);
                readPos += (188 - (readPos % 188)) % 188;
                aligned = true;
                keyframeAligned = true;
            }

            long available = writePos - readPos;
            if (available <= 0) return 0;

            int toRead = (int)Math.Min(buffer.Length, available);
            int ringOffset = (int)(readPos % RingSize);
            int first = Math.Min(toRead, RingSize - ringOffset);
            Buffer.BlockCopy(_ringBuffer, ringOffset, buffer, 0, first);
            if (first < toRead)
                Buffer.BlockCopy(_ringBuffer, 0, buffer, first, toRead - first);
            readPos += toRead;
            return toRead;
        }
    }

    public Task WaitForDataAsync(int milliseconds)
    {
        return _dataAvailable.WaitAsync(milliseconds);
    }

    public string GetReaderDiagnostics(long readPos, bool aligned)
    {
        lock (_ringLock)
        {
            long writePos = _ringWritePos;
            return $"readPos={readPos} writePos={writePos} backlog={writePos - readPos} aligned={aligned} latestKeyframe={_lastKeyframeRingPos} ringSize={RingSize}";
        }
    }

    public void Stop()
    {
        _running = false;
        try { _dataAvailable.Release(); } catch { }
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxGst:{_streamId}] Kill error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try { _stdoutThread?.Join(1000); } catch { }
        try { _stderrThread?.Join(1000); } catch { }
        try { _process?.Dispose(); } catch { }
        _dataAvailable.Dispose();
    }
}

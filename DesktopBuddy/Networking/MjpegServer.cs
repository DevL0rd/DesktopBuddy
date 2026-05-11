using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed class MjpegServer : IDisposable
{
    private const int StreamBufferSize = 64 * 1024;

    private HttpListener _listener;
    private volatile bool _running;
    private readonly int _port;
    private long _nextConnectionId;

    private readonly ConcurrentDictionary<int, StreamEntry> _streams = new();
    public int Port => _port;

    public MjpegServer(int port = 48080)
    {
        _port = port;
        _listener = new HttpListener();
        _running = true;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://+:{_port}/");
        _listener.Start();
        Log.Msg($"[MjpegServer] Listening on http://+:{_port}/");
        _ = ListenLoopAsync();
    }

    public FfmpegEncoder CreateEncoder(int streamId)
    {
        var encoder = new FfmpegEncoder(streamId);
        _streams[streamId] = new StreamEntry(encoder);
        Log.Msg($"[MjpegServer] Created encoder for stream {streamId}");
        return encoder;
    }

    public void StopEncoder(int streamId)
    {
        Log.MsgImmediate($"[CleanupTrace] MjpegServer.StopEncoder ENTER stream={streamId}");
        if (!_streams.TryRemove(streamId, out var entry)) return;
        Log.MsgImmediate($"[CleanupTrace] MjpegServer.StopEncoder removed entry stream={streamId}; encoder.Dispose START");
        entry.Encoder.Dispose();
        Log.MsgImmediate($"[CleanupTrace] MjpegServer.StopEncoder EXIT stream={streamId}");
    }

    private async Task ListenLoopAsync()
    {
        Log.Msg("[MjpegServer] Async listen loop started");
        while (_running)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleRequestAsync(ctx);
            }
            catch (HttpListenerException ex) { Log.Msg($"[MjpegServer] Listener stopped: {ex.Message}"); break; }
            catch (ObjectDisposedException) { Log.Msg("[MjpegServer] Listener disposed, stopping"); break; }
            catch (Exception ex)
            {
                Log.Msg($"[MjpegServer] Listen error: {ex.Message}");
            }
        }
        Log.Msg("[MjpegServer] Async listen loop ended");
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/stream/"))
                await ServeStreamAsync(ctx, path).ConfigureAwait(false);
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[MjpegServer] Request error: {ex.Message}");
            try { ctx.Response.Close(); } catch (Exception closeEx) { Log.Msg($"[MjpegServer] Response close error: {closeEx.Message}"); }
        }
    }

    private async Task ServeStreamAsync(HttpListenerContext ctx, string urlPath)
    {
        var parts = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

        if (!int.TryParse(parts[1], out int streamId) || !_streams.TryGetValue(streamId, out var entry))
        {
            Log.Msg($"[MjpegServer] Stream {streamId} not found");
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var encoder = entry.Encoder;
        int waitCount = 0;
        while (!encoder.IsRunning && waitCount < 50)
        {
            await Task.Delay(100).ConfigureAwait(false);
            waitCount++;
        }
        if (!encoder.IsRunning)
        {
            Log.Msg($"[MjpegServer] Stream {streamId} encoder not ready after {waitCount * 100}ms");
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        long minimumKeyframePos = encoder.CurrentWritePosition;

        int keyframeWaitCount = 0;
        while (encoder.IsRunning && !encoder.HasReadableVideoKeyframeAtOrAfter(minimumKeyframePos) && keyframeWaitCount < 300)
        {
            await Task.Delay(10).ConfigureAwait(false);
            keyframeWaitCount++;
        }
        if (!encoder.HasReadableVideoKeyframeAtOrAfter(minimumKeyframePos))
        {
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} has no fresh readable keyframe after {keyframeWaitCount * 10}ms from minPos={minimumKeyframePos}: {encoder.ReadableStreamState}");
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        if (keyframeWaitCount > 0)
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} fresh keyframe readable after {keyframeWaitCount * 10}ms from minPos={minimumKeyframePos}: {encoder.ReadableStreamState}");

        int clientCount = entry.AddClient();
        Log.Msg($"[MjpegServer] Stream client added stream={streamId} conn={connectionId} clients={clientCount}");
        Log.Msg($"[MjpegServer] Serving stream {streamId} conn={connectionId} to {ctx.Request.RemoteEndPoint} local={ctx.Request.LocalEndPoint} {DescribeRequest(ctx.Request)}");
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.SendChunked = true;
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        long totalBytes = 0;
        long lastSummaryTicks = Stopwatch.GetTimestamp();
        long summaryStartTicks = lastSummaryTicks;
        long writeOps = 0;
        long slowWrites = 0;
        long maxWriteTicks = 0;
        long totalWriteTicks = 0;
        int maxWriteBytes = 0;
        long readPos = 0;
        bool aligned = false;
        long readBytes = 0;
        long chunks = 0;
        long keyframeChunks = 0;
        long zeroReads = 0;
        long waits = 0;
        long readerOverruns = 0;
        var buffer = new byte[StreamBufferSize];
        try
        {
            while (_running && encoder.IsRunning)
            {
                int read = encoder.ReadStream(buffer, ref readPos, ref aligned, minimumKeyframePos, out bool startsAtKeyframe);
                if (read == 0)
                {
                    zeroReads++;
                    await encoder.WaitForDataAsync(5).ConfigureAwait(false);
                    waits++;
                    continue;
                }

                if (read < 0)
                {
                    readerOverruns++;
                    Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} fell behind the ring; closing response for clean player reconnect. state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
                    break;
                }

                long writeStart = Stopwatch.GetTimestamp();
                long writeTicks;
                using (DesktopBuddyMod.Perf.Time("stream_http_write"))
                    await ctx.Response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                writeTicks = Stopwatch.GetTimestamp() - writeStart;

                writeOps++;
                totalWriteTicks += writeTicks;
                if (writeTicks > maxWriteTicks)
                {
                    maxWriteTicks = writeTicks;
                    maxWriteBytes = read;
                }

                totalBytes += read;
                readBytes += read;
                chunks++;
                if (startsAtKeyframe) keyframeChunks++;
                if (TicksToMs(writeTicks) >= 25.0)
                    slowWrites++;

                long nowTicks = Stopwatch.GetTimestamp();
                if (TicksToMs(nowTicks - lastSummaryTicks) >= 5000.0)
                {
                    double summaryMs = TicksToMs(nowTicks - summaryStartTicks);
                    double streamMbps = summaryMs > 0 ? readBytes * 8.0 / summaryMs / 1000.0 : 0.0;
                    Log.Msg($"[MjpegServer] Stream client summary stream={streamId} conn={connectionId} sent={totalBytes} readBytes={readBytes} streamMbps={streamMbps:F2} chunks={chunks} keyframeChunks={keyframeChunks} zeroReads={zeroReads} waits={waits} readerOverruns={readerOverruns} writeOps={writeOps} slowWrites={slowWrites} maxWrite={TicksToMs(maxWriteTicks):F2}ms/{maxWriteBytes}B avgWrite={(writeOps > 0 ? TicksToMs(totalWriteTicks) / writeOps : 0):F2}ms state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
                    lastSummaryTicks = nowTicks;
                    summaryStartTicks = nowTicks;
                    readBytes = 0;
                    chunks = 0;
                    keyframeChunks = 0;
                    zeroReads = 0;
                    waits = 0;
                    readerOverruns = 0;
                    writeOps = 0;
                    slowWrites = 0;
                    maxWriteTicks = 0;
                    totalWriteTicks = 0;
                    maxWriteBytes = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} error: {ex.GetType().Name}: {ex.Message} sent={totalBytes} state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
        }
        finally
        {
            clientCount = entry.RemoveClient();
            Log.Msg($"[MjpegServer] Stream client removed stream={streamId} conn={connectionId} clients={clientCount}");
            try { ctx.Response.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Stream {streamId} response close error: {ex.Message}"); }
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} ended, sent {totalBytes} bytes state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
        }
    }

    private static double TicksToMs(long ticks)
    {
        return (double)ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string DescribeRequest(HttpListenerRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("method=").Append(request.HttpMethod);
        sb.Append(" proto=").Append(request.ProtocolVersion);
        sb.Append(" ua=\"").Append(request.UserAgent ?? "").Append('"');
        sb.Append(" accept=\"").Append(request.Headers["Accept"] ?? "").Append('"');
        sb.Append(" range=\"").Append(request.Headers["Range"] ?? "").Append('"');
        sb.Append(" forwarded=\"").Append(request.Headers["X-Forwarded-For"] ?? "").Append('"');
        sb.Append(" cfRay=\"").Append(request.Headers["Cf-Ray"] ?? request.Headers["CF-Ray"] ?? "").Append('"');
        return sb.ToString();
    }

    public void Dispose()
    {
        Log.MsgImmediate($"[CleanupTrace] MjpegServer.Dispose ENTER streams={_streams.Count}");
        _running = false;
        foreach (var kvp in _streams)
        {
            Log.MsgImmediate($"[CleanupTrace] MjpegServer.Dispose encoder.Dispose START stream={kvp.Key}");
            kvp.Value.Encoder.Dispose();
            Log.MsgImmediate($"[CleanupTrace] MjpegServer.Dispose encoder.Dispose DONE stream={kvp.Key}");
        }
        _streams.Clear();
        Log.MsgImmediate("[CleanupTrace] MjpegServer.Dispose listener.Stop START");
        try { _listener.Stop(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener stop error: {ex.Message}"); }
        Log.MsgImmediate("[CleanupTrace] MjpegServer.Dispose listener.Close START");
        try { _listener.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener close error: {ex.Message}"); }
        Log.MsgImmediate("[CleanupTrace] MjpegServer.Dispose EXIT");
    }

    private sealed class StreamEntry
    {
        public readonly FfmpegEncoder Encoder;
        private int _clientCount;

        public StreamEntry(FfmpegEncoder encoder)
        {
            Encoder = encoder;
        }

        public int AddClient()
        {
            return Interlocked.Increment(ref _clientCount);
        }

        public int RemoveClient()
        {
            int count = Interlocked.Decrement(ref _clientCount);
            return Math.Max(0, count);
        }
    }
}

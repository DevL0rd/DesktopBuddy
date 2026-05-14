using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed class BuiltInStreamServer : IDisposable
{
    private const int StreamBufferSize = 64 * 1024;

    private HttpListener _listener;
    private volatile bool _running;
    private readonly int _port;
    private long _nextConnectionId;

    private readonly ConcurrentDictionary<int, StreamEntry> _streams = new();
    public int Port => _port;

    public BuiltInStreamServer(int port = 48080)
    {
        _port = port;
        _listener = new HttpListener();
        _running = true;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://+:{_port}/");
        _listener.Start();
        Log.Msg($"[BuiltInStreamServer] Listening on http://+:{_port}/");
        _ = ListenLoopAsync();
    }

    public FfmpegEncoder CreateEncoder(int streamId)
    {
        var encoder = new FfmpegEncoder(streamId);
        _streams[streamId] = new StreamEntry(encoder);
        Log.Msg($"[BuiltInStreamServer] Created encoder for stream {streamId}");
        return encoder;
    }

    public void StopEncoder(int streamId)
    {
        Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.StopEncoder ENTER stream={streamId}");
        if (!_streams.TryRemove(streamId, out var entry)) return;
        Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.StopEncoder removed entry stream={streamId}; encoder.Dispose START");
        entry.Encoder.Dispose();
        Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.StopEncoder EXIT stream={streamId}");
    }

    private async Task ListenLoopAsync()
    {
        Log.Msg("[BuiltInStreamServer] Async listen loop started");
        while (_running)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleRequestAsync(ctx);
            }
            catch (HttpListenerException ex) { Log.Msg($"[BuiltInStreamServer] Listener stopped: {ex.Message}"); break; }
            catch (ObjectDisposedException) { Log.Msg("[BuiltInStreamServer] Listener disposed, stopping"); break; }
            catch (Exception ex)
            {
                Log.Msg($"[BuiltInStreamServer] Listen error: {ex.Message}");
            }
        }
        Log.Msg("[BuiltInStreamServer] Async listen loop ended");
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
            Log.Msg($"[BuiltInStreamServer] Request error: {ex.Message}");
            try { ctx.Response.Close(); } catch (Exception closeEx) { Log.Msg($"[BuiltInStreamServer] Response close error: {closeEx.Message}"); }
        }
    }

    private async Task ServeStreamAsync(HttpListenerContext ctx, string urlPath)
    {
        var parts = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

        if (!int.TryParse(parts[1], out int streamId) || !_streams.TryGetValue(streamId, out var entry))
        {
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} not found");
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
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} encoder not ready after {waitCount * 100}ms");
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
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} conn={connectionId} has no fresh readable keyframe after {keyframeWaitCount * 10}ms from minPos={minimumKeyframePos}: {encoder.ReadableStreamState}");
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        if (keyframeWaitCount > 0)
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} conn={connectionId} fresh keyframe readable after {keyframeWaitCount * 10}ms from minPos={minimumKeyframePos}: {encoder.ReadableStreamState}");

        int clientCount = entry.AddClient();
        Log.Msg($"[BuiltInStreamServer] Stream client added stream={streamId} conn={connectionId} clients={clientCount}");
        Log.Msg($"[BuiltInStreamServer] Serving stream {streamId} conn={connectionId} to {ctx.Request.RemoteEndPoint} local={ctx.Request.LocalEndPoint} {DescribeRequest(ctx.Request)}");
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.SendChunked = true;
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        long totalBytes = 0;
        long readPos = 0;
        bool aligned = false;
        var buffer = new byte[StreamBufferSize];
        try
        {
            while (_running && encoder.IsRunning)
            {
                int read = encoder.ReadStream(buffer, ref readPos, ref aligned, minimumKeyframePos, out _);
                if (read == 0)
                {
                    await encoder.WaitForDataAsync(5).ConfigureAwait(false);
                    continue;
                }

                if (read < 0)
                {
                    Log.Msg($"[BuiltInStreamServer] Stream {streamId} conn={connectionId} fell behind the ring; closing response for clean player reconnect. state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
                    break;
                }

                await ctx.Response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                totalBytes += read;
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} conn={connectionId} error: {ex.GetType().Name}: {ex.Message} sent={totalBytes} state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
        }
        finally
        {
            clientCount = entry.RemoveClient();
            Log.Msg($"[BuiltInStreamServer] Stream client removed stream={streamId} conn={connectionId} clients={clientCount}");
            try { ctx.Response.Close(); } catch (Exception ex) { Log.Msg($"[BuiltInStreamServer] Stream {streamId} response close error: {ex.Message}"); }
            Log.Msg($"[BuiltInStreamServer] Stream {streamId} conn={connectionId} ended, sent {totalBytes} bytes state=({encoder.GetReaderDiagnostics(readPos, aligned)})");
        }
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
        Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.Dispose ENTER streams={_streams.Count}");
        _running = false;
        foreach (var kvp in _streams)
        {
            Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.Dispose encoder.Dispose START stream={kvp.Key}");
            kvp.Value.Encoder.Dispose();
            Log.MsgImmediate($"[CleanupTrace] BuiltInStreamServer.Dispose encoder.Dispose DONE stream={kvp.Key}");
        }
        _streams.Clear();
        Log.MsgImmediate("[CleanupTrace] BuiltInStreamServer.Dispose listener.Stop START");
        try { _listener.Stop(); } catch (Exception ex) { Log.Msg($"[BuiltInStreamServer] Listener stop error: {ex.Message}"); }
        Log.MsgImmediate("[CleanupTrace] BuiltInStreamServer.Dispose listener.Close START");
        try { _listener.Close(); } catch (Exception ex) { Log.Msg($"[BuiltInStreamServer] Listener close error: {ex.Message}"); }
        Log.MsgImmediate("[CleanupTrace] BuiltInStreamServer.Dispose EXIT");
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

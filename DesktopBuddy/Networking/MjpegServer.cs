using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        var broadcaster = new StreamBroadcaster(streamId, encoder, () => _running);
        _streams[streamId] = new StreamEntry(encoder, broadcaster);
        Log.Msg($"[MjpegServer] Created encoder for stream {streamId}");
        return encoder;
    }

    public void StopEncoder(int streamId)
    {
        if (!_streams.TryRemove(streamId, out var entry)) return;
        entry.Broadcaster.Dispose();
        entry.Encoder.Dispose();
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

        int keyframeWaitCount = 0;
        while (encoder.IsRunning && !encoder.HasReadableVideoKeyframe && keyframeWaitCount < 300)
        {
            await Task.Delay(10).ConfigureAwait(false);
            keyframeWaitCount++;
        }
        if (!encoder.HasReadableVideoKeyframe)
        {
            Log.Msg($"[MjpegServer] Stream {streamId} has no readable keyframe after {keyframeWaitCount * 10}ms: {encoder.ReadableStreamState}");
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        if (keyframeWaitCount > 0)
            Log.Msg($"[MjpegServer] Stream {streamId} readable after {keyframeWaitCount * 10}ms: {encoder.ReadableStreamState}");

        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        var client = entry.Broadcaster.AddClient(connectionId);
        Log.Msg($"[MjpegServer] Serving stream {streamId} conn={connectionId} to {ctx.Request.RemoteEndPoint} local={ctx.Request.LocalEndPoint} {DescribeRequest(ctx.Request)}");
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.SendChunked = true;
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        long totalBytes = 0;
        long lastSummaryTicks = Stopwatch.GetTimestamp();
        long writeOps = 0;
        long slowWrites = 0;
        long maxWriteTicks = 0;
        long totalWriteTicks = 0;
        int maxWriteBytes = 0;
        long lastSeq = 0;
        try
        {
            while (_running && encoder.IsRunning && !client.IsDisposed)
            {
                if (!await client.WaitForChunkAsync(1000).ConfigureAwait(false))
                    continue;

                if (!client.TryDequeue(out var chunk))
                    continue;

                long writeStart = Stopwatch.GetTimestamp();
                long writeTicks;
                try
                {
                    using (DesktopBuddyMod.Perf.Time("stream_http_write"))
                        await ctx.Response.OutputStream.WriteAsync(chunk.Buffer, 0, chunk.Length).ConfigureAwait(false);
                    writeTicks = Stopwatch.GetTimestamp() - writeStart;
                }
                finally
                {
                    chunk.Release();
                }

                writeOps++;
                totalWriteTicks += writeTicks;
                if (writeTicks > maxWriteTicks)
                {
                    maxWriteTicks = writeTicks;
                    maxWriteBytes = chunk.Length;
                }

                totalBytes += chunk.Length;
                lastSeq = chunk.Sequence;
                if (TicksToMs(writeTicks) >= 25.0)
                    slowWrites++;

                long nowTicks = Stopwatch.GetTimestamp();
                if (TicksToMs(nowTicks - lastSummaryTicks) >= 5000.0)
                {
                    Log.Msg($"[MjpegServer] Stream client summary stream={streamId} conn={connectionId} sent={totalBytes} writeOps={writeOps} slowWrites={slowWrites} maxWrite={TicksToMs(maxWriteTicks):F2}ms/{maxWriteBytes}B avgWrite={(writeOps > 0 ? TicksToMs(totalWriteTicks) / writeOps : 0):F2}ms queueBytes={client.QueuedBytes} queueChunks={client.QueuedChunks} catchupRequests={client.CatchupRequests} catchups={client.Catchups} droppedBytes={client.DroppedBytes} droppedChunks={client.DroppedChunks} lastSeq={lastSeq}");
                    lastSummaryTicks = nowTicks;
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
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} error: {ex.GetType().Name}: {ex.Message} sent={totalBytes} queueBytes={client.QueuedBytes}");
        }
        finally
        {
            entry.Broadcaster.RemoveClient(connectionId);
            try { ctx.Response.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Stream {streamId} response close error: {ex.Message}"); }
            Log.Msg($"[MjpegServer] Stream {streamId} conn={connectionId} ended, sent {totalBytes} bytes catchups={client.Catchups} droppedBytes={client.DroppedBytes}");
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
        _running = false;
        foreach (var kvp in _streams)
        {
            kvp.Value.Broadcaster.Dispose();
            kvp.Value.Encoder.Dispose();
        }
        _streams.Clear();
        try { _listener.Stop(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener stop error: {ex.Message}"); }
        try { _listener.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener close error: {ex.Message}"); }
    }

    private sealed class StreamEntry
    {
        public readonly FfmpegEncoder Encoder;
        public readonly StreamBroadcaster Broadcaster;

        public StreamEntry(FfmpegEncoder encoder, StreamBroadcaster broadcaster)
        {
            Encoder = encoder;
            Broadcaster = broadcaster;
        }
    }

    private sealed class StreamBroadcaster : IDisposable
    {
        private readonly int _streamId;
        private readonly FfmpegEncoder _encoder;
        private readonly Func<bool> _serverRunning;
        private readonly ConcurrentDictionary<long, ClientQueue> _clients = new();
        private volatile bool _disposed;
        private int _pumpStarted;
        private long _readPos;
        private bool _aligned;
        private long _catchupRequestedAfterRingPos = -1;
        private long _sequence;

        public StreamBroadcaster(int streamId, FfmpegEncoder encoder, Func<bool> serverRunning)
        {
            _streamId = streamId;
            _encoder = encoder;
            _serverRunning = serverRunning;
        }

        public ClientQueue AddClient(long connectionId)
        {
            var client = new ClientQueue(connectionId, _encoder.LiveCatchupThresholdBytes);
            _clients[connectionId] = client;
            EnsurePumpStarted();
            Log.Msg($"[MjpegServer] Broadcaster client added stream={_streamId} conn={connectionId} clients={_clients.Count} maxQueueBytes={client.MaxQueueBytes}");
            return client;
        }

        public void RemoveClient(long connectionId)
        {
            if (!_clients.TryRemove(connectionId, out var client)) return;
            client.Dispose();
            Log.Msg($"[MjpegServer] Broadcaster client removed stream={_streamId} conn={connectionId} clients={_clients.Count} catchups={client.Catchups} droppedBytes={client.DroppedBytes}");
        }

        private void EnsurePumpStarted()
        {
            if (Interlocked.Exchange(ref _pumpStarted, 1) != 0) return;
            Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            Log.Msg($"[MjpegServer] Broadcaster pump started stream={_streamId}");
            var buffer = new byte[StreamBufferSize];
            long lastSummaryTicks = Stopwatch.GetTimestamp();
            long readBytes = 0;
            long chunks = 0;
            long keyframeChunks = 0;
            long zeroReads = 0;
            long waits = 0;
            long enqueues = 0;

            try
            {
                while (!_disposed && _serverRunning() && _encoder.IsRunning)
                {
                    if (_clients.IsEmpty)
                    {
                        _aligned = false;
                        _readPos = 0;
                        _catchupRequestedAfterRingPos = -1;
                        await Task.Delay(100).ConfigureAwait(false);
                        continue;
                    }

                    int read = _encoder.ReadStream(buffer, ref _readPos, ref _aligned, ref _catchupRequestedAfterRingPos, out bool startsAtKeyframe, out _);
                    if (read > 0)
                    {
                        var chunkBuffer = ArrayPool<byte>.Shared.Rent(read);
                        Buffer.BlockCopy(buffer, 0, chunkBuffer, 0, read);
                        var chunk = new StreamChunk(chunkBuffer, read, Interlocked.Increment(ref _sequence), startsAtKeyframe);
                        try
                        {
                            foreach (var kvp in _clients)
                            {
                                if (kvp.Value.Enqueue(chunk))
                                    enqueues++;
                            }
                        }
                        finally
                        {
                            chunk.Release();
                        }

                        readBytes += read;
                        chunks++;
                        if (startsAtKeyframe) keyframeChunks++;
                    }
                    else if (read < 0)
                    {
                        Log.Msg($"[MjpegServer] Broadcaster reader overrun stream={_streamId}; resetting reader state=({_encoder.GetReaderDiagnostics(_readPos, _aligned)})");
                        _aligned = false;
                        _catchupRequestedAfterRingPos = -1;
                    }
                    else
                    {
                        zeroReads++;
                        await _encoder.WaitForDataAsync(5).ConfigureAwait(false);
                        waits++;
                    }

                    long nowTicks = Stopwatch.GetTimestamp();
                    if (TicksToMs(nowTicks - lastSummaryTicks) >= 5000.0)
                    {
                        long queuedBytes = 0;
                        long catchupRequests = 0;
                        long catchups = 0;
                        long droppedBytes = 0;
                        foreach (var kvp in _clients)
                        {
                            queuedBytes += kvp.Value.QueuedBytes;
                            catchupRequests += kvp.Value.CatchupRequests;
                            catchups += kvp.Value.Catchups;
                            droppedBytes += kvp.Value.DroppedBytes;
                        }

                        Log.Msg($"[MjpegServer] Broadcaster summary stream={_streamId} clients={_clients.Count} readBytes={readBytes} chunks={chunks} keyframeChunks={keyframeChunks} enqueues={enqueues} zeroReads={zeroReads} waits={waits} queuedBytes={queuedBytes} catchupRequests={catchupRequests} catchups={catchups} droppedBytes={droppedBytes} state=({_encoder.GetReaderDiagnostics(_readPos, _aligned)})");
                        lastSummaryTicks = nowTicks;
                        readBytes = 0;
                        chunks = 0;
                        keyframeChunks = 0;
                        zeroReads = 0;
                        waits = 0;
                        enqueues = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Log.Msg($"[MjpegServer] Broadcaster pump error stream={_streamId}: {ex.GetType().Name}: {ex.Message} state=({_encoder.GetReaderDiagnostics(_readPos, _aligned)})");
            }
            finally
            {
                foreach (var kvp in _clients)
                    kvp.Value.Dispose();
                Log.Msg($"[MjpegServer] Broadcaster pump stopped stream={_streamId} state=({_encoder.GetReaderDiagnostics(_readPos, _aligned)})");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var kvp in _clients)
                kvp.Value.Dispose();
        }
    }

    private sealed class ClientQueue : IDisposable
    {
        private readonly object _lock = new();
        private readonly Queue<StreamChunk> _queue = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
        private bool _signalPending;
        private bool _catchupPending;
        private long _catchupRequestSequence;
        private long _queuedBytes;
        private long _droppedBytes;
        private long _droppedChunks;
        private long _catchupRequests;
        private long _catchups;
        private volatile bool _disposed;

        public long ConnectionId { get; }
        public long MaxQueueBytes { get; }
        public bool IsDisposed => _disposed;
        public long QueuedBytes { get { lock (_lock) return _queuedBytes; } }
        public int QueuedChunks { get { lock (_lock) return _queue.Count; } }
        public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
        public long DroppedChunks => Interlocked.Read(ref _droppedChunks);
        public long CatchupRequests => Interlocked.Read(ref _catchupRequests);
        public long Catchups => Interlocked.Read(ref _catchups);

        public ClientQueue(long connectionId, long maxQueueBytes)
        {
            ConnectionId = connectionId;
            MaxQueueBytes = Math.Max(256 * 1024, maxQueueBytes);
        }

        public bool Enqueue(StreamChunk chunk)
        {
            if (_disposed) return false;

            bool release = false;
            lock (_lock)
            {
                if (_disposed) return false;

                if (_catchupPending)
                {
                    if (!chunk.StartsAtKeyframe || chunk.Sequence <= _catchupRequestSequence)
                    {
                        Interlocked.Add(ref _droppedBytes, chunk.Length);
                        Interlocked.Increment(ref _droppedChunks);
                        return false;
                    }

                    _catchupPending = false;
                    Interlocked.Increment(ref _catchups);
                }
                else if (_queuedBytes + chunk.Length > MaxQueueBytes)
                {
                    DropQueuedChunksLocked();
                    _catchupPending = true;
                    _catchupRequestSequence = chunk.Sequence;
                    Interlocked.Increment(ref _catchupRequests);

                    if (!chunk.StartsAtKeyframe)
                    {
                        Interlocked.Add(ref _droppedBytes, chunk.Length);
                        Interlocked.Increment(ref _droppedChunks);
                        return false;
                    }

                    _catchupPending = false;
                    Interlocked.Increment(ref _catchups);
                }

                chunk.AddRef();
                bool wasEmpty = _queue.Count == 0;
                _queue.Enqueue(chunk);
                _queuedBytes += chunk.Length;
                if (wasEmpty && !_signalPending)
                {
                    _signalPending = true;
                    release = true;
                }
            }

            if (release)
            {
                try { _signal.Release(); }
                catch (ObjectDisposedException) { }
            }

            return true;
        }

        public Task<bool> WaitForChunkAsync(int timeoutMs)
        {
            if (_disposed) return Task.FromResult(false);
            return _signal.WaitAsync(Math.Max(1, timeoutMs));
        }

        public bool TryDequeue(out StreamChunk chunk)
        {
            bool release = false;
            lock (_lock)
            {
                _signalPending = false;
                if (_queue.Count > 0)
                {
                    chunk = _queue.Dequeue();
                    _queuedBytes -= chunk.Length;
                    if (_queuedBytes < 0) _queuedBytes = 0;
                    if (_queue.Count > 0 && !_signalPending)
                    {
                        _signalPending = true;
                        release = true;
                    }
                }
                else
                {
                    chunk = null;
                    return false;
                }
            }

            if (release)
            {
                try { _signal.Release(); }
                catch (ObjectDisposedException) { }
            }

            return true;
        }

        private void DropQueuedChunksLocked()
        {
            while (_queue.Count > 0)
            {
                var old = _queue.Dequeue();
                Interlocked.Add(ref _droppedBytes, old.Length);
                Interlocked.Increment(ref _droppedChunks);
                old.Release();
            }
            _queuedBytes = 0;
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_lock)
            {
                while (_queue.Count > 0)
                    _queue.Dequeue().Release();
                _queue.Clear();
                _queuedBytes = 0;
                _signalPending = false;
            }
            try { _signal.Release(); } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }

    private sealed class StreamChunk
    {
        public readonly byte[] Buffer;
        public readonly int Length;
        public readonly long Sequence;
        public readonly bool StartsAtKeyframe;
        private int _refCount;

        public StreamChunk(byte[] buffer, int length, long sequence, bool startsAtKeyframe)
        {
            Buffer = buffer;
            Length = length;
            Sequence = sequence;
            StartsAtKeyframe = startsAtKeyframe;
            _refCount = 1;
        }

        public void AddRef()
        {
            Interlocked.Increment(ref _refCount);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
                ArrayPool<byte>.Shared.Return(Buffer);
        }
    }
}

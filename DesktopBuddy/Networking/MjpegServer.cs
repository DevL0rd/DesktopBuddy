using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed class MjpegServer : IDisposable
{
    private HttpListener _listener;
    private volatile bool _running;
    private readonly int _port;

    private readonly ConcurrentDictionary<int, FfmpegEncoder> _encoders = new();

    public int Port => _port;

    public MjpegServer(int port = 48080)
    {
        _port = port;
        _listener = new HttpListener();
        _running = true;
    }

    public void Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            Log.Msg($"[MjpegServer] Listening on http://+:{_port}/");
        }
        catch (Exception ex)
        {
            Log.Msg($"[MjpegServer] http://+ failed ({ex.Message}), requesting admin urlacl...");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"http add urlacl url=http://+:{_port}/ sddl=D:(A;;GX;;;S-1-1-0)",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                Log.Msg($"[MjpegServer] urlacl result: {proc?.ExitCode}");
            }
            catch (Exception urlEx)
            {
                Log.Msg($"[MjpegServer] urlacl failed: {urlEx.Message}");
            }

            _listener.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            Log.Msg($"[MjpegServer] Listening on http://+:{_port}/ (after urlacl)");
        }
        _ = ListenLoopAsync();
    }

    public FfmpegEncoder CreateEncoder(int streamId)
    {
        var enc = new FfmpegEncoder(streamId);
        _encoders[streamId] = enc;
        Log.Msg($"[MjpegServer] Created encoder for stream {streamId}");
        return enc;
    }

    public void StopEncoder(int streamId)
    {
        if (_encoders.TryRemove(streamId, out var enc))
            enc.Dispose();
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
        catch (Exception ex) { Log.Msg($"[MjpegServer] Request error: {ex.Message}"); try { ctx.Response.Close(); } catch (Exception closeEx) { Log.Msg($"[MjpegServer] Response close error: {closeEx.Message}"); } }
    }

    private async Task ServeStreamAsync(HttpListenerContext ctx, string urlPath)
    {
        var parts = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

        if (!int.TryParse(parts[1], out int streamId) || !_encoders.TryGetValue(streamId, out var encoder))
        {
            Log.Msg($"[MjpegServer] Stream {streamId} not found");
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

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

        Log.Msg($"[MjpegServer] Serving stream {streamId} to {ctx.Request.RemoteEndPoint}");
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.SendChunked = true;
        ctx.Response.StatusCode = 200;

        long totalBytes = 0;
        long readPos = 0;
        bool aligned = false;
        try
        {
            var buffer = new byte[65536];
            while (_running && encoder.IsRunning)
            {
                int read = encoder.ReadStream(buffer, ref readPos, ref aligned);
                if (read > 0)
                {
                    await ctx.Response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    totalBytes += read;
                }
                else
                {
                    await encoder.WaitForDataAsync(5).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[MjpegServer] Stream {streamId} error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Stream {streamId} response close error: {ex.Message}"); }
            Log.Msg($"[MjpegServer] Stream {streamId} ended, sent {totalBytes} bytes");
        }
    }

    public void Dispose()
    {
        _running = false;
        foreach (var kvp in _encoders) kvp.Value.Dispose();
        _encoders.Clear();
        try { _listener.Stop(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener stop error: {ex.Message}"); }
        try { _listener.Close(); } catch (Exception ex) { Log.Msg($"[MjpegServer] Listener close error: {ex.Message}"); }
    }

}

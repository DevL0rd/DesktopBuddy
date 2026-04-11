using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace DesktopBuddyRenderer
{
    public sealed class MjpegServer : IDisposable
    {
        private static ManualLogSource Log => DesktopBuddyRendererPlugin.Log;

        private HttpListener _listener;
        private volatile bool _running;
        private readonly int _port;

        private readonly Dictionary<int, FfmpegEncoder> _encoders = new Dictionary<int, FfmpegEncoder>();
        private readonly object _encodersLock = new object();

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
            Log.LogInfo($"[MjpegServer] Listening on http://+:{_port}/");
            Task.Run(() => ListenLoopAsync());
        }

        public void RegisterEncoder(int streamId, FfmpegEncoder encoder)
        {
            lock (_encodersLock)
            {
                _encoders[streamId] = encoder;
            }
            Log.LogInfo($"[MjpegServer] Registered encoder for stream {streamId}");
        }

        public void UnregisterEncoder(int streamId)
        {
            lock (_encodersLock)
            {
                _encoders.Remove(streamId);
            }
        }

        private bool TryGetEncoder(int streamId, out FfmpegEncoder encoder)
        {
            lock (_encodersLock)
            {
                return _encoders.TryGetValue(streamId, out encoder);
            }
        }

        private async Task ListenLoopAsync()
        {
            Log.LogInfo("[MjpegServer] Async listen loop started");
            while (_running)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    Task.Run(() => HandleRequestAsync(ctx));
                }
                catch (HttpListenerException ex) { Log.LogInfo($"[MjpegServer] Listener stopped: {ex.Message}"); break; }
                catch (ObjectDisposedException) { Log.LogInfo("[MjpegServer] Listener disposed, stopping"); break; }
                catch (Exception ex)
                {
                    Log.LogWarning($"[MjpegServer] Listen error: {ex.Message}");
                }
            }
            Log.LogInfo("[MjpegServer] Async listen loop ended");
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url != null ? ctx.Request.Url.AbsolutePath : "/";
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
                Log.LogWarning($"[MjpegServer] Request error: {ex.Message}");
                try { ctx.Response.Close(); } catch { }
            }
        }

        private async Task ServeStreamAsync(HttpListenerContext ctx, string urlPath)
        {
            // Manual split for net472 (no StringSplitOptions overload on string.Split(char))
            var rawParts = urlPath.Split('/');
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < rawParts.Length; i++)
            {
                if (rawParts[i].Length > 0)
                    parts.Add(rawParts[i]);
            }

            if (parts.Count < 2) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            int streamId;
            FfmpegEncoder encoder;
            if (!int.TryParse(parts[1], out streamId) || !TryGetEncoder(streamId, out encoder))
            {
                Log.LogInfo($"[MjpegServer] Stream not found for path: {urlPath}");
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
                Log.LogWarning($"[MjpegServer] Stream {streamId} encoder not ready after {waitCount * 100}ms");
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                return;
            }

            Log.LogInfo($"[MjpegServer] Serving stream {streamId} to {ctx.Request.RemoteEndPoint}");
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
                Log.LogInfo($"[MjpegServer] Stream {streamId} error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
                Log.LogInfo($"[MjpegServer] Stream {streamId} ended, sent {totalBytes} bytes");
            }
        }

        public void Dispose()
        {
            _running = false;
            lock (_encodersLock)
            {
                _encoders.Clear();
            }
            try { _listener.Stop(); } catch (Exception ex) { Log.LogWarning($"[MjpegServer] Listener stop error: {ex.Message}"); }
            try { _listener.Close(); } catch (Exception ex) { Log.LogWarning($"[MjpegServer] Listener close error: {ex.Message}"); }
        }
    }
}

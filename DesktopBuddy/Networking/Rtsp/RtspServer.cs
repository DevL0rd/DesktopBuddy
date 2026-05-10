using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class RtspServer : IDisposable
{
    private readonly int _port;
    private readonly object _endpointLock = new();
    private string _publicHost;
    private int _publicPort;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener _listener;
    private long _nextClientId;

    public RtspStreamRegistry Streams { get; } = new();
    public int Port => _port;
    public string PublicHost { get { lock (_endpointLock) return _publicHost; } }
    public int PublicPort { get { lock (_endpointLock) return _publicPort; } }

    public RtspServer(int port, string publicHost)
    {
        _port = port;
        _publicHost = string.IsNullOrWhiteSpace(publicHost) ? "127.0.0.1" : publicHost.Trim();
        _publicPort = port;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Log.Msg($"[RTSP] Listening on rtsp://+:{_port}/, publicHost={_publicHost}");
        _ = AcceptLoopAsync();
    }

    public FfmpegEncoder CreateEncoder(int streamId)
    {
        var stream = Streams.GetOrCreate(streamId);
        var encoder = new FfmpegEncoder(streamId, stream);
        stream.KeyframeRequested += encoder.RequestKeyframe;
        Log.Msg($"[RTSP] Created embedded encoder for stream {streamId}");
        return encoder;
    }

    public void StopStream(int streamId)
    {
        Log.MsgImmediate($"[CleanupTrace] RtspServer.StopStream ENTER stream={streamId}");
        Streams.Remove(streamId);
        Log.MsgImmediate($"[CleanupTrace] RtspServer.StopStream EXIT stream={streamId}");
    }

    public Uri GetStreamUri(int streamId)
    {
        lock (_endpointLock)
            return new Uri($"rtsp://{_publicHost}:{_publicPort}/stream/{streamId}");
    }

    public void UpdatePublicEndpoint(string host, int port, string source)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return;

        lock (_endpointLock)
        {
            _publicHost = host.Trim();
            _publicPort = port;
        }

        Log.Msg($"[RTSP] Public endpoint updated from {source}: rtsp://{host.Trim()}:{port}/stream/{{streamId}}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        Streams.Clear();
        _cts.Dispose();
        Log.Msg("[RTSP] Server disposed");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                long clientId = Interlocked.Increment(ref _nextClientId);
                var session = new RtspSession(clientId, tcp, Streams);
                _ = session.RunAsync(_cts.Token);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (_cts.IsCancellationRequested)
            {
                Log.Msg($"[RTSP] Listener stopped: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Log.Msg($"[RTSP] Accept error: {ex.Message}");
            }
        }
    }
}

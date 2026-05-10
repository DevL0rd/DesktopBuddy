using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class RtspSession : IDisposable
{
    private const int MaxQueuedRtpPackets = 256;

    private readonly long _clientId;
    private readonly TcpClient _client;
    private readonly RtspStreamRegistry _registry;
    private readonly string _sessionId;
    private readonly ConcurrentQueue<RtpPacket> _sendQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _localCts = new();
    private NetworkStream _network;
    private RtspStream _stream;
    private int _streamId;
    private volatile bool _playing;
    private volatile bool _waitingForKeyframe;
    private volatile bool _videoSetup;
    private volatile bool _audioSetup;
    private int _queuedPackets;
    private int _disposed;

    public RtspSession(long clientId, TcpClient client, RtspStreamRegistry registry)
    {
        _clientId = clientId;
        _client = client;
        _registry = registry;
        _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _localCts.Token);
        try
        {
            _client.NoDelay = true;
            _network = _client.GetStream();
            Log.Msg($"[RTSP] Client {_clientId} connected from {_client.Client.RemoteEndPoint}");
            _ = SendLoopAsync(linkedCts.Token);

            while (!linkedCts.IsCancellationRequested && _client.Connected)
            {
                var request = await RtspRequestReader.ReadAsync(_network, linkedCts.Token).ConfigureAwait(false);
                if (request == null) break;
                await HandleRequestAsync(request, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            Log.Msg($"[RTSP] Client {_clientId} IO closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] Client {_clientId} error: {ex}");
        }
        finally
        {
            Dispose();
            Log.Msg($"[RTSP] Client {_clientId} disconnected");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _playing = false;
        try { _localCts.Cancel(); } catch (ObjectDisposedException) { }
        if (_stream != null)
        {
            _stream.Unsubscribe(EnqueueRtp);
            _stream = null;
        }

        try { _sendSignal.Release(); } catch { }
        try { _client.Close(); } catch { }
        try { _sendSignal.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        try { _localCts.Dispose(); } catch { }
    }

    private async Task HandleRequestAsync(RtspRequest request, CancellationToken token)
    {
        string method = request.Method.ToUpperInvariant();
        string cseq = request.Headers.TryGetValue("CSeq", out string value) ? value : "0";
        LogRtspRequest(request, cseq);

        switch (method)
        {
            case "OPTIONS":
                await SendResponseAsync(200, "OK", cseq, new Dictionary<string, string>
                {
                    ["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN"
                }, null, token).ConfigureAwait(false);
                break;

            case "DESCRIBE":
                await HandleDescribeAsync(request, cseq, token).ConfigureAwait(false);
                break;

            case "SETUP":
                await HandleSetupAsync(request, cseq, token).ConfigureAwait(false);
                break;

            case "PLAY":
                await HandlePlayAsync(cseq, token).ConfigureAwait(false);
                break;

            case "PAUSE":
                _playing = false;
                await SendResponseAsync(200, "OK", cseq, SessionHeaders(), null, token).ConfigureAwait(false);
                break;

            case "TEARDOWN":
                _playing = false;
                await SendResponseAsync(200, "OK", cseq, SessionHeaders(), null, token).ConfigureAwait(false);
                Dispose();
                break;

            default:
                await SendResponseAsync(405, "Method Not Allowed", cseq, null, null, token).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleDescribeAsync(RtspRequest request, string cseq, CancellationToken token)
    {
        if (!TryParseStreamId(request.Uri, out int streamId) || !_registry.TryGet(streamId, out var stream))
        {
            await SendResponseAsync(404, "Not Found", cseq, null, null, token).ConfigureAwait(false);
            return;
        }

        if (!await WaitForStreamInfoAsync(stream, token).ConfigureAwait(false))
        {
            await SendResponseAsync(503, "Service Unavailable", cseq, null, null, token).ConfigureAwait(false);
            return;
        }

        _streamId = streamId;
        string sdp = stream.BuildSdp("trackID=0");
        byte[] body = Encoding.ASCII.GetBytes(sdp ?? "");
        Log.Msg($"[RTSP] Client {_clientId} DESCRIBE SDP stream={streamId}:\n{sdp}");
        await SendResponseAsync(200, "OK", cseq, new Dictionary<string, string>
        {
            ["Content-Base"] = request.Uri,
            ["Content-Type"] = "application/sdp",
            ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture)
        }, body, token).ConfigureAwait(false);

        Log.Msg($"[RTSP] Client {_clientId} DESCRIBE stream={streamId}");
    }

    private async Task HandleSetupAsync(RtspRequest request, string cseq, CancellationToken token)
    {
        if (_streamId <= 0 && !TryParseStreamId(request.Uri, out _streamId))
        {
            await SendResponseAsync(454, "Session Not Found", cseq, null, null, token).ConfigureAwait(false);
            return;
        }

        if (!_registry.TryGet(_streamId, out var stream))
        {
            await SendResponseAsync(404, "Not Found", cseq, null, null, token).ConfigureAwait(false);
            return;
        }

        request.Headers.TryGetValue("Transport", out string requestedTransport);
        if (string.IsNullOrWhiteSpace(requestedTransport) ||
            requestedTransport.IndexOf("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) < 0)
        {
            await SendResponseAsync(461, "Unsupported Transport", cseq, new Dictionary<string, string>
            {
                ["Transport"] = "RTP/AVP/TCP;unicast;interleaved=0-1"
            }, null, token).ConfigureAwait(false);
            Log.Msg($"[RTSP] Client {_clientId} SETUP rejected unsupported transport: {requestedTransport ?? "null"}");
            return;
        }

        int trackId = TryParseTrackId(request.Uri, out int parsedTrackId) ? parsedTrackId : 0;
        bool isAudioTrack = trackId == 1;
        string interleaved = isAudioTrack ? "2-3" : "0-1";

        _stream = stream;
        if (isAudioTrack)
            _audioSetup = true;
        else
            _videoSetup = true;

        await SendResponseAsync(200, "OK", cseq, new Dictionary<string, string>
        {
            ["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={interleaved}",
            ["Session"] = _sessionId
        }, null, token).ConfigureAwait(false);

        Log.Msg($"[RTSP] Client {_clientId} SETUP stream={_streamId} track={trackId} interleaved={interleaved}");
    }

    private async Task HandlePlayAsync(string cseq, CancellationToken token)
    {
        if (_stream == null)
        {
            await SendResponseAsync(454, "Session Not Found", cseq, null, null, token).ConfigureAwait(false);
            return;
        }

        _waitingForKeyframe = _videoSetup;
        _playing = true;
        _stream.Subscribe(EnqueueRtp);
        if (_videoSetup)
            _stream.RequestKeyframe();

        await SendResponseAsync(200, "OK", cseq, new Dictionary<string, string>
        {
            ["Session"] = _sessionId,
            ["Range"] = "npt=0.000-"
        }, null, token).ConfigureAwait(false);

        Log.Msg($"[RTSP] Client {_clientId} PLAY stream={_streamId} video={_videoSetup} audio={_audioSetup} subscribers={_stream.SubscriberCount}");
    }

    private void EnqueueRtp(RtpPacket packet)
    {
        if (!_playing || packet == null || packet.Data.Length == 0) return;
        if (packet.Channel == 0 && !_videoSetup) return;
        if (packet.Channel == 2 && !_audioSetup) return;

        if (_waitingForKeyframe)
        {
            if (packet.Channel != 0 || !packet.StartsKeyframe) return;
            _waitingForKeyframe = false;
            Log.Msg($"[RTSP] Client {_clientId} stream={_streamId} starting at keyframe");
        }

        int queued = Interlocked.Increment(ref _queuedPackets);
        if (queued > MaxQueuedRtpPackets)
        {
            Interlocked.Decrement(ref _queuedPackets);
            return;
        }

        _sendQueue.Enqueue(packet);
        try { _sendSignal.Release(); } catch { }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        var header = new byte[4];
        header[0] = (byte)'$';

        while (!token.IsCancellationRequested)
        {
            try { await _sendSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            while (_sendQueue.TryDequeue(out var packet))
            {
                Interlocked.Decrement(ref _queuedPackets);
                if (!_playing || packet.Data.Length > ushort.MaxValue) continue;

                header[1] = packet.Channel;
                header[2] = (byte)(packet.Data.Length >> 8);
                header[3] = (byte)(packet.Data.Length & 0xFF);
                await _writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await _network.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                    await _network.WriteAsync(packet.Data, 0, packet.Data.Length, token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
    }

    private async Task SendResponseAsync(int status, string reason, string cseq, Dictionary<string, string> headers, byte[] body, CancellationToken token)
    {
        LogRtspResponse(status, reason, cseq, headers, body);

        var builder = new StringBuilder();
        builder.Append("RTSP/1.0 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        builder.Append("CSeq: ").Append(cseq).Append("\r\n");
        if (headers != null)
        {
            foreach (var header in headers)
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("\r\n");
        byte[] responseHead = Encoding.ASCII.GetBytes(builder.ToString());
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _network.WriteAsync(responseHead, 0, responseHead.Length, token).ConfigureAwait(false);
            if (body != null && body.Length > 0)
                await _network.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Dictionary<string, string> SessionHeaders()
    {
        return new Dictionary<string, string> { ["Session"] = _sessionId };
    }

    private void LogRtspRequest(RtspRequest request, string cseq)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append("[RTSP] Client ").Append(_clientId)
                .Append(" <- ").Append(request.Method)
                .Append(" cseq=").Append(cseq)
                .Append(" uri=").Append(request.Uri);

            foreach (var header in request.Headers)
                builder.Append("\n[RTSP]   ").Append(header.Key).Append(": ").Append(header.Value);

            Log.Msg(builder.ToString());
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] Client {_clientId} request log failed: {ex.Message}");
        }
    }

    private void LogRtspResponse(int status, string reason, string cseq, Dictionary<string, string> headers, byte[] body)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append("[RTSP] Client ").Append(_clientId)
                .Append(" -> ").Append(status).Append(' ').Append(reason)
                .Append(" cseq=").Append(cseq);

            if (headers != null)
            {
                foreach (var header in headers)
                    builder.Append("\n[RTSP]   ").Append(header.Key).Append(": ").Append(header.Value);
            }

            if (body != null && body.Length > 0)
                builder.Append("\n[RTSP]   bodyBytes=").Append(body.Length);

            Log.Msg(builder.ToString());
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] Client {_clientId} response log failed: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForStreamInfoAsync(RtspStream stream, CancellationToken token)
    {
        for (int i = 0; i < 100 && !token.IsCancellationRequested; i++)
        {
            if (stream.HasStreamInfo) return true;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        return stream.HasStreamInfo;
    }

    private static bool TryParseStreamId(string uriText, out int streamId)
    {
        streamId = 0;
        string path = uriText;
        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("stream", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(parts[1], out streamId);

        return false;
    }

    private static bool TryParseTrackId(string uriText, out int trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(uriText))
            return false;

        string text = uriText;
        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            text = uri.AbsolutePath;

        if (text.IndexOf("trackID=1", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.EndsWith("/track1", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith("/audio", StringComparison.OrdinalIgnoreCase))
        {
            trackId = 1;
            return true;
        }

        if (text.IndexOf("trackID=0", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.EndsWith("/track0", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith("/video", StringComparison.OrdinalIgnoreCase))
        {
            trackId = 0;
            return true;
        }

        return false;
    }

}

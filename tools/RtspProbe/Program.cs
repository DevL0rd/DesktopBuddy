using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) || uri.Scheme != "rtsp")
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/RtspProbe -- rtsp://host:port/stream/1 [seconds]");
    return 2;
}

int seconds = args.Length >= 2 && int.TryParse(args[1], out var parsedSeconds)
    ? Math.Clamp(parsedSeconds, 1, 60)
    : 8;

using var client = new TcpClient { NoDelay = true };
await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554);
await using NetworkStream stream = client.GetStream();
stream.ReadTimeout = 5000;
stream.WriteTimeout = 5000;

var probe = new RtspProbe(stream, uri);
await probe.OptionsAsync();
string sdp = await probe.DescribeAsync();
string control = ExtractControl(sdp) ?? "trackID=0";
await probe.SetupAsync(control);
await probe.PlayAsync();
await probe.ReadInterleavedAsync(TimeSpan.FromSeconds(seconds));
await probe.TeardownAsync();

return 0;

static string? ExtractControl(string sdp)
{
    foreach (string rawLine in sdp.Split('\n'))
    {
        string line = rawLine.Trim();
        if (line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
        {
            string value = line["a=control:".Length..].Trim();
            if (value.Length > 0 && value != "*")
                return value;
        }
    }

    return null;
}

internal sealed class RtspProbe
{
    private readonly NetworkStream _stream;
    private readonly Uri _uri;
    private int _cseq;
    private string? _session;

    public RtspProbe(NetworkStream stream, Uri uri)
    {
        _stream = stream;
        _uri = uri;
    }

    public Task OptionsAsync() =>
        SendAndReadAsync("OPTIONS", _uri.ToString(), null, expectBody: false);

    public async Task<string> DescribeAsync()
    {
        var response = await SendAndReadAsync("DESCRIBE", _uri.ToString(), "Accept: application/sdp\r\n", expectBody: true);
        Console.WriteLine("----- SDP -----");
        Console.WriteLine(response.Body);
        Console.WriteLine("---------------");
        return response.Body;
    }

    public Task SetupAsync(string control)
    {
        string setupUri = control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            ? control
            : _uri.ToString().TrimEnd('/') + "/" + control.TrimStart('/');

        return SendAndReadAsync(
            "SETUP",
            setupUri,
            "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n",
            expectBody: false);
    }

    public Task PlayAsync() =>
        SendAndReadAsync("PLAY", _uri.ToString(), SessionHeader() + "Range: npt=0.000-\r\n", expectBody: false);

    public Task TeardownAsync() =>
        SendAndReadAsync("TEARDOWN", _uri.ToString(), SessionHeader(), expectBody: false);

    public async Task ReadInterleavedAsync(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        var header = new byte[4];
        long packets = 0;
        long bytes = 0;
        ushort firstSeq = 0;
        uint firstTimestamp = 0;
        bool firstRtp = true;

        while (DateTime.UtcNow < end)
        {
            int b = await ReadByteWithTimeoutAsync(1000);
            if (b < 0) continue;
            if (b != '$') continue;

            header[0] = (byte)b;
            await ReadExactAsync(header, 1, 3);
            int channel = header[1];
            int length = (header[2] << 8) | header[3];
            if (length <= 0) continue;

            var payload = new byte[length];
            await ReadExactAsync(payload, 0, payload.Length);
            packets++;
            bytes += length;

            if (firstRtp && length >= 12)
            {
                firstRtp = false;
                firstSeq = (ushort)((payload[2] << 8) | payload[3]);
                firstTimestamp = ((uint)payload[4] << 24) |
                                 ((uint)payload[5] << 16) |
                                 ((uint)payload[6] << 8) |
                                 payload[7];
                Console.WriteLine($"First RTP: channel={channel} pt={payload[1] & 0x7F} marker={(payload[1] & 0x80) != 0} seq={firstSeq} ts={firstTimestamp} bytes={length}");
            }
        }

        Console.WriteLine($"Interleaved RTP: packets={packets} bytes={bytes} seconds={duration.TotalSeconds:F1}");
    }

    private async Task<RtspResponse> SendAndReadAsync(string method, string requestUri, string? extraHeaders, bool expectBody)
    {
        int cseq = ++_cseq;
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(requestUri).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(cseq).Append("\r\n");
        builder.Append("User-Agent: DesktopBuddyRtspProbe/1.0\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
            builder.Append(extraHeaders);
        builder.Append("\r\n");

        byte[] request = Encoding.ASCII.GetBytes(builder.ToString());
        await _stream.WriteAsync(request);
        await _stream.FlushAsync();

        RtspResponse response = await ReadResponseAsync(expectBody);
        Console.WriteLine($"{method} -> {response.StatusLine}");
        if (response.Headers.TryGetValue("Session", out string? session) && !string.IsNullOrWhiteSpace(session))
            _session = session.Split(';')[0].Trim();
        return response;
    }

    private async Task<RtspResponse> ReadResponseAsync(bool expectBody)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = await ReadByteWithTimeoutAsync(5000);
            if (b < 0) throw new IOException("Timed out waiting for RTSP response");

            if (b == '$')
            {
                var interleavedHeader = new byte[3];
                await ReadExactAsync(interleavedHeader, 0, interleavedHeader.Length);
                int packetLength = (interleavedHeader[1] << 8) | interleavedHeader[2];
                if (packetLength > 0)
                {
                    var discard = new byte[packetLength];
                    await ReadExactAsync(discard, 0, discard.Length);
                }

                continue;
            }

            bytes.Add((byte)b);
            int count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == '\r' &&
                bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' &&
                bytes[count - 1] == '\n')
                break;
        }

        string headerText = Encoding.ASCII.GetString(bytes.ToArray());
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string body = "";
        if (expectBody && headers.TryGetValue("Content-Length", out string? lengthText) && int.TryParse(lengthText, out int length) && length > 0)
        {
            var bodyBytes = new byte[length];
            await ReadExactAsync(bodyBytes, 0, bodyBytes.Length);
            body = Encoding.ASCII.GetString(bodyBytes);
        }

        return new RtspResponse(lines[0], headers, body);
    }

    private string SessionHeader() =>
        string.IsNullOrWhiteSpace(_session) ? "" : $"Session: {_session}\r\n";

    private async Task<int> ReadByteWithTimeoutAsync(int timeoutMs)
    {
        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            int read = await _stream.ReadAsync(buffer, cts.Token);
            return read == 1 ? buffer[0] : -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset, count));
            if (read <= 0)
                throw new IOException("RTSP socket closed");
            offset += read;
            count -= read;
        }
    }

    private sealed record RtspResponse(string StatusLine, Dictionary<string, string> Headers, string Body);
}

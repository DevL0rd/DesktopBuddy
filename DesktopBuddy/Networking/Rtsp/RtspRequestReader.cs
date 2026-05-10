using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuddy.Networking.Rtsp;

internal static class RtspRequestReader
{
    public static async Task<RtspRequest> ReadAsync(NetworkStream network, CancellationToken token)
    {
        string requestLine = await ReadRtspLineAsync(network, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine)) return null;

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 3) return null;

        var headers = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string line = await ReadRtspLineAsync(network, token).ConfigureAwait(false);
            if (line == null) return null;
            if (line.Length == 0) break;

            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new RtspRequest(parts[0], parts[1], headers);
    }

    private static async Task<string> ReadRtspLineAsync(NetworkStream network, CancellationToken token)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];

        while (!token.IsCancellationRequested)
        {
            int read = await network.ReadAsync(one, 0, 1, token).ConfigureAwait(false);
            if (read == 0) return null;

            byte value = one[0];
            if (value == (byte)'$' && bytes.Count == 0)
            {
                await SkipInterleavedFrameAsync(network, token).ConfigureAwait(false);
                continue;
            }

            if (value == (byte)'\n')
            {
                int count = bytes.Count;
                if (count > 0 && bytes[count - 1] == (byte)'\r')
                    bytes.RemoveAt(count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(value);
            if (bytes.Count > 8192)
                throw new InvalidDataException("RTSP line too long");
        }

        return null;
    }

    private static async Task SkipInterleavedFrameAsync(NetworkStream network, CancellationToken token)
    {
        byte[] header = new byte[3];
        await ReadExactAsync(network, header, 0, header.Length, token).ConfigureAwait(false);
        int length = (header[1] << 8) | header[2];
        byte[] buffer = new byte[System.Math.Min(length, 4096)];
        int remaining = length;
        while (remaining > 0)
        {
            int chunk = System.Math.Min(remaining, buffer.Length);
            await ReadExactAsync(network, buffer, 0, chunk, token).ConfigureAwait(false);
            remaining -= chunk;
        }
    }

    private static async Task ReadExactAsync(NetworkStream network, byte[] buffer, int offset, int count, CancellationToken token)
    {
        while (count > 0)
        {
            int read = await network.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
            if (read == 0) throw new IOException("RTSP socket closed");
            offset += read;
            count -= read;
        }
    }
}

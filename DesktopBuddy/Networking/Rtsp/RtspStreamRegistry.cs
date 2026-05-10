using System.Collections.Concurrent;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class RtspStreamRegistry
{
    private readonly ConcurrentDictionary<int, RtspStream> _streams = new();

    public RtspStream GetOrCreate(int streamId)
    {
        return _streams.GetOrAdd(streamId, id => new RtspStream(id));
    }

    public bool TryGet(int streamId, out RtspStream stream)
    {
        return _streams.TryGetValue(streamId, out stream);
    }

    public void Remove(int streamId)
    {
        if (_streams.TryRemove(streamId, out var stream))
            stream.Dispose();
    }

    public void Clear()
    {
        foreach (var pair in _streams)
        {
            if (_streams.TryRemove(pair.Key, out var stream))
                stream.Dispose();
        }
    }
}

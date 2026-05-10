using System;

namespace DesktopBuddy;

public sealed class EncodedAudioPacket
{
    public byte[] Data { get; }
    public long Pts { get; }

    public EncodedAudioPacket(byte[] data, long pts)
    {
        Data = data ?? Array.Empty<byte>();
        Pts = pts;
    }
}

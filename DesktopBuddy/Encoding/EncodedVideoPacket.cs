using System;

namespace DesktopBuddy;

public sealed class EncodedVideoPacket
{
    public EncodedVideoCodec Codec { get; }
    public byte[] Data { get; }
    public long Pts90k { get; }
    public bool IsKeyframe { get; }

    public EncodedVideoPacket(EncodedVideoCodec codec, byte[] data, long pts90k, bool isKeyframe)
    {
        Codec = codec;
        Data = data ?? Array.Empty<byte>();
        Pts90k = pts90k;
        IsKeyframe = isKeyframe;
    }
}

using System;

namespace DesktopBuddy;

public sealed class EncodedVideoStreamInfo
{
    public EncodedVideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] ExtraData { get; }

    public EncodedVideoStreamInfo(EncodedVideoCodec codec, int width, int height, byte[] extraData)
    {
        Codec = codec;
        Width = width;
        Height = height;
        ExtraData = extraData ?? Array.Empty<byte>();
    }
}

using System;

namespace DesktopBuddy;

public sealed class EncodedAudioStreamInfo
{
    public int SampleRate { get; }
    public int Channels { get; }
    public byte[] ExtraData { get; }

    public EncodedAudioStreamInfo(int sampleRate, int channels, byte[] extraData)
    {
        SampleRate = sampleRate;
        Channels = channels;
        ExtraData = extraData ?? Array.Empty<byte>();
    }
}

using System;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class RtpPacket
{
    public byte[] Data { get; }
    public long Timestamp90k { get; }
    public bool StartsKeyframe { get; }
    public byte Channel { get; }

    public RtpPacket(byte[] data, long timestamp90k, bool startsKeyframe, byte channel = 0)
    {
        Data = data ?? Array.Empty<byte>();
        Timestamp90k = timestamp90k;
        StartsKeyframe = startsKeyframe;
        Channel = channel;
    }
}

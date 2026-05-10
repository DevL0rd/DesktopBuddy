using System;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public interface IRtpPacketizer : IDisposable
{
    event Action<RtpPacket> PacketReady;
    string BuildSdp(string controlUrl);
    void Write(EncodedVideoPacket packet);
}

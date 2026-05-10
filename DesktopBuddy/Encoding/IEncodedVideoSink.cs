namespace DesktopBuddy;

public interface IEncodedVideoSink
{
    void SetStreamInfo(EncodedVideoStreamInfo info);
    void SetAudioInfo(EncodedAudioStreamInfo info);
    void WriteVideoPacket(EncodedVideoPacket packet);
    void WriteAudioPacket(EncodedAudioPacket packet);
    void RequestKeyframe();
}

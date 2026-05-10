using System;
using System.Collections.Generic;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class RtspStream : IEncodedVideoSink, IDisposable
{
    private readonly object _lock = new();
    private readonly List<Action<RtpPacket>> _subscribers = new();
    private IRtpPacketizer _videoPacketizer;
    private FfmpegAudioRtpPacketizer _audioPacketizer;
    private EncodedVideoStreamInfo _videoInfo;
    private EncodedAudioStreamInfo _audioInfo;

    public int StreamId { get; }
    public int SubscriberCount
    {
        get { lock (_lock) return _subscribers.Count; }
    }

    public event Action KeyframeRequested;

    public RtspStream(int streamId)
    {
        StreamId = streamId;
    }

    public bool HasStreamInfo
    {
        get { lock (_lock) return _videoInfo != null; }
    }

    public void SetStreamInfo(EncodedVideoStreamInfo info)
    {
        if (info == null) return;

        lock (_lock)
        {
            if (_videoInfo != null &&
                _videoInfo.Codec == info.Codec &&
                _videoInfo.Width == info.Width &&
                _videoInfo.Height == info.Height)
            {
                return;
            }

            _videoPacketizer?.Dispose();
            _videoInfo = info;
            _videoPacketizer = new FfmpegRtpPacketizer(info);
            _videoPacketizer.PacketReady += Broadcast;
            Log.Msg($"[RTSP] Stream {StreamId} info: codec={info.Codec} size={info.Width}x{info.Height} extra={info.ExtraData.Length}B");
        }
    }

    public void SetAudioInfo(EncodedAudioStreamInfo info)
    {
        if (info == null) return;

        lock (_lock)
        {
            if (_audioInfo != null &&
                _audioInfo.SampleRate == info.SampleRate &&
                _audioInfo.Channels == info.Channels)
            {
                return;
            }

            _audioPacketizer?.Dispose();
            _audioInfo = info;
            _audioPacketizer = new FfmpegAudioRtpPacketizer(info);
            _audioPacketizer.PacketReady += Broadcast;
            Log.Msg($"[RTSP] Stream {StreamId} audio info: codec=AAC sampleRate={info.SampleRate} channels={info.Channels} extra={info.ExtraData.Length}B");
        }
    }

    public string BuildSdp(string controlUrl)
    {
        lock (_lock)
        {
            string videoSdp = _videoPacketizer?.BuildSdp("trackID=0");
            string audioSdp = _audioPacketizer?.BuildSdp("trackID=1");
            return CombineSdp(videoSdp, audioSdp);
        }
    }

    public void WriteVideoPacket(EncodedVideoPacket packet)
    {
        IRtpPacketizer packetizer;
        lock (_lock)
            packetizer = _videoPacketizer;

        packetizer?.Write(packet);
    }

    public void WriteAudioPacket(EncodedAudioPacket packet)
    {
        FfmpegAudioRtpPacketizer packetizer;
        lock (_lock)
            packetizer = _audioPacketizer;

        packetizer?.Write(packet);
    }

    public void RequestKeyframe()
    {
        try { KeyframeRequested?.Invoke(); }
        catch (Exception ex) { Log.Msg($"[RTSP] Stream {StreamId} keyframe request callback failed: {ex.Message}"); }
    }

    public void Subscribe(Action<RtpPacket> subscriber)
    {
        if (subscriber == null) return;
        lock (_lock)
        {
            if (!_subscribers.Contains(subscriber))
                _subscribers.Add(subscriber);
        }
    }

    public void Unsubscribe(Action<RtpPacket> subscriber)
    {
        if (subscriber == null) return;
        lock (_lock)
            _subscribers.Remove(subscriber);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _subscribers.Clear();
            _videoPacketizer?.Dispose();
            _audioPacketizer?.Dispose();
            _videoPacketizer = null;
            _audioPacketizer = null;
            _videoInfo = null;
            _audioInfo = null;
        }
    }

    private void Broadcast(RtpPacket packet)
    {
        Action<RtpPacket>[] subscribers;
        lock (_lock)
            subscribers = _subscribers.ToArray();

        foreach (var subscriber in subscribers)
        {
            try { subscriber(packet); }
            catch (Exception ex) { Log.Msg($"[RTSP] Stream {StreamId} subscriber failed: {ex.Message}"); }
        }
    }

    private static string CombineSdp(string videoSdp, string audioSdp)
    {
        if (string.IsNullOrWhiteSpace(audioSdp))
            return videoSdp;
        if (string.IsNullOrWhiteSpace(videoSdp))
            return audioSdp;

        string normalizedVideo = NormalizeSdp(videoSdp);
        string normalizedAudio = NormalizeSdp(audioSdp);
        int videoMedia = normalizedVideo.IndexOf("\nm=", StringComparison.Ordinal);
        int audioMedia = normalizedAudio.IndexOf("\nm=", StringComparison.Ordinal);
        if (videoMedia < 0 || audioMedia < 0)
            return normalizedVideo;

        string session = normalizedVideo[..(videoMedia + 1)];
        string videoMediaSection = normalizedVideo[(videoMedia + 1)..];
        string audioMediaSection = normalizedAudio[(audioMedia + 1)..];
        return (session + videoMediaSection + audioMediaSection).Replace("\n", "\r\n");
    }

    private static string NormalizeSdp(string sdp)
    {
        string normalized = sdp.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.EndsWith("\n", StringComparison.Ordinal))
            normalized += "\n";
        return normalized;
    }
}

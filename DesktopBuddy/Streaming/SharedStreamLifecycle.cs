using System;
using System.Threading;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static SharedStream AcquireSharedStream(IntPtr hwnd, bool useMediaMtx)
    {
        lock (_sharedStreams)
        {
            if (hwnd != IntPtr.Zero && _sharedStreams.TryGetValue(hwnd, out var shared))
            {
                Msg($"[RemoteStream] Reusing shared stream {shared.StreamId} for hwnd={hwnd} (refs={shared.RefCount})");
                shared.RefCount++;
                return shared;
            }

            int streamId = NextStreamId();
            FfmpegEncoder encoder;
            Uri url;

            if (useMediaMtx)
            {
                var rtspUrl = GetMediaMtxRtspUrl(streamId);
                encoder = new FfmpegEncoder(streamId, rtspUrl);
                url = new Uri(rtspUrl);
                Msg($"[RemoteStream] Using MediaMTX RTSP: {rtspUrl}");
            }
            else
            {
                encoder = StreamServer.CreateEncoder(streamId);
                url = GetBuiltInStreamUrl(streamId);
                Msg($"[RemoteStream] Using built-in HTTP stream: {url}");
            }

            var audio = new AudioCapture();
            var startAudio = CreateAudioStartAction(audio, hwnd);

            shared = new SharedStream
            {
                StreamId = streamId,
                Encoder = encoder,
                Audio = audio,
                StartAudio = startAudio,
                StreamUrl = url,
                RefCount = 1
            };
            if (hwnd != IntPtr.Zero)
                _sharedStreams[hwnd] = shared;
            Msg($"[RemoteStream] Created new shared stream {streamId} for hwnd={hwnd}");
            return shared;
        }
    }

    private static Action CreateAudioStartAction(AudioCapture audio, IntPtr hwnd)
    {
        int started = 0;
        return () =>
        {
            if (audio == null || audio.IsCapturing)
                return;
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            try
            {
                if (hwnd != IntPtr.Zero)
                    audio.Start(hwnd, AudioCaptureMode.IncludeProcess);
                else
                    audio.Start(IntPtr.Zero, AudioCaptureMode.ExcludeProcess);
            }
            catch (Exception ex)
            {
                Msg($"[AudioCapture] Async start error: {ex}");
            }
        };
    }

    private static AudioCapture GetSharedStreamAudio(IntPtr hwnd)
    {
        lock (_sharedStreams)
        {
            return _sharedStreams.TryGetValue(hwnd, out var shared) ? shared.Audio : null;
        }
    }

    private static Action GetSharedStreamAudioStart(IntPtr hwnd)
    {
        lock (_sharedStreams)
        {
            return _sharedStreams.TryGetValue(hwnd, out var shared) ? shared.StartAudio : null;
        }
    }

    private static FfmpegEncoder GetSharedStreamEncoder(IntPtr hwnd)
    {
        lock (_sharedStreams)
        {
            return _sharedStreams.TryGetValue(hwnd, out var shared) ? shared.Encoder : null;
        }
    }

    private static bool TryClaimSharedStreamDriver(IntPtr hwnd, int streamId, DesktopSession session)
    {
        lock (_sharedStreams)
        {
            if (!_sharedStreams.TryGetValue(hwnd, out var shared) || shared.StreamId != streamId)
                return true;

            bool shouldDriveEncoder = shared.DriverSession == null ||
                shared.DriverSession.Cleaned ||
                shared.DriverSession.Streamer == null;
            if (shouldDriveEncoder)
                shared.DriverSession = session;
            return shouldDriveEncoder;
        }
    }

    private static bool TryTransferSharedStreamDriver(
        DesktopSession session,
        out DesktopSession replacementDriver,
        out FfmpegEncoder replacementEncoder)
    {
        replacementDriver = null;
        replacementEncoder = null;

        if (session.StreamId <= 0)
            return false;

        lock (_sharedStreams)
        {
            if (!_sharedStreams.TryGetValue(session.Hwnd, out var shared) ||
                shared.StreamId != session.StreamId ||
                shared.DriverSession != session)
            {
                return false;
            }

            shared.DriverSession = null;
            foreach (var candidate in ActiveSessions)
            {
                if (candidate == session ||
                    candidate.Cleaned ||
                    candidate.Hwnd != session.Hwnd ||
                    candidate.StreamId != session.StreamId ||
                    candidate.Streamer == null)
                {
                    continue;
                }

                replacementDriver = candidate;
                replacementEncoder = shared.Encoder;
                shared.DriverSession = candidate;
                return true;
            }
        }

        return false;
    }

    private static void UpdateSharedStreamAfterResize(IntPtr hwnd, int newStreamId, FfmpegEncoder newEncoder, Uri newUrl)
    {
        lock (_sharedStreams)
        {
            if (_sharedStreams.TryGetValue(hwnd, out var shared))
            {
                shared.StreamId = newStreamId;
                shared.Encoder = newEncoder;
                if (newUrl != null)
                    shared.StreamUrl = newUrl;
            }
        }
    }

    private static bool ReleaseSharedStreamReference(
        IntPtr hwnd,
        int streamId,
        DesktopSession session,
        out AudioCapture audioToDispose,
        out FfmpegEncoder encoderToDispose)
    {
        audioToDispose = null;
        encoderToDispose = null;

        lock (_sharedStreams)
        {
            if (_sharedStreams.TryGetValue(hwnd, out var shared) && shared.StreamId == streamId)
            {
                shared.RefCount--;
                Msg($"[Cleanup:BG] Stream {shared.StreamId} refs now {shared.RefCount}");
                if (shared.RefCount <= 0)
                {
                    _sharedStreams.Remove(hwnd);
                    audioToDispose = shared.Audio;
                    encoderToDispose = shared.Encoder;
                    return true;
                }

                if (shared.DriverSession == session)
                    shared.DriverSession = null;
                return false;
            }

            encoderToDispose = session.Encoder;
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace DesktopBuddy;

internal sealed class SharedStream
{
    public int StreamId;
    public FfmpegEncoder Encoder;
    public ILiveStreamSource Source;
    public bool UsesMediaMtx;
    public AudioCapture Audio;
    public Action StartAudio;
    public Uri StreamUrl;
    public int RefCount;
    public DesktopSession DriverSession;
}

internal static class SharedStreamRegistry
{
    internal static readonly Dictionary<IntPtr, SharedStream> Streams = new();

    private static int _nextStreamId;

    internal static int NextStreamId() => Interlocked.Increment(ref _nextStreamId);
}

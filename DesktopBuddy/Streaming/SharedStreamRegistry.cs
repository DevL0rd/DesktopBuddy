using System;
using System.Collections.Generic;
using System.Threading;

namespace DesktopBuddy;

internal sealed class SharedStream
{
    public int StreamId;
    public FfmpegEncoder Encoder;
    public AudioCapture Audio;
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

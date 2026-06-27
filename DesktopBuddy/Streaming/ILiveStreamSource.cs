using System;
using System.Threading.Tasks;

namespace DesktopBuddy;

public interface ILiveStreamSource : IDisposable
{
    bool IsRunning { get; }
    long CurrentWritePosition { get; }
    string ReadableStreamState { get; }

    bool IsSourceAlive => true;

    bool HasReadableVideoKeyframeAtOrAfter(long minimumKeyframePos);
    int ReadStream(byte[] buffer, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool keyframeAligned);
    Task WaitForDataAsync(int milliseconds);
    string GetReaderDiagnostics(long readPos, bool aligned);
    void Stop();
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
    {
        return _dataAvailable.WaitAsync(Math.Max(1, timeoutMs));
    }

    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool startsAtKeyframe)
    {
        startsAtKeyframe = false;
        lock (_ringLock)
        {
            long available = _ringWritePos - readPos;
            if (available <= 0) return 0;

            long latestKeyframePos = Interlocked.Read(ref _lastKeyframeRingPos);
            if (!aligned)
            {
                long kfPos = latestKeyframePos;
                if (kfPos >= minimumKeyframePos && kfPos >= 0 && kfPos >= _ringWritePos - RING_SIZE && kfPos < _ringWritePos)
                {
                    readPos = kfPos;
                    available = _ringWritePos - readPos;
                    aligned = true;
                    Log.Msg($"[FfmpegEnc:{_streamId}] Reader aligned to latest keyframe at ringPos={readPos}, liveWritePos={_ringWritePos}, backlog={available} bytes");
                }
                if (!aligned) return 0;
            }

            available = _ringWritePos - readPos;
            if (available > RING_SIZE)
            {
                RecordReaderOverrun(readPos, _ringWritePos, available);
                return -1;
            }

            long maxLiveBacklogBytes = GetMaxHttpLiveBacklogBytes();
            if (aligned && available > maxLiveBacklogBytes)
            {
                long kfPos = latestKeyframePos;
                if (kfPos > readPos && kfPos >= minimumKeyframePos && kfPos >= _ringWritePos - RING_SIZE && kfPos < _ringWritePos)
                {
                    long dropped = kfPos - readPos;
                    readPos = kfPos;
                    available = _ringWritePos - readPos;
                    startsAtKeyframe = true;
                    RecordReaderLiveCatchup(dropped, available, maxLiveBacklogBytes, readPos);
                }
                else
                {
                    return 0;
                }
            }

            int toRead = (int)Math.Min(available, buffer.Length);
            startsAtKeyframe = latestKeyframePos >= 0 && readPos == latestKeyframePos;

            int ringPos = (int)(readPos % RING_SIZE);
            int firstChunk = Math.Min(toRead, RING_SIZE - ringPos);
            Buffer.BlockCopy(_ringBuffer, ringPos, buffer, 0, firstChunk);
            if (firstChunk < toRead)
                Buffer.BlockCopy(_ringBuffer, 0, buffer, firstChunk, toRead - firstChunk);
            readPos += toRead;
            return toRead;
        }
    }


    private static long GetMaxHttpLiveBacklogBytes()
    {
        long bitrate = Math.Max(1, DesktopBuddyMod.RuntimeBitrateMbps) * 1_000_000L;
        long halfSecondBytes = bitrate / 16;
        return Math.Clamp(halfSecondBytes, 256 * 1024L, 2 * 1024 * 1024L);
    }

    private void RecordReaderLiveCatchup(long droppedBytes, long remainingBytes, long maxBacklogBytes, long readPos)
    {
        long events = Interlocked.Increment(ref _readerLiveCatchupEvents);
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long lastTicks = Interlocked.Read(ref _readerLastCatchupLogTicks);
        bool shouldLog = events == 1 ||
            lastTicks == 0 ||
            (double)(nowTicks - lastTicks) / System.Diagnostics.Stopwatch.Frequency >= 30.0;

        if (!shouldLog)
            return;

        Interlocked.Exchange(ref _readerLastCatchupLogTicks, nowTicks);
        Log.Msg($"[FfmpegEnc:{_streamId}] Reader live catch-up summary: dropped={droppedBytes}B remaining={remainingBytes}B max={maxBacklogBytes}B events={events}");
    }


    private void RecordReaderOverrun(long readPos, long liveWritePos, long backlogBytes)
    {
        DesktopBuddyMod.Perf.IncrementCounter("stream_reader_overrun");
        Interlocked.Increment(ref _readerOverrunEvents);
        UpdateMax(ref _readerOverrunMaxBacklogBytes, backlogBytes);

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long previousLogTicks = Interlocked.Read(ref _readerLastOverrunLogTicks);
        if (previousLogTicks != 0)
        {
            double elapsedMs = (double)(nowTicks - previousLogTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMs < 5000.0) return;
        }

        if (Interlocked.CompareExchange(ref _readerLastOverrunLogTicks, nowTicks, previousLogTicks) != previousLogTicks)
            return;

        long events = Interlocked.Exchange(ref _readerOverrunEvents, 0);
        long maxBacklog = Interlocked.Exchange(ref _readerOverrunMaxBacklogBytes, 0);
        Log.Msg($"[FfmpegEnc:{_streamId}] Reader overrun summary: events={events}, maxBacklog={maxBacklog} bytes, ringSize={RING_SIZE} bytes, lastReadPos={readPos}, liveWritePos={liveWritePos}, backlog={backlogBytes} bytes; closing reader for clean reconnect");
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

}

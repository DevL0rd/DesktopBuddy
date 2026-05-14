using System;
using System.Threading;

namespace DesktopBuddy;

internal static class AudioRingBuffer
{
    internal static void WriteLatest(Span<float> samples, float[] ring, ref long writePosition)
    {
        int ringSize = ring.Length;
        long wp = Volatile.Read(ref writePosition);
        int toWrite = Math.Min(samples.Length, ringSize);
        int offset = (int)(wp % ringSize);
        int first = Math.Min(toWrite, ringSize - offset);

        samples.Slice(0, first).CopyTo(ring.AsSpan(offset, first));
        if (first < toWrite)
            samples.Slice(first, toWrite - first).CopyTo(ring.AsSpan(0, toWrite - first));

        Volatile.Write(ref writePosition, wp + toWrite);
    }

    internal static int Read(float[] ring, long writePosition, ref long readPosition, float[] output, int maxSamples)
    {
        long available = ClampReadPosition(ring, writePosition, ref readPosition);
        if (available <= 0) return 0;

        int toRead = (int)Math.Min(available, maxSamples);
        int ringSize = ring.Length;
        int offset = (int)(readPosition % ringSize);
        int first = Math.Min(toRead, ringSize - offset);
        Array.Copy(ring, offset, output, 0, first);
        if (first < toRead)
            Array.Copy(ring, 0, output, first, toRead - first);
        readPosition += toRead;
        return toRead;
    }

    internal static int Read(float[] ring, long writePosition, ref long readPosition, Span<float> output)
    {
        long available = ClampReadPosition(ring, writePosition, ref readPosition);
        if (available <= 0) return 0;

        int toRead = (int)Math.Min(available, output.Length);
        int ringSize = ring.Length;
        int offset = (int)(readPosition % ringSize);
        int first = Math.Min(toRead, ringSize - offset);
        ring.AsSpan(offset, first).CopyTo(output);
        if (first < toRead)
            ring.AsSpan(0, toRead - first).CopyTo(output.Slice(first));
        readPosition += toRead;
        return toRead;
    }

    private static long ClampReadPosition(float[] ring, long writePosition, ref long readPosition)
    {
        long available = writePosition - readPosition;
        if (available > ring.Length)
        {
            readPosition = writePosition - ring.Length;
            available = ring.Length;
        }

        return available;
    }
}

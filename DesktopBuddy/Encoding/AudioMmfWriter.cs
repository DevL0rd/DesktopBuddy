using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DesktopBuddy;

/// <summary>
/// Game-side writer that copies PCM samples from AudioCapture's ring buffer
/// into a shared MMF so the renderer-side AudioBridge can read them.
/// </summary>
internal sealed class AudioMmfWriter : IDisposable
{
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private readonly AudioCapture _capture;
    private long _readPos;
    private Thread _pumpThread;
    private volatile bool _disposed;
    private readonly float[] _pumpBuffer;

    public AudioMmfWriter(AudioCapture capture, string queueName, int slot)
    {
        _capture = capture;
        _pumpBuffer = new float[4096];

        string mmfName = CaptureSessionProtocol.GetAudioMmfName(queueName, slot);
        _mmf = MemoryMappedFile.CreateOrOpen(mmfName, CaptureSessionProtocol.AudioMmfSize);
        _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.AudioMmfSize, MemoryMappedFileAccess.ReadWrite);

        // Zero out header
        _accessor.Write(0, 0L); // writePos
        _accessor.Write(8, 0L); // readPos

        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = $"AudioMmfWriter:{slot}" };
        _pumpThread.Start();
        Log.Msg($"[AudioMmfWriter] Started for slot {slot}, MMF: {mmfName}");
    }

    private void PumpLoop()
    {
        long mmfWritePos = 0;
        int ringSize = CaptureSessionProtocol.AudioRingSamples;

        while (!_disposed)
        {
            Thread.Sleep(5); // ~200Hz pump rate
            if (_disposed) break;

            int read = _capture.ReadSamples(_pumpBuffer, _pumpBuffer.Length, ref _readPos);
            if (read <= 0) continue;

            int ringOffset = (int)(mmfWritePos % ringSize);
            int headerOffset = CaptureSessionProtocol.AudioHeaderSize;

            int firstChunk = Math.Min(read, ringSize - ringOffset);
            _accessor.WriteArray(headerOffset + ringOffset * 4, _pumpBuffer, 0, firstChunk);
            if (firstChunk < read)
                _accessor.WriteArray(headerOffset, _pumpBuffer, firstChunk, read - firstChunk);

            mmfWritePos += read;
            _accessor.Write(0, mmfWritePos); // Update writePos atomically
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpThread?.Join(2000);
        _accessor?.Dispose();
        _mmf?.Dispose();
        _accessor = null;
        _mmf = null;
        Log.Msg("[AudioMmfWriter] Disposed");
    }
}

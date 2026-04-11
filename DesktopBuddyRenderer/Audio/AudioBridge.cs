using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using BepInEx.Logging;
using DesktopBuddy.Shared;

namespace DesktopBuddyRenderer
{
    /// <summary>
    /// Renderer-side audio bridge. Reads PCM float32 samples from a shared MMF
    /// written by the game-side AudioCapture.
    ///
    /// MMF layout (AudioMmfSize bytes):
    ///   [0..7]   writePos  (long) — total samples written by game
    ///   [8..15]  readPos   (long) — total samples read by renderer (for flow control)
    ///   [16..]   float32 ring buffer (AudioRingSamples entries)
    /// </summary>
    public sealed class AudioBridge : IDisposable
    {
        private static ManualLogSource Log => DesktopBuddyRendererPlugin.Log;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private long _readPos;
        private volatile bool _disposed;
        private readonly string _mmfName;

        public bool IsOpen => _accessor != null && !_disposed;
        public int SampleRate => 48000;
        public int Channels => 2;

        public AudioBridge(string queuePrefix, int slot)
        {
            _mmfName = CaptureSessionProtocol.GetAudioMmfName(queuePrefix + "Primary", slot);
        }

        /// <summary>
        /// Try to open the shared MMF. Returns true if already open or successfully opened.
        /// Returns false if the game hasn't created the MMF yet (FileNotFoundException).
        /// </summary>
        public bool TryOpen()
        {
            if (_accessor != null) return true;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(_mmfName);
                _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.AudioMmfSize, MemoryMappedFileAccess.ReadWrite);
                _readPos = 0;
                Log.LogInfo($"[AudioBridge] Opened MMF: {_mmfName}");
                return true;
            }
            catch (System.IO.FileNotFoundException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[AudioBridge] Failed to open MMF {_mmfName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read interleaved float32 PCM samples from the shared ring buffer.
        /// Called by FfmpegEncoder's audio encode loop.
        /// </summary>
        public int ReadSamples(float[] output, int maxSamples)
        {
            if (_accessor == null || _disposed) return 0;

            long writePos = _accessor.ReadInt64(0);
            long available = writePos - _readPos;
            if (available <= 0) return 0;

            int ringSize = CaptureSessionProtocol.AudioRingSamples;
            if (available > ringSize)
            {
                // We fell behind — skip to latest data minus a small buffer
                _readPos = writePos - ringSize;
                available = ringSize;
            }

            int toRead = (int)Math.Min(available, maxSamples);
            int ringOffset = (int)(_readPos % ringSize);
            int headerOffset = CaptureSessionProtocol.AudioHeaderSize;

            int firstChunk = Math.Min(toRead, ringSize - ringOffset);
            _accessor.ReadArray(headerOffset + ringOffset * 4, output, 0, firstChunk);
            if (firstChunk < toRead)
                _accessor.ReadArray(headerOffset, output, firstChunk, toRead - firstChunk);

            _readPos += toRead;
            // Write our read position back so the game can track consumer progress
            _accessor.Write(8, _readPos);

            return toRead;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _accessor?.Dispose();
            _mmf?.Dispose();
            _accessor = null;
            _mmf = null;
        }
    }
}

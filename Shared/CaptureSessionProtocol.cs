using System;
using InterprocessLib;
using Renderite.Shared;

namespace DesktopBuddy.Shared
{
    internal static class CaptureSessionProtocol
    {
        public const string OwnerId = "DesktopBuddy.Capture";
        public const string QueueName = "DesktopBuddy.Capture";
        public const string StartMessageId = "Start";
        public const string StopMessageId = "Stop";
        public const string RunningMessageId = "Running";

        public const int MaxSessions = 4096;
        public const int MagicIndexBase = 10000;
    }

    internal sealed class SimpleMemoryPackerPool : IMemoryPackerEntityPool
    {
        public static readonly SimpleMemoryPackerPool Instance = new();

        private SimpleMemoryPackerPool()
        {
        }

        T IMemoryPackerEntityPool.Borrow<T>() => new T();

        void IMemoryPackerEntityPool.Return<T>(T value)
        {
        }
    }

    internal sealed class CaptureStartMessage : IMemoryPackable
    {
        public int SessionId;
        public long Hwnd;
        public long MonitorHandle;
        public bool UseLegacyUwc;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SessionId);
            packer.Write(Hwnd);
            packer.Write(MonitorHandle);
            packer.Write(UseLegacyUwc);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SessionId);
            unpacker.Read(ref Hwnd);
            unpacker.Read(ref MonitorHandle);
            unpacker.Read(ref UseLegacyUwc);
        }
    }

    internal sealed class CaptureStopMessage : IMemoryPackable
    {
        public int SessionId;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SessionId);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SessionId);
        }
    }

    internal sealed class CaptureRunningMessage : IMemoryPackable
    {
        public int SessionId;
        public int Width;
        public int Height;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SessionId);
            packer.Write(Width);
            packer.Write(Height);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SessionId);
            unpacker.Read(ref Width);
            unpacker.Read(ref Height);
        }
    }
}

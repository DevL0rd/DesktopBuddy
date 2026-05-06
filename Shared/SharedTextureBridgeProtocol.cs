using InterprocessLib;
using Renderite.Shared;

namespace DesktopBuddy.Shared
{
    internal static class SharedTextureBridgeProtocol
    {
        public const string OwnerId = "DesktopBuddy.SharedTexture";
        public const string QueueName = "DesktopBuddy.SharedTexture";
        public const string StartMessageId = "Start";
        public const string StopMessageId = "Stop";
        public const string RunningMessageId = "Running";

        public const int MaxTextureSlots = 4096;
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

    internal sealed class SharedTextureStartMessage : IMemoryPackable
    {
        public int SlotId;
        public long SharedTextureHandle;
        public int SharedTextureWidth;
        public int SharedTextureHeight;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(SharedTextureHandle);
            packer.Write(SharedTextureWidth);
            packer.Write(SharedTextureHeight);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref SharedTextureHandle);
            unpacker.Read(ref SharedTextureWidth);
            unpacker.Read(ref SharedTextureHeight);
        }
    }

    internal sealed class SharedTextureStopMessage : IMemoryPackable
    {
        public int SlotId;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
        }
    }

    internal sealed class SharedTextureRunningMessage : IMemoryPackable
    {
        public int SlotId;
        public int Width;
        public int Height;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(Width);
            packer.Write(Height);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref Width);
            unpacker.Read(ref Height);
        }
    }
}

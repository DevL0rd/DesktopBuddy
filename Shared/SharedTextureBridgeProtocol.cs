using System;
using InterprocessLib;
using Renderite.Shared;

namespace DesktopBuddy.Shared
{
    internal static class SharedTextureBridgeProtocol
    {
        private const string BaseOwnerId = "DesktopBuddy.SharedTexture.v2";
        private const string BaseQueueName = "DesktopBuddy.SharedTexture.v2";

        private static readonly string QueueScope = GetQueueScope();

        public static readonly string OwnerId = GetScopedName(BaseOwnerId);
        public static readonly string QueueName = GetScopedName(BaseQueueName);

        public const string StartMessageId = "Start";
        public const string StopMessageId = "Stop";
        public const string RunningMessageId = "Running";
        public const string StoppedMessageId = "Stopped";
        public const string RendererDeviceMessageId = "RendererDevice";

        public const int MaxTextureSlots = 4096;
        public const int MagicIndexBase = 10000;

        private static string GetScopedName(string baseName)
        {
            return string.IsNullOrEmpty(QueueScope) ? baseName : baseName + "." + QueueScope;
        }

        private static string GetQueueScope()
        {
            string shmprefix = GetArgumentValue("-shmprefix") ?? GetArgumentValue("--shmprefix");
            if (string.IsNullOrWhiteSpace(shmprefix))
                return string.Empty;

            return "shm" + ComputeStableHash(shmprefix).ToString("X16");
        }

        private static string GetArgumentValue(string name)
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                    continue;

                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        return args[i + 1];
                    return null;
                }

                string prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(prefix.Length);
            }

            return null;
        }

        private static ulong ComputeStableHash(string value)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= prime;
                }
                return hash;
            }
        }
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
        public int Generation;
        public long SharedTextureHandle;
        public string SharedTextureName;
        public int SharedTextureWidth;
        public int SharedTextureHeight;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(Generation);
            packer.Write(SharedTextureHandle);
            packer.Write(SharedTextureName);
            packer.Write(SharedTextureWidth);
            packer.Write(SharedTextureHeight);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref Generation);
            unpacker.Read(ref SharedTextureHandle);
            unpacker.Read(ref SharedTextureName);
            unpacker.Read(ref SharedTextureWidth);
            unpacker.Read(ref SharedTextureHeight);
        }
    }


    internal sealed class SharedTextureStopMessage : IMemoryPackable
    {
        public int SlotId;
        public int Generation;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(Generation);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref Generation);
        }
    }

    internal sealed class SharedTextureRunningMessage : IMemoryPackable
    {
        public int SlotId;
        public int Generation;
        public int Width;
        public int Height;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(Generation);
            packer.Write(Width);
            packer.Write(Height);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref Generation);
            unpacker.Read(ref Width);
            unpacker.Read(ref Height);
        }
    }

    internal sealed class SharedTextureStoppedMessage : IMemoryPackable
    {
        public int SlotId;
        public int Generation;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(SlotId);
            packer.Write(Generation);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref SlotId);
            unpacker.Read(ref Generation);
        }
    }

    internal sealed class SharedTextureRendererDeviceMessage : IMemoryPackable
    {
        public long AdapterLuid;
        public int VendorId;
        public string Description;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(AdapterLuid);
            packer.Write(VendorId);
            packer.Write(Description);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref AdapterLuid);
            unpacker.Read(ref VendorId);
            unpacker.Read(ref Description);
        }
    }
}

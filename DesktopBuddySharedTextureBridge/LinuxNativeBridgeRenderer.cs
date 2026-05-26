using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class LinuxNativeBridgeRenderer : IDisposable
    {
        private IntPtr _module;
        private DesktopBuddyLinuxBridgeCallDelegate _call;

        internal bool TryLoad()
        {
            if (_call != null) return true;

            string path = ResolveBridgePath();
            _module = LoadLibraryA(path);
            if (_module == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning($"[LinuxBridge] LoadLibrary failed path={path} err=0x{Marshal.GetLastWin32Error():X8}");
                return false;
            }

            IntPtr proc = GetProcAddress(_module, "DesktopBuddyLinuxBridgeCall");
            if (proc == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning($"[LinuxBridge] GetProcAddress failed err=0x{Marshal.GetLastWin32Error():X8}");
                return false;
            }

            _call = (DesktopBuddyLinuxBridgeCallDelegate)Marshal.GetDelegateForFunctionPointer(proc, typeof(DesktopBuddyLinuxBridgeCallDelegate));
            SharedTextureBridgePlugin.LogInfo($"[LinuxBridge] Loaded {path}");
            return true;
        }

        internal int StartGpu(uint nodeId, ulong[] modifiers)
        {
            if (!TryLoad()) return -1;
            if (modifiers == null || modifiers.Length == 0)
            {
                var call = new DbLinuxBridgeCall { Op = 6, Buffer = nodeId };
                return _call(ref call);
            }

            var handle = GCHandle.Alloc(modifiers, GCHandleType.Pinned);
            try
            {
                var call = new DbLinuxBridgeCall
                {
                    Op = 6,
                    Modifiers = (ulong)handle.AddrOfPinnedObject().ToInt64(),
                    ModifierCount = checked((uint)modifiers.Length),
                    Buffer = nodeId
                };
                return _call(ref call);
            }
            finally { handle.Free(); }
        }

        internal int PollFrame(out DbLinuxFrame frame)
        {
            frame = default;
            if (_call == null) return -1;
            var call = new DbLinuxBridgeCall { Op = 2 };
            int status = _call(ref call);
            frame = call.Frame;
            return status;
        }

        internal void Stop()
        {
            if (_call == null) return;
            var call = new DbLinuxBridgeCall { Op = 3 };
            _call(ref call);
        }

        private static string ResolveBridgePath()
        {
            string dir = Path.GetDirectoryName(typeof(SharedTextureBridgePlugin).Assembly.Location) ?? string.Empty;
            return Path.Combine(dir, "DesktopBuddyLinuxBridge.so");
        }

        public void Dispose()
        {
            Stop();
            _call = null;
            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DesktopBuddyLinuxBridgeCallDelegate(ref DbLinuxBridgeCall call);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibraryA(string fileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DbLinuxFrame
    {
        public int Status;
        public int Fd;
        public uint Width;
        public uint Height;
        public uint Fourcc;
        public uint Offset;
        public int Stride;
        public ulong Modifier;
        public uint HasModifier;
        public uint PlaneCount;
        public uint MouseValid;
        public float MouseX;
        public float MouseY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DbLinuxBridgeCall
    {
        public uint Op;
        public int Status;
        public ulong Modifiers;
        public uint ModifierCount;
        public uint Reserved;
        public DbLinuxFrame Frame;
        public ulong Buffer;
    }
}

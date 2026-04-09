using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Renderite.Shared;

namespace DesktopBuddy;

internal sealed class VirtualCamera : IDisposable
{
    private IntPtr _camera;
    private int _width, _height;
    private byte[] _bgrBuffer;
    private GCHandle _pinnedBgr;
    private volatile bool _disposed;
    private Thread _connectionThread;
    internal bool _logNextFrame = true;

    private const int IdleWidth = 1280;
    private const int IdleHeight = 720;

    internal bool ConsumerConnected { get; private set; }

    internal volatile bool ManuallyDisabled;

    internal bool IsActive => _camera != IntPtr.Zero;

    internal bool StartIdle()
    {
        if (_camera != IntPtr.Zero) return true;

        try
        {
            _camera = SoftCamInterop.scCreateCamera(IdleWidth, IdleHeight, 0f);
            if (_camera == IntPtr.Zero)
            {
                Log.Msg("[VirtualCamera] scCreateCamera returned null (another instance running?)");
                return false;
            }
            _width = IdleWidth;
            _height = IdleHeight;
            AllocBuffer(IdleWidth, IdleHeight);
            Log.Msg($"[VirtualCamera] Idle camera created: {IdleWidth}x{IdleHeight}");

            _connectionThread = new Thread(ConnectionPollLoop) { Name = "VirtualCamera:Poll", IsBackground = true };
            _connectionThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] StartIdle failed: {ex.Message}");
            return false;
        }
    }

    private void ConnectionPollLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(500);
            if (_disposed || _camera == IntPtr.Zero) break;

            try
            {
                ConsumerConnected = SoftCamInterop.scIsConnected(_camera);
            }
            catch { ConsumerConnected = false; }
        }
    }

    internal void SendFrame(Span<byte> pixelData, int srcWidth, int srcHeight, TextureFormat format)
    {
        if (_disposed || pixelData.Length == 0 || _camera == IntPtr.Zero) return;

        int targetW = srcWidth & ~3;
        int targetH = srcHeight & ~3;
        if (targetW < 4 || targetH < 4) return;

        if (targetW != _width || targetH != _height)
        {
            Log.Msg($"[VirtualCamera] Resize {_width}x{_height} -> {targetW}x{targetH}");
            SoftCamInterop.scDeleteCamera(_camera);
            _camera = SoftCamInterop.scCreateCamera(targetW, targetH, 0f);
            if (_camera == IntPtr.Zero) { Log.Msg("[VirtualCamera] Resize failed"); return; }
            _width = targetW;
            _height = targetH;
            AllocBuffer(targetW, targetH);
            _logNextFrame = true;
        }

        unsafe
        {
            fixed (byte* srcPtr = pixelData)
            fixed (byte* dstPtr = _bgrBuffer)
            {
                ConvertToBgr24(srcPtr, dstPtr, srcWidth, srcHeight, format);
            }
        }

        try
        {
            SoftCamInterop.scSendFrame(_camera, _pinnedBgr.AddrOfPinnedObject());
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] scSendFrame error: {ex.Message}");
        }
    }

    private unsafe void ConvertToBgr24(byte* src, byte* dst, int w, int h, TextureFormat format)
    {
        int dstW = _width;
        int dstH = _height;
        int dstStride = dstW * 3;
        int bpp = format == TextureFormat.RGB24 ? 3 : 4;
        int srcStride = w * bpp;

        for (int y = 0; y < dstH; y++)
        {
            byte* srcRow = src + (dstH - 1 - y) * srcStride;
            byte* dstRow = dst + y * dstStride;

            if (format == TextureFormat.ARGB32)
            {
                for (int x = 0; x < dstW; x++)
                {
                    byte* s = srcRow + x * 4;
                    byte* d = dstRow + x * 3;
                    d[0] = s[3];
                    d[1] = s[2];
                    d[2] = s[1];
                }
            }
            else if (format == TextureFormat.BGRA32)
            {
                for (int x = 0; x < dstW; x++)
                {
                    byte* s = srcRow + x * 4;
                    byte* d = dstRow + x * 3;
                    d[0] = s[0];
                    d[1] = s[1];
                    d[2] = s[2];
                }
            }
            else
            {
                int x = 0;
                if (Ssse3.IsSupported && bpp == 4)
                {
                    var mask = Vector128.Create(
                        (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);

                    for (; x + 4 <= dstW; x += 4)
                    {
                        var rgba = Sse2.LoadVector128(srcRow + x * 4);
                        var bgr = Ssse3.Shuffle(rgba, mask);
                        *(long*)(dstRow + x * 3) = bgr.AsInt64().GetElement(0);
                        *(int*)(dstRow + x * 3 + 8) = bgr.AsInt32().GetElement(2);
                    }
                }
                for (; x < dstW; x++)
                {
                    byte* s = srcRow + x * bpp;
                    byte* d = dstRow + x * 3;
                    d[0] = s[2];
                    d[1] = s[1];
                    d[2] = s[0];
                }
            }
        }
    }

    private void AllocBuffer(int w, int h)
    {
        if (_pinnedBgr.IsAllocated) _pinnedBgr.Free();
        _bgrBuffer = new byte[w * h * 3];
        _pinnedBgr = GCHandle.Alloc(_bgrBuffer, GCHandleType.Pinned);
    }

    internal void Stop()
    {
        ManuallyDisabled = true;
        Log.Msg("[VirtualCamera] Rendering disabled");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_camera != IntPtr.Zero)
        {
            try { SoftCamInterop.scDeleteCamera(_camera); }
            catch (Exception ex) { Log.Msg($"[VirtualCamera] scDeleteCamera error: {ex.Message}"); }
            _camera = IntPtr.Zero;
        }
        if (_pinnedBgr.IsAllocated) _pinnedBgr.Free();
        _bgrBuffer = null;
        Log.Msg("[VirtualCamera] Disposed");
    }
}

using System;
using System.Runtime.InteropServices;
using Renderite.Shared;

namespace DesktopBuddy;

internal sealed class VirtualCamera : IDisposable
{
    private IntPtr _camera;
    private int _width, _height;
    private byte[] _bgrBuffer;
    private GCHandle _pinnedBgr;
    private volatile bool _disposed;
    internal bool _logNextFrame = true;

    internal bool IsActive => _camera != IntPtr.Zero;

    internal bool Start(int width, int height, float fps = 0f)
    {
        if (_camera != IntPtr.Zero) Stop();

        // SoftCam requires both dimensions to be multiples of 4
        width = width & ~3;
        height = height & ~3;
        if (width < 4 || height < 4) return false;

        try
        {
            _camera = SoftCamInterop.scCreateCamera(width, height, fps);
            if (_camera == IntPtr.Zero)
            {
                Log.Msg("[VirtualCamera] scCreateCamera returned null (another instance running?)");
                return false;
            }
            _width = width;
            _height = height;
            AllocBuffer(width, height);
            _logNextFrame = true;
            Log.Msg($"[VirtualCamera] Started: {width}x{height}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] Start failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends a frame to the virtual camera. Accepts a Span directly from Bitmap2D.RawData
    /// to avoid an intermediate array copy. Converts to BGR24 in-place using unsafe pointers.
    /// </summary>
    internal void SendFrame(Span<byte> pixelData, int srcWidth, int srcHeight, TextureFormat format)
    {
        if (_disposed || pixelData.Length == 0) return;

        int targetW = srcWidth & ~3;
        int targetH = srcHeight & ~3;

        if (_camera == IntPtr.Zero || targetW != _width || targetH != _height)
        {
            if (_camera != IntPtr.Zero)
                Log.Msg($"[VirtualCamera] Resize {_width}x{_height} -> {targetW}x{targetH}");
            Stop();
            if (!Start(targetW, targetH)) return;
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
                    d[0] = s[3]; // B
                    d[1] = s[2]; // G
                    d[2] = s[1]; // R
                }
            }
            else if (format == TextureFormat.BGRA32)
            {
                for (int x = 0; x < dstW; x++)
                {
                    byte* s = srcRow + x * 4;
                    byte* d = dstRow + x * 3;
                    d[0] = s[0]; // B
                    d[1] = s[1]; // G
                    d[2] = s[2]; // R
                }
            }
            else // RGBA32 and others
            {
                for (int x = 0; x < dstW; x++)
                {
                    byte* s = srcRow + x * bpp;
                    byte* d = dstRow + x * 3;
                    d[0] = s[2]; // B
                    d[1] = s[1]; // G
                    d[2] = s[0]; // R
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
        if (_camera != IntPtr.Zero)
        {
            try { SoftCamInterop.scDeleteCamera(_camera); }
            catch (Exception ex) { Log.Msg($"[VirtualCamera] scDeleteCamera error: {ex.Message}"); }
            _camera = IntPtr.Zero;
            Log.Msg("[VirtualCamera] Stopped");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_pinnedBgr.IsAllocated) _pinnedBgr.Free();
        _bgrBuffer = null;
    }
}

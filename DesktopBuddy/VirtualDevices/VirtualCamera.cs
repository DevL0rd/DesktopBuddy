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
            // fps=0 means variable rate — we push frames as they arrive, no internal sleep
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

    internal void SendFrame(byte[] pixelData, int srcWidth, int srcHeight, TextureFormat format = TextureFormat.RGBA32)
    {
        if (_disposed || pixelData == null) return;

        int targetW = srcWidth & ~3;
        int targetH = srcHeight & ~3;

        // Auto-start on first frame, or restart if size changed
        if (_camera == IntPtr.Zero || targetW != _width || targetH != _height)
        {
            if (_camera != IntPtr.Zero)
                Log.Msg($"[VirtualCamera] Resize {_width}x{_height} -> {targetW}x{targetH}");
            Stop();
            if (!Start(targetW, targetH)) return;
        }

        ConvertToBgr24(pixelData, srcWidth, srcHeight, format);

        try
        {
            SoftCamInterop.scSendFrame(_camera, _pinnedBgr.AddrOfPinnedObject());
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] scSendFrame error: {ex.Message}");
        }
    }

    /// <summary>
    /// Convert pixel data to BGR24 top-down for SoftCam.
    /// Handles RGBA32, BGRA32, and RGB24 source formats.
    /// </summary>
    private void ConvertToBgr24(byte[] src, int w, int h, TextureFormat format)
    {
        int dstW = _width;
        int dstH = _height;
        int dstStride = dstW * 3;

        int bpp = format == TextureFormat.RGB24 ? 3 : 4;
        int srcStride = w * bpp;

        // Flip vertically — RenderToBitmap produces top-down but SoftCam needs bottom-up from sender
        for (int y = 0; y < dstH; y++)
        {
            int srcRow = (dstH - 1 - y) * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < dstW; x++)
            {
                int si = srcRow + x * bpp;
                int di = dstRow + x * 3;

                switch (format)
                {
                    case TextureFormat.ARGB32:
                        // src: A R G B → dst: B G R
                        _bgrBuffer[di] = src[si + 3];     // B
                        _bgrBuffer[di + 1] = src[si + 2]; // G
                        _bgrBuffer[di + 2] = src[si + 1]; // R
                        break;
                    case TextureFormat.BGRA32:
                        // src: B G R A → dst: B G R
                        _bgrBuffer[di] = src[si];
                        _bgrBuffer[di + 1] = src[si + 1];
                        _bgrBuffer[di + 2] = src[si + 2];
                        break;
                    case TextureFormat.RGB24:
                        // src: R G B → dst: B G R
                        _bgrBuffer[di] = src[si + 2];
                        _bgrBuffer[di + 1] = src[si + 1];
                        _bgrBuffer[di + 2] = src[si];
                        break;
                    default: // RGBA32 and others
                        // src: R G B A → dst: B G R
                        _bgrBuffer[di] = src[si + 2];
                        _bgrBuffer[di + 1] = src[si + 1];
                        _bgrBuffer[di + 2] = src[si];
                        break;
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

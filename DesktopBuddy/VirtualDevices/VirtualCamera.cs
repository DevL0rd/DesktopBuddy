using System;
using System.Runtime.InteropServices;
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
    private bool _extendedApiAvailable = true;
    private bool _usingExtendedApi;
    private int _pixelFormat;
    private int _frameFlags;
    internal bool _logNextFrame = true;

    private readonly bool _linux = DesktopBuddyPlatform.IsLinux;
    private readonly LinuxNativeBridge _linuxBridge = DesktopBuddyPlatform.IsLinux ? new LinuxNativeBridge() : null;
    private int _v4l2Fd = -1;

    private const int IdleWidth = 1280;
    private const int IdleHeight = 720;
    private const int SoftcamPixelFormatBgr24 = 0;

    internal bool ConsumerConnected { get; private set; }

    internal volatile bool ManuallyDisabled;

    internal bool IsActive => _camera != IntPtr.Zero;

    internal bool StartIdle()
    {
        if (_camera != IntPtr.Zero) return true;

        if (_linux)
        {
            try
            {
                _v4l2Fd = _linuxBridge.VcamOpen(IdleWidth, IdleHeight);
                if (_v4l2Fd < 0)
                {
                    Log.Msg("[VirtualCamera] v4l2loopback 'DesktopBuddy - Camera' not found; run virtual camera setup in the Devices tab");
                    return false;
                }
                _camera = (IntPtr)1;
                _width = IdleWidth;
                _height = IdleHeight;
                _pixelFormat = SoftcamPixelFormatBgr24;
                AllocBuffer(IdleWidth, IdleHeight);
                Log.Msg($"[VirtualCamera] v4l2 camera opened fd={_v4l2Fd} {IdleWidth}x{IdleHeight}");
                _connectionThread = new Thread(ConnectionPollLoop) { Name = "VirtualCamera:Poll", IsBackground = true };
                _connectionThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Msg($"[VirtualCamera] Linux StartIdle failed: {ex.Message}");
                return false;
            }
        }

        try
        {
            _camera = CreateCamera(IdleWidth, IdleHeight, SoftcamPixelFormatBgr24, 0);
            if (_camera == IntPtr.Zero)
            {
                Log.Msg("[VirtualCamera] scCreateCamera returned null (another instance running?)");
                return false;
            }
            _width = IdleWidth;
            _height = IdleHeight;
            if (!_usingExtendedApi || _pixelFormat == SoftcamPixelFormatBgr24)
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

            if (_linux)
            {

                ConsumerConnected = _v4l2Fd >= 0;
                continue;
            }

            try
            {
                ConsumerConnected = SoftCam.scIsConnected(_camera);
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

        if (_linux)
        {
            if (targetW != _width || targetH != _height)
            {
                _linuxBridge.VcamClose(_v4l2Fd);
                _v4l2Fd = _linuxBridge.VcamOpen(targetW, targetH);
                if (_v4l2Fd < 0) { Log.Msg("[VirtualCamera] v4l2 reopen failed on resize"); _camera = IntPtr.Zero; return; }
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

            int written = _linuxBridge.VcamWrite(_v4l2Fd, _bgrBuffer, _bgrBuffer.Length);
            if (written < 0) Log.Msg("[VirtualCamera] v4l2 write failed");
            return;
        }

        int desiredFormat = SoftcamPixelFormatBgr24;
        int desiredFlags = 0;

        if (targetW != _width || targetH != _height || desiredFormat != _pixelFormat || desiredFlags != _frameFlags)
        {
            Log.Msg($"[VirtualCamera] Resize/reformat {_width}x{_height}/{_pixelFormat}/{_frameFlags} -> {targetW}x{targetH}/{desiredFormat}/{desiredFlags}");
            SoftCam.scDeleteCamera(_camera);
            _camera = CreateCamera(targetW, targetH, desiredFormat, desiredFlags);
            if (_camera == IntPtr.Zero) { Log.Msg("[VirtualCamera] Resize failed"); return; }
            _width = targetW;
            _height = targetH;
            if (!_usingExtendedApi || _pixelFormat == SoftcamPixelFormatBgr24)
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
            SoftCam.scSendFrame(_camera, _pinnedBgr.AddrOfPinnedObject());
        }
        catch (Exception ex)
        {
            Log.Msg($"[VirtualCamera] scSendFrame error: {ex.Message}");
        }
    }

    private IntPtr CreateCamera(int width, int height, int pixelFormat, int frameFlags)
    {
        _pixelFormat = pixelFormat;
        _frameFlags = frameFlags;
        _usingExtendedApi = false;

        if (_extendedApiAvailable)
        {
            try
            {
                IntPtr camera = SoftCam.scCreateCameraEx(width, height, 0f, pixelFormat, frameFlags);
                if (camera != IntPtr.Zero)
                {
                    _usingExtendedApi = true;
                    return camera;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _extendedApiAvailable = false;
            }
            catch (Exception ex)
            {
                Log.Msg($"[VirtualCamera] scCreateCameraEx failed: {ex.Message}");
            }
        }

        _pixelFormat = SoftcamPixelFormatBgr24;
        _frameFlags = 0;
        return SoftCam.scCreateCamera(width, height, 0f);
    }

    private unsafe void ConvertToBgr24(byte* src, byte* dst, int w, int h, TextureFormat format)
    {
        int dstW = _width;
        int dstH = _height;
        int dstStride = dstW * 3;
        int bpp = format == TextureFormat.RGB24 ? 3 : format == TextureFormat.BGR565 ? 2 : 4;
        int srcStride = w * bpp;

        for (int y = 0; y < dstH; y++)
        {

            byte* srcRow = src + (_linux ? y : (dstH - 1 - y)) * srcStride;
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
            else if (format == TextureFormat.BGR565)
            {
                ushort* src565 = (ushort*)srcRow;
                for (int x = 0; x < dstW; x++)
                {
                    ushort p = src565[x];
                    byte* d = dstRow + x * 3;
                    d[0] = (byte)((p & 0x1F) * 255 / 31);
                    d[1] = (byte)(((p >> 5) & 0x3F) * 255 / 63);
                    d[2] = (byte)(((p >> 11) & 0x1F) * 255 / 31);
                }
            }
            else
            {
                for (int x = 0; x < dstW; x++)
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
        if (_linux)
        {
            if (_v4l2Fd >= 0)
            {
                try { _linuxBridge.VcamClose(_v4l2Fd); }
                catch (Exception ex) { Log.Msg($"[VirtualCamera] v4l2 close error: {ex.Message}"); }
                _v4l2Fd = -1;
            }
            try { _linuxBridge?.Dispose(); } catch { }
            _camera = IntPtr.Zero;
        }
        else if (_camera != IntPtr.Zero)
        {
            try { SoftCam.scDeleteCamera(_camera); }
            catch (Exception ex) { Log.Msg($"[VirtualCamera] scDeleteCamera error: {ex.Message}"); }
            _camera = IntPtr.Zero;
        }
        if (_pinnedBgr.IsAllocated) _pinnedBgr.Free();
        _bgrBuffer = null;
        Log.Msg("[VirtualCamera] Disposed");
    }
}

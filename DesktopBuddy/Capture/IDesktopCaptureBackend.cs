using System;

namespace DesktopBuddy;

internal interface IDesktopCaptureBackend : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool IsValid { get; }
    object D3dContextLock { get; }
    IntPtr D3dDevice { get; }
    IntPtr D3dContext { get; }
    IntPtr SharedTexture { get; }
    IntPtr SharedTextureHandle { get; }
    int SharedTextureWidth { get; }
    int SharedTextureHeight { get; }
    bool HasCurrentSharedFrame { get; }
    bool IsResizeRecreatePending { get; }
    Action<IntPtr, IntPtr, int, int> OnGpuFrame { get; set; }

    bool TryInitialCapture();
    void RecreatePoolIfNeeded();
    void FlushD3dContext();
    void StopCapture();
}

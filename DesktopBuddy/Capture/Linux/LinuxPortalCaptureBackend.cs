using System;

namespace DesktopBuddy;

internal interface ILinuxPipeWireSelection
{
    uint PipeWireNodeId { get; }
    ulong InputSessionId { get; }
}

internal sealed class LinuxPortalCaptureBackend : IDesktopCaptureBackend, ILinuxPipeWireSelection
{
    private bool _running;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint PipeWireNodeId { get; private set; }
    public ulong InputSessionId { get; private set; }
    public bool IsValid => _running;
    public object D3dContextLock => null;
    public IntPtr D3dDevice => IntPtr.Zero;
    public IntPtr D3dContext => IntPtr.Zero;
    public IntPtr SharedTexture => IntPtr.Zero;
    public IntPtr SharedTextureHandle => IntPtr.Zero;
    public int SharedTextureWidth => 0;
    public int SharedTextureHeight => 0;
    public bool HasCurrentSharedFrame => false;
    public bool IsResizeRecreatePending => false;
    public Action<IntPtr, IntPtr, int, int> OnGpuFrame { get; set; }

    public bool TryInitialCapture()
    {
        if (!LinuxPortalSelectionStore.TryConsume(out var selection))
        {
            Log.Msg("[LinuxPortalCapture] No selected PipeWire stream is pending");
            return false;
        }

        PipeWireNodeId = selection.NodeId;
        InputSessionId = selection.InputSessionId;
        Width = selection.Width > 0 ? selection.Width : 1280;
        Height = selection.Height > 0 ? selection.Height : 720;
        _running = true;
        Log.Msg($"[LinuxPortalCapture] Using selected PipeWire node={PipeWireNodeId} size={Width}x{Height} inputSession={InputSessionId}");
        return true;
    }

    public void RecreatePoolIfNeeded() { }
    public void FlushD3dContext() { }

    public void StopCapture()
    {
        _running = false;
    }

    public void Dispose()
    {
        _running = false;
    }
}

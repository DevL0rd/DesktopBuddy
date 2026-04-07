using System;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public class DesktopSession
{
    public DesktopStreamer Streamer;
    public SolidColorTexture Texture;
    public RawImage TextureImage;
    public Canvas Canvas;
    public Slot Root;
    public bool UpdateInProgress;
    public bool ManualModeSet;
    public double TimeSinceLastCapture;
    public double TargetInterval;

    public Component LastActiveSource;

    public int LastScrollSign;
    public double LastScrollTick;

    public int StreamId;
    public IntPtr Hwnd;

    public uint ProcessId;
    public double TimeSinceChildCheck;
    public HashSet<IntPtr> TrackedChildHwnds = new();
    public List<DesktopSession> ChildSessions = new();
    public DesktopSession ParentSession;
    public bool IsChildPanel => ParentSession != null;
    public bool Cleaned;

    public double ResizeDebounceUntil;
    public int PendingResizeW, PendingResizeH;
    public BoxCollider Collider;

    public VideoTextureProvider VideoTexture;

    public Action<int, int> OnResize;
}

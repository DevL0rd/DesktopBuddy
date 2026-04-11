using System;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public class DesktopSession
{
    public DesktopStreamer Streamer;
    public DesktopTextureSource Texture;
    public RawImage TextureImage;
    public Canvas Canvas;
    public Slot Root;
    public bool UpdateInProgress;
    public double TimeSinceLastCapture;
    public double TargetInterval;

    public Component LastActiveSource;

    public int LastScrollSign;
    public double LastScrollTick;

    public int StreamId;
    public IntPtr Hwnd;

    public uint ProcessId;
    public double TimeSinceChildCheck;
    public double TimeSinceValidCheck;
    public bool LastValidState = true;
    public TextRenderer TitleText;
    public string LastTitle;
    public HashSet<IntPtr> TrackedChildHwnds = new();
    public List<DesktopSession> ChildSessions = new();
    public DesktopSession ParentSession;
    public bool IsChildPanel => ParentSession != null;
    public bool Cleaned;

    public double ResizeDebounceUntil;
    public int PendingResizeW, PendingResizeH;
    public BoxCollider Collider;

    public DesktopKeyboardSource KeyboardSource;

    public VideoTextureProvider VideoTexture;
    public bool FeedsVirtualCamera;
    public Slot VCamSlot;
    public Camera VCamCamera;
    public bool VCamRenderPending;
    public UI_UnlitMaterial VCamIndicator;
    public bool VCamLastLitState;
    public AudioListener VMicListener;
    public UI_UnlitMaterial VMicIndicator;
    public bool VMicMuted;
    public DesktopAudioSource SpatialAudioSource;
    public AudioOutput SpatialAudioOutput;
    public bool OwnsAudioRedirect;

    public Action<int, int> OnResize;
}

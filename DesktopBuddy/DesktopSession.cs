using System;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public class DesktopSession
{
    public DesktopStreamer Streamer;
    public DesktopTextureProvider Texture;
    public RawImage TextureImage;
    public Canvas Canvas;
    public Slot Root;
    public bool UpdateInProgress;
    public int SharedTextureSlot = -1;
    public int LastKnownW, LastKnownH;

    public Component LastActiveSource;
    public HashSet<uint> ActiveTouchIds = new();

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
    public HashSet<IntPtr> SeenRelatedHwnds = new();
    public bool Cleaned;

    public double ResizeDebounceUntil;
    public int PendingResizeW, PendingResizeH;
    public int PendingVisualResizeW, PendingVisualResizeH;
    public BoxCollider Collider;
    public Slot TopBarRenderHost;

    public DesktopKeyboardSource KeyboardSource;

    public FfmpegEncoder Encoder;
    public VideoTextureProvider VideoTexture;
    public bool FeedsVirtualCamera;
    public Slot VCamSlot;
    public Camera VCamCamera;
    public bool VCamRenderPending;
    public long VCamLastSubmitTicks;
    public UI_UnlitMaterial VCamIndicator;
    public bool VCamLastLitState;
    public Slot VMicSlot;
    public AudioListener VMicListener;
    public UI_UnlitMaterial VMicIndicator;
    public bool VMicMuted;
    public DesktopAudioSource SpatialAudioSource;
    public AudioOutput SpatialAudioOutput;
    public bool OwnsAudioRedirect;

    public Action<int, int> OnResize;
}

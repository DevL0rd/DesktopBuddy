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
    public SettingsPanelState SettingsPanel;
    public Slot CullingPreviewSlot;
    public CurvedPlaneMesh PanelMesh;
    public float PanelCanvasScale;
    public string OwnerUserId;
    public ValueUserOverride<bool> ViewerStreamAllowed;
    public ValueUserOverride<bool> PreviewStreamAllowed;
    public ValueUserOverride<bool> StreamVisualAllowed;
    public ValueUserOverride<bool> FinalStreamAllowedOverride;
    public ValueField<bool> ViewerAllowedField;
    public ValueField<bool> PreviewAllowedField;
    public ValueField<bool> RangeAllowedField;
    public ValueField<bool> FinalStreamAllowedField;
    public BooleanValueDriver<Uri> StreamUrlDriver;
    public Uri StreamUrl;
    public ColliderUserTracker CullingTracker;
    public Slot CullingTriggerSlot;
    public SphereCollider CullingSphereCollider;
    public BoxCollider CullingFrustumCollider;
    public readonly Dictionary<string, bool> ViewerStreamEnabledByUserId = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> CullingAppliedStreamAllowedByUserId = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, double> CullingOutOfRangeSinceByUserId = new(StringComparer.OrdinalIgnoreCase);
    public bool LocalPreviewingRemoteStream;
    public int CullingGateGeneration;
    public double CullingOutOfRangeSince = -1;

    public DesktopKeyboardSource KeyboardSource;

    public FfmpegEncoder Encoder;
    public VideoTextureProvider VideoTexture;
    public bool FeedsVirtualCamera;
    public Slot VCamSlot;
    public Camera VCamCamera;
    public RenderTextureProvider VCamPreviewTexture;
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

public sealed class SettingsPanelState
{
    public Slot RenderHost;
    public Slot RenderRoot;
    public Slot SurfaceSlot;
    public Canvas Canvas;
    public RectTransform ModalRect;
    public RenderTextureProvider RenderTexture;
    public Camera Camera;
    public CurvedPlaneMesh Mesh;
    public BlurMaterial BackgroundBlur;
    public StaticTexture2D BackgroundBlurMask;
    public int BackgroundBlurMaskWidth;
    public int BackgroundBlurMaskHeight;
    public Slot ContentRoot;
    public Slot TabRoot;
    public ScrollRect ContentScroll;
    public ScrollRect DebugLogScroll;
    public Text DebugLogText;
    public List<SettingsScrollbarState> Scrollbars = new();
    public Slot OwnerRoot;
    public DesktopSession Session;
    public int RenderWidth;
    public int RenderHeight;
    public int ModalWidth;
    public int ModalHeight;
    public float CanvasScale;
    public SettingsPanelTab ActiveTab = SettingsPanelTab.Viewers;
    public bool ViewerCullingPreviewEnabled;
    public string ViewerCullingMode = "frustum";
    public float ViewerFrustumAngle = 120f;
    public float ViewerFrustumDepth = 3f;
    public float ViewerDistance = 3f;
    public int DebugLogRefreshGeneration;
    public string DebugLogContent;
    public int ViewerListRefreshGeneration;
    public string ViewerListSignature;
}

public sealed class SettingsScrollbarState
{
    public ScrollRect Scroll;
    public Slot Root;
    public RectTransform ThumbRect;
    public Slider<float> Slider;
    public float TrackPadding = 8f;
    public float TrackHeight;
    public float ThumbHeight;
}

public enum SettingsPanelTab
{
    Viewers,
    Stream,
    Network,
    Devices,
    Debug,
    UpdateInfo
}

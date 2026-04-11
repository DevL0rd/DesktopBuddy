using System;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

/// <summary>
/// Local-only custom component that owns the desktop frame bitmap and drives GPU upload.
/// Attach to a slot created via AddLocalSlot — it will never be synced to other users.
/// Call Initialize(w, h) once after attach, then SetFrame() from the background capture thread.
/// Upload is triggered automatically on the engine update thread via OnCommonUpdate.
/// </summary>
public class DesktopTextureSource : ProceduralTextureBase
{
    // Reflection delegates — encapsulated here instead of in DesktopBuddyMod.
    // Both members are private on ProceduralTextureBase so reflection is still required,
    // but at least it lives next to the code that uses it.
    private static readonly Func<ProceduralTextureBase, Bitmap2D> _getTex2D;
    private static readonly Action<ProceduralTextureBase, TextureUploadHint, AssetIntegrated> _setFromBitmap;

    static DesktopTextureSource()
    {
        var tex2DGetter = typeof(ProceduralTextureBase)
            .GetProperty("tex2D", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetGetMethod(true);
        if (tex2DGetter != null)
            _getTex2D = (Func<ProceduralTextureBase, Bitmap2D>)Delegate.CreateDelegate(
                typeof(Func<ProceduralTextureBase, Bitmap2D>), tex2DGetter);

        var setMethod = typeof(ProceduralTextureBase)
            .GetMethod("SetFromCurrentBitmap", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setMethod != null)
            _setFromBitmap = (Action<ProceduralTextureBase, TextureUploadHint, AssetIntegrated>)
                Delegate.CreateDelegate(
                    typeof(Action<ProceduralTextureBase, TextureUploadHint, AssetIntegrated>),
                    setMethod);

        Log.Msg($"[DesktopTextureSource] Static init: getTex2D={_getTex2D != null}, setFromBitmap={_setFromBitmap != null}");
    }

    // Width/Height stored as plain C# fields — no sync needed (local-only component).
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Required abstract overrides from ProceduralTextureBase / ProceduralAssetProvider<Texture2D>.
    // GenerateSize/Format/Mipmaps tell the engine how to allocate the backing Bitmap2D.
    protected override int2 GenerateSize => new int2(Width, Height);
    protected override TextureFormat GenerateFormat => TextureFormat.RGBA32;
    protected override bool GenerateMipmaps => false;
    // No procedural generation; bitmap is filled externally by SetFrame.
    protected override void ClearTextureData() { }
    protected override void GenerateErrorIndication() { }

    // Set by the background BitmapCopyLoop thread, cleared on the engine thread in OnCommonUpdate.
    private volatile bool _frameReady;

    /// <summary>
    /// Must be called on the engine thread immediately after AttachComponent.
    /// Sets texture dimensions and filter mode. LocalManualUpdate is switched on automatically
    /// once the engine completes its one initial allocation cycle (IsAssetAvailable).
    /// </summary>
    public void Initialize(int w, int h)
    {
        Width = w;
        Height = h;
        FilterMode.Value = TextureFilterMode.Bilinear;
        Log.Msg($"[DesktopTextureSource] Initialized at {w}x{h}");
    }

    /// <summary>
    /// Copy a captured RGBA frame into the backing bitmap.
    /// Safe to call from any thread (background BitmapCopyLoop).
    /// Returns false and skips the copy if a frame is already queued or the bitmap isn't ready.
    /// </summary>
    public bool SetFrame(byte[] data, int w, int h)
    {
        if (_frameReady) return false;
        if (IsDestroyed) return false;

        var bitmap = _getTex2D?.Invoke(this);
        if (bitmap == null || bitmap.Size.x != w || bitmap.Size.y != h) return false;

        data.AsSpan(0, w * h * 4).CopyTo(bitmap.RawData);
        _frameReady = true;
        return true;
    }

    /// <summary>
    /// Engine update tick. After the engine's first allocation cycle (IsAssetAvailable),
    /// switches to manual mode and pushes queued frames to the GPU.
    /// </summary>
    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        if (!IsAssetAvailable) return;

        // First time the asset is ready — opt out of the engine's auto-regeneration cycle.
        if (!LocalManualUpdate)
        {
            LocalManualUpdate = true;
            return;
        }

        if (!_frameReady) return;
        _frameReady = false;

        using (DesktopBuddyMod.Perf.Time("texture_upload"))
            _setFromBitmap?.Invoke(this, default, null);
    }
}

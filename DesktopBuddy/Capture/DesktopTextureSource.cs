using System;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

// Local-only component that owns the desktop frame bitmap and pushes it to Renderite.
// Bypasses ProceduralTextureBase entirely — WriteFrameDirect sends straight to the
// Renderite render thread from the WGC capture thread, zero engine-update-thread cost per frame.
public class DesktopTextureSource : DynamicAssetProvider<Texture2D>,
    ITexture2DProvider, IAssetProvider<ITexture2D>, ITextureProvider, IAssetProvider<ITexture>
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    private Bitmap2D _bitmap;
    private volatile bool _uploadInFlight;

    // Explicit covariant asset properties required by the marker interface chain.
    ITexture2D IAssetProvider<ITexture2D>.Asset => Asset;
    ITexture IAssetProvider<ITexture>.Asset => Asset;

    // Called immediately after AttachComponent on the engine thread.
    public void Initialize(int w, int h)
    {
        Width = w;
        Height = h;
        HighPriorityIntegration.Value = true;
        Log.Msg($"[DesktopTextureSource] Initialized at {w}x{h}");
    }

    // Called exactly once by the engine when this component is first referenced.
    // Allocates the shared-memory Bitmap2D and queues the initial (blank) frame to Renderite.
    protected override void UpdateAsset(Texture2D asset)
    {
        _bitmap?.Buffer?.Dispose();

        IBackingBufferAllocator allocator = Engine.RenderSystem as IBackingBufferAllocator;
        _bitmap = new Bitmap2D(Width, Height, TextureFormat.RGBA32, false,
            ColorProfile.sRGB, flipY: true, null, allocator);
        if (allocator != null)
            _bitmap.RawData.Clear();  // zero-init shared memory (uninitialized otherwise)

        // Block future auto-calls — we drive uploads ourselves from the WGC thread.
        LocalManualUpdate = true;

        _uploadInFlight = true;
        asset.SetFromBitmap2D(_bitmap, new TextureUploadHint { readable = false },
            TextureFilterMode.Bilinear, 8,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, 0f,
            _ => { _uploadInFlight = false; AssetCreated(); });

        Log.Msg($"[DesktopTextureSource] Asset created, initial upload queued ({Width}x{Height})");
    }

    // DynamicAssetProvider notification — fires when the engine assigns a new asset instance.
    // Nothing to do here; UpdateAsset drives the real work.
    protected override void AssetCreated(Texture2D asset) { }

    protected override void ClearAsset()
    {
        (_bitmap as IDisposable)?.Dispose();
        _bitmap = null;
    }

    // Called from the WGC capture thread each frame.
    // Copies the mapped staging texture into the shared-memory bitmap then pushes it
    // directly to Renderite — no engine-update-thread involvement.
    // Returns false if the asset isn't ready yet or an upload is already in flight.
    public unsafe bool WriteFrameDirect(IntPtr mappedData, int rowPitch, int w, int h)
    {
        if (IsDestroyed || _uploadInFlight) return false;
        var asset = Asset;
        if (asset == null) return false;
        var bitmap = _bitmap;
        if (bitmap == null || bitmap.Size.x != w || bitmap.Size.y != h) return false;

        // Lock before memcpy so Renderite can't read while we're writing.
        _uploadInFlight = true;

        // Shader output is RGBA — straight memcpy, row-by-row for pitch padding.
        int dstStride = w * 4;
        byte* src = (byte*)mappedData;
        fixed (byte* dstBase = bitmap.RawData)
        {
            if (rowPitch == dstStride)
                Buffer.MemoryCopy(src, dstBase, (long)h * dstStride, (long)h * dstStride);
            else
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * rowPitch,
                        dstBase + (long)y * dstStride, dstStride, dstStride);
        }
        asset.SetFromBitmap2D(bitmap, new TextureUploadHint { readable = false },
            TextureFilterMode.Bilinear, 8,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, 0f,
            _ => _uploadInFlight = false);
        return true;
    }
}

using System;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void PrewarmSharedResources()
    {
        try { WgcCapture.PrewarmSharedDevice(); }
        catch (Exception ex) { Msg($"[Startup] WGC shared device prewarm failed: {ex.Message}"); }

        try { WgcCapture.PrewarmCaptureFactory(); }
        catch (Exception ex) { Msg($"[Startup] WGC capture factory prewarm failed: {ex.Message}"); }

        try { FfmpegEncoder.SetFfmpegPath(); }
        catch (Exception ex) { Msg($"[Startup] FFmpeg prewarm failed: {ex.Message}"); }

        try { FfmpegEncoder.PrewarmHardwareEncoder(WgcCapture.SharedD3dDevice, WgcCapture.SharedD3dContextLock); }
        catch (Exception ex) { Msg($"[Startup] FFmpeg hardware encoder prewarm failed: {ex.Message}"); }

        try { WindowInput.PrewarmTouchInjection(); }
        catch (Exception ex) { Msg($"[Startup] Touch injection prewarm failed: {ex.Message}"); }

        try { DesktopTextureProviderPatch.PrewarmReflection(); }
        catch (Exception ex) { Msg($"[Startup] DesktopTexture reflection prewarm failed: {ex.Message}"); }

        try { AudioRouter.PrewarmFactory(); }
        catch (Exception ex) { Msg($"[Startup] Audio router prewarm failed: {ex.Message}"); }
    }

    private static void OpenSharedTextureBridge()
    {
        if (_textureBridgeOpened) return;

        try
        {
            TextureBridgeChannel = new SharedTextureBridgeChannel();
            TextureBridgeChannel.Open();
            _textureBridgeOpened = true;
            Msg("[SharedTextureBridge] Opened successfully");
        }
        catch (Exception ex)
        {
            Msg($"[SharedTextureBridge] Error: {ex}");
        }
    }

}

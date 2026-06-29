using System;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void PrewarmSharedResources()
    {
        if (!DesktopBuddyPlatform.IsLinux)
        {
            try { WgcCapture.PrewarmSharedDevice(); }
            catch (Exception ex) { Msg($"[Startup] WGC adapter prewarm failed: {ex.Message}"); }

            try { WgcCapture.PrewarmCaptureFactory(); }
            catch (Exception ex) { Msg($"[Startup] WGC capture factory prewarm failed: {ex.Message}"); }
        }
        else
        {
            Msg("[Startup] Linux runtime detected; skipping WGC prewarm");
        }

        if (!DesktopBuddyPlatform.IsLinux)
        {
            try { FfmpegEncoder.SetFfmpegPath(); }
            catch (Exception ex) { Msg($"[Startup] FFmpeg prewarm failed: {ex.Message}"); }
        }

        if (!DesktopBuddyPlatform.IsLinux)
        {
            try { WindowInput.PrewarmTouchInjection(); }
            catch (Exception ex) { Msg($"[Startup] Touch injection prewarm failed: {ex.Message}"); }
        }

        try { DesktopTextureProviderPatch.PrewarmReflection(); }
        catch (Exception ex) { Msg($"[Startup] DesktopTexture reflection prewarm failed: {ex.Message}"); }

        if (!DesktopBuddyPlatform.IsLinux)
        {
            try { AudioRouter.PrewarmFactory(); }
            catch (Exception ex) { Msg($"[Startup] Audio router prewarm failed: {ex.Message}"); }
        }
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

# DesktopBuddy

DesktopBuddy brings your Windows desktop into Resonite with a virtual camera and microphone to integrate windows completly and seemlessly into resonite.


## Install

1. Follow instructions here to setup resonite with Gale, a mod manager for bepis mods.
https://modding.resonite.net/getting-started/installation/

2. Search for DesktopBuddy and enable the mod.

3. Launch resonite with Gale.

GitHub release zips are the bleeding-edge manual install path and may update faster than Thunderstore while packages wait for review.

## Features

- Spawn full desktops, monitors, or individual application windows as grabbable curved panels.
- Interact with windows using VR controller, hand tracking, or touch input.
- Fully gpu accelerated WGC desktop capture.
- Stream panels to other users through local encoding and remote HTTPS tunnel support.
- Virtual video camera drivers for windows so you can do video calls from within resonite.
- Virtual microphone driver for windows so friends can hear you in calls in resonite.
- Use privacy controls for hiding or limiting what other users can see.
- Adjust capture, streaming, audio, culling, viewer, and debug options from the in-world settings panel.
- Keep game-side and renderer-side work separated through the shared texture bridge.

## Credits

Special thanks to the projects and libraries DesktopBuddy builds on.

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [BepisLoader](https://thunderstore.io/c/resonite/p/ResoniteModding/BepisLoader/) | Game-side BepInEx loader |
| [BepisResoniteWrapper](https://github.com/ResoniteModding/BepisResoniteWrapper) | Resonite engine-ready startup hook |
| [InterprocessLib](https://thunderstore.io/c/resonite/p/Nytra/InterprocessLib/) | Control messages between the game plugin and renderer bridge |
| [BepInEx.Renderer](https://github.com/ResoniteModding/BepInEx.Renderer) | Renderer-side BepInEx loader |
| [RenderiteHook](https://github.com/ResoniteModding/RenderiteHook) | Renderer-side hook support |
| [FFmpeg](https://github.com/FFmpeg/FFmpeg) | H.264/HEVC encoding libraries in `DesktopBuddyRuntime` |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | C# bindings for FFmpeg, packaged in `DesktopBuddyRuntime` |
| [cloudflared](https://github.com/cloudflare/cloudflared) | Bundled Cloudflare Tunnel client for public HTTPS stream URLs |
| [SoftCam](https://github.com/tshino/softcam) | DirectShow virtual camera filter |
| [VB-Cable](https://vb-audio.com/Cable/) | Virtual microphone driver; no public source repository is provided by VB-Audio |
| [Harmony](https://github.com/pardeike/Harmony) | Runtime patching |
| [CsWinRT](https://github.com/microsoft/CsWinRT) | Windows Runtime interop support used by Windows.Graphics.Capture |

## License

AGPL-3.0 - see [LICENSE](LICENSE).

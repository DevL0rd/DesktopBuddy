# DesktopBuddy

DesktopBuddy brings your desktop seamlessly into Resonite, including screensharing, control, a virtual camera and microphone, and more.


## Install

Use a Thunderstore mod manager, such as Gale, to install the mod directly. 

GitHub release zips are provided on the DesktopBuddy Github for manual installation. They will include bleeding-edge features and may update faster than Thunderstore while packages wait for review.

### Linux prerequisites

> **⚠️ Linux support is experimental and may have issues.** The core "share your desktop into Resonite" flow works (capture, input as touch, audio, virtual camera, streaming), but expect rough edges. In particular, the capture → renderer-side path is currently a **CPU copy** rather than a full GPU pipeline — due to some complexity this is a temporary path, so it may cause performance issues until it's replaced with a proper end-to-end GPU pipeline.

On Linux, DesktopBuddy uses your system's FFmpeg and `cloudflared` at runtime, and the virtual camera needs the `v4l2loopback` kernel module. DesktopBuddy cannot install packages for you — install them with your package manager before launching:

```sh
# Arch / CachyOS
sudo pacman -S ffmpeg v4l2loopback-dkms cloudflared
```

Then use Devices → "Virtual camera setup" in-world to load and configure the camera module. (Windows needs no prerequisites.)

## Features

- Spawn full desktops or monitors (and individual application windows on Windows) as grabbable curved panels.
- Interact with captured panels using VR controllers, hand tracking, or touch input.
- Fully GPU-accelerated desktop capture — Windows Graphics Capture on Windows, PipeWire / xdg-desktop-portal on Linux.
- Stream panels to other users through local encoding and remote HTTPS tunnel support.
- Virtual video camera (DirectShow on Windows, v4l2loopback on Linux) for video calls and more from within Resonite.
- Virtual microphone for Windows for voice calls with spatialized voice from Resonite.
- Use privacy controls for hiding or limiting what other users can see.
- Adjust capture, streaming, audio, culling, viewer, and debug options from the in-world settings panel.
- Keep game-side and renderer-side work separated through the shared texture bridge.

## Credits

Special thanks to the projects and libraries DesktopBuddy builds on!

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

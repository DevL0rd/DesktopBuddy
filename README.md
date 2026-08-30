# DesktopBuddy

<p align="center">
  <img src="icon_transparent.png" alt="DesktopBuddy icon" width="512">
</p>


DesktopBuddy brings your desktop into Resonite with a virtual camera and microphone to integrate your destkop completly and seemlessly into resonite.


## Install

### Easy Install

1. Follow instructions here to setup resonite with Gale, a mod manager for bepis mods.
https://modding.resonite.net/getting-started/installation/

2. Search for DesktopBuddy and enable the mod.

3. Launch resonite with Gale.

Thunderstore packages update more slowly because every release can require review.

### Manual Install

Manual GitHub release zips are the bleeding-edge path. They include both DesktopBuddy and the runtime payloads in one self-contained zip.

1. Download `DesktopBuddy-x.y.z.zip` from the latest [GitHub release](https://github.com/DevL0rd/DesktopBuddy/releases), then extract it into the correct root folder. The zip contains the `BepInEx` and `Renderer` folders used by the manual install layout.

2. Choose install method:
For Gale, extract into the profile root:

```text
%APPDATA%\com.kesomannen.gale\resonite\profiles\Default
```

For another Gale profile, replace `Default` with that profile folder name.

For a manual BepisLoader install, extract into the Resonite install folder:

```text
C:\Program Files (x86)\Steam\steamapps\common\Resonite
```

For manual installs, launch Resonite with BepisLoader enabled, such as with `--hookfxr-enable`.

Install or enable these loader packages too:

- BepisLoader
- BepisResoniteWrapper
- InterprocessLib
- BepInExRenderer
- RenderiteHook

### Linux prerequisites

> **⚠️ Linux support is experimental and may have issues.** The core "share your desktop into Resonite" flow works (capture, input as touch, audio, virtual camera, streaming), but expect rough edges. In particular, the capture → renderer-side path is currently a **CPU copy** rather than a full GPU pipeline — due to some complexity this is a temporary path, so it may cause performance issues until it's replaced with a proper end-to-end GPU pipeline.

On Linux, DesktopBuddy uses your system's FFmpeg and `cloudflared` (loaded at runtime), and the virtual camera needs the `v4l2loopback` kernel module. DesktopBuddy cannot install packages for you, so install these first:

```sh
# Arch / CachyOS
sudo pacman -S ffmpeg v4l2loopback-dkms cloudflared
```

PipeWire and xdg-desktop-portal (used for screen capture) ship with most modern Wayland desktops. After installing `v4l2loopback`, open Devices → "Virtual camera setup" in-world to load and configure the camera module.

Is is also important to add the following WINEDLLOVERRIDES as a prefix under your steam launch options:
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```


## Features
- Spawn full desktops or monitors (and individual application windows on Windows) as grabbable curved panels.
- Interact with captured panels using VR controllers, hand tracking, or touch input.
- Fully GPU-accelerated desktop capture — Windows Graphics Capture on Windows, PipeWire / xdg-desktop-portal on Linux.
- Stream panels to other users through local encoding and remote HTTPS tunnel support.
- Virtual video camera (DirectShow on Windows, v4l2loopback on Linux) so you can do video calls from within Resonite.
- Virtual microphone driver (Windows) so friends can hear you in calls in Resonite.
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

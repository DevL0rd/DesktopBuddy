# DesktopBuddy

<p align="center">
  <img src="icon_transparent.png" alt="DesktopBuddy icon" width="512">
</p>


DesktopBuddy brings your Windows desktop into Resonite with a virtual camera and microphone to integrate windows completly and seemlessly into resonite.


## Install

### Easy Install

## Install

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


## Building

Install:

- .NET 10 SDK
- Windows SDK 10.0.26100.0 or newer

Build locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Restart
```

This builds the game-side BepInEx plugin and renderer-side shared texture bridge, deploys them into the local Gale profile named `Default` when present, and restarts Resonite through the root HookFxr loader with that profile's BepInEx target. Add `-Desktop` for desktop mode:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Restart -Desktop
```

Use a different Gale profile name with `-ProfileName`, or an exact profile path with `-ProfilePath`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Restart -ProfileName MyProfile
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Restart -ProfilePath "$env:APPDATA\com.kesomannen.gale\resonite\profiles\MyProfile"
```

CI-style compile without deploy:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -NoDeploy
```


## Packaging And Release

Thunderstore package metadata still lives in `thunderstore.toml`, while `scripts\package.ps1` builds a clean GitHub release zip for manual installation into a Gale profile or manual BepisLoader root. `VERSION` is the source of truth for the plugin package, and `RUNTIME_VERSION` is the source of truth for the separate `DesktopBuddyRuntime` Thunderstore package. After changing either file, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sync-version.ps1
```

Create the manual GitHub release zip locally with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1
```

The manual zip is always self-contained and includes both the mod DLLs and `DesktopBuddyRuntime`.

To build the split Thunderstore packages for manager/testing use, add `-ThunderstoreFormat`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1 -ThunderstoreFormat
```

GitHub Actions refreshes the manual release zip on pushes to `main`, even when `VERSION` does not change. Thunderstore publishing is separate and only uploads exact package versions that do not already exist.


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

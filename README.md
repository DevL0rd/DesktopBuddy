# DesktopBuddy

> **BIG COMPATIBILITY WARNING**
>
> DesktopBuddy currently supports **Resonite Mod Loader only** on a **vanilla Resonite renderer install**.
> The setup script installs DesktopBuddy's required renderer dependencies.
> Any other modloader, renderer modloader, or unrelated renderer mods are **not supported right now**.
> This will be fixed soon.

A Resonite mod that spawns world-space desktop/window viewers with touch input, GPU-accelerated capture, remote streaming, and virtual camera/microphone output.

## Quick Start

1. Install [Resonite](https://store.steampowered.com/app/2519830/Resonite/) and [Resonite Mod Loader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Download the latest `DesktopBuddy-Alpha-*.zip` from [Releases](https://github.com/DevL0rd/DesktopBuddy/releases).
3. Extract the zip directly into your Resonite root folder, for example `C:\Program Files (x86)\Steam\steamapps\common\Resonite\`.
4. For a first-time install, run `setup\Setup-DesktopBuddy.bat` as administrator from the Resonite root.
5. Launch Resonite and open the context menu, then **Desktop**.

If you already had DesktopBuddy installed and working, you do **not** need to run the setup script again after extracting an update. Run it again only if dependencies are missing, the renderer setup was removed, or you are repairing the install.

The zip is already structured for the Resonite root. There is no DesktopBuddy Manager anymore.

## Setup Script

`setup\Setup-DesktopBuddy.bat` launches `setup\Setup-DesktopBuddy.ps1` with administrator privileges and performs the setup work that used to live in DesktopBuddy Manager:

- Registers SoftCam so the virtual camera appears as **DesktopBuddy - Camera**
- Installs VB-Cable so the virtual microphone appears as **CABLE Output**
- Disables VB-Cable loopback
- Adds the HTTP URL ACL for the stream server on port `48080`
- Installs required renderer dependencies: RenderiteHook and BepInEx.Renderer
- Checks that `DesktopBuddyRenderer.dll` is in the renderer plugin folder

You do not need to run this script again for normal DesktopBuddy updates if the previous install was already working. Run it again only if any of those dependencies are missing or out of place. A reboot may be required after VB-Cable installation.

## Troubleshooting

**DesktopBuddy does not appear in the context menu**

- Confirm `DesktopBuddy.dll` is in `<Resonite root>\rml_mods\`.
- Confirm Resonite Mod Loader is installed and working.
- Start from a vanilla Resonite renderer and run `setup\Setup-DesktopBuddy.bat`; other modloaders and unrelated renderer mods are currently unsupported.

**Virtual camera "DesktopBuddy - Camera" not showing**

- Register `rml_libs\softcam64.dll` with `regsvr32` from an elevated terminal.
- Restart Resonite after registration.
- Restart Discord/Zoom/OBS; many apps cache the device list at startup.
- Check Windows Settings > Bluetooth & devices > Cameras.

**Virtual microphone "CABLE Output" not showing**

- Run `vbcable\VBCABLE_Setup_x64.exe` as administrator.
- Reboot after installing VB-Cable.
- Check Windows Settings > System > Sound > Input.

**Virtual camera shows black**

- Open a desktop window in DesktopBuddy first.
- Make sure the consumer app has selected **DesktopBuddy - Camera**.
- The camera only renders when something is actively using it.

**Virtual mic is silent**

- Make sure the mic indicator on the DesktopBuddy panel is green.
- In Discord/Zoom, select **CABLE Output** as your microphone input.
- The mic captures spatial in-game audio, so make sure there are audio sources in the world.

**Streaming not working for other users**

The mod runs a local HTTP server on port `48080`. If streaming is blocked, run this once as administrator:

```cmd
netsh http add urlacl url=http://+:48080/ sddl="D:(A;;GX;;;S-1-1-0)"
```

## Features

- GPU-accelerated capture via Windows.Graphics.Capture and renderer-side capture support
- Hardware H.264/HEVC encoding via NVENC or AMF through FFmpeg
- Remote streaming via MPEG-TS over Cloudflare Tunnel
- Per-window audio capture via WASAPI process loopback
- Virtual camera output as **DesktopBuddy - Camera**
- Virtual microphone output through VB-Cable as **CABLE Output**
- Touch, mouse, keyboard, and scroll input injection from VR controllers
- Child window detection for popups and dialogs
- Context menu integration for windows and monitors
- Auto-reconnecting Cloudflare tunnel

## Usage

1. In Resonite, open the context menu.
2. Select **Desktop** to open the window/monitor picker.
3. Pick a window or monitor to spawn a viewer panel.
4. Interact with the panel using VR controllers.
5. Other users in the session see the stream through Cloudflare Tunnel.

## Prerequisites

- Windows 10+
- NVIDIA or AMD GPU
- Resonite with Resonite Mod Loader
- Vanilla Resonite renderer before setup, with no unrelated renderer mods

## Building

Install:

- .NET 10 SDK
- Windows SDK 10.0.19041.0+ if you need shader tools

Then build locally:

```cmd
scripts\build.bat -r
```

This builds the game-side mod and renderer component, deploys them into your local Resonite install, and restarts Resonite. Add `-d` for desktop mode:

```cmd
scripts\build.bat -r -d
```

## Packaging

```cmd
scripts\package.bat
```

Creates `DesktopBuddy-Alpha-<date>_<sha>.zip` ready to extract into the Resonite root:

```text
DesktopBuddy-Alpha-*.zip
  INSTALL.txt
  setup/
    Setup-DesktopBuddy.bat
    Setup-DesktopBuddy.ps1
  rml_mods/
    DesktopBuddy.dll
    DesktopBuddy.sha
  rml_libs/
    avcodec-62.dll
    avformat-62.dll
    avutil-60.dll
    swresample-6.dll
    softcam64.dll
    cloudflared.exe
  Renderer/BepInEx/plugins/
    DesktopBuddyRenderer.dll
  vbcable/
    VBCABLE_Setup_x64.exe
    driver files
```

## Credits

Special thanks to the projects and libraries DesktopBuddy builds on.

### Bundled or Packaged

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [ResoniteInterprocessLib](https://github.com/Nytra/ResoniteInterprocessLib) | Shared-source IPC between the Resonite mod and renderer plugin |
| [FFmpeg](https://github.com/FFmpeg/FFmpeg) | H.264/HEVC encoding libraries in `rml_libs` |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | C# bindings for FFmpeg |
| [SoftCam](https://github.com/tshino/softcam) | DirectShow virtual camera filter |
| [cloudflared](https://github.com/cloudflare/cloudflared) | Cloudflare Tunnel executable for remote stream access |

### Installed by Setup

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [RenderiteHook](https://github.com/ResoniteModding/RenderiteHook) | Renderer-side hook support |
| [BepInEx.Renderer](https://github.com/ResoniteModding/BepInEx.Renderer) | BepInEx loader for the Resonite renderer |
| [VB-Cable](https://vb-audio.com/Cable/) | Virtual microphone driver; no public source repository is provided by VB-Audio |

### Build and Runtime References

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [Resonite Mod Loader](https://github.com/resonite-modding-group/ResoniteModLoader) | Game-side mod loading |
| [Harmony](https://github.com/pardeike/Harmony) | Runtime patching |
| [BepInEx](https://github.com/BepInEx/BepInEx) | Renderer plugin runtime |
| [uWindowCapture](https://github.com/hecomi/uWindowCapture) | Renderer-side Windows capture plugin |
| [ILRepack](https://github.com/gluck/il-repack) | Merging packaged game-side dependencies |
| [CsWinRT](https://github.com/microsoft/CsWinRT) | Windows Runtime interop support used by Windows.Graphics.Capture |

## Contributing

Contributions welcome. Areas where help is especially needed:

- Linux support
- Renderer compatibility cleanup
- Code review and testing

## License

AGPL-3.0 - see [LICENSE](LICENSE).

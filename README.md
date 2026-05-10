# DesktopBuddy

> **BIG COMPATIBILITY WARNING**
>
> DesktopBuddy currently supports **Resonite Mod Loader only** on a **vanilla Resonite renderer install**.
> The setup script installs DesktopBuddy's required renderer dependencies.
> Any other modloader, renderer modloader, or unrelated renderer mods are **not supported right now**.
> This will be fixed soon.
> Run `setup\Setup-DesktopBuddy.bat` as administrator after extracting every update, even if DesktopBuddy was already installed.
> The setup script is idempotent and skips dependencies that are already installed.

A Resonite mod that spawns world-space desktop/window viewers with touch input, GPU-accelerated capture, shared GPU textures, remote streaming, and virtual camera/microphone output.

## Quick Start

1. Install [Resonite](https://store.steampowered.com/app/2519830/Resonite/) and [Resonite Mod Loader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Download the latest `DesktopBuddy-Alpha-*.zip` from [Releases](https://github.com/DevL0rd/DesktopBuddy/releases).
3. Extract the zip directly into your Resonite root folder, for example `C:\Program Files (x86)\Steam\steamapps\common\Resonite\`.
4. Run `setup\Setup-DesktopBuddy.bat` as administrator from the Resonite root. Run this for upgrades too; it is safe to run repeatedly.
5. Launch Resonite and open the context menu, then **Desktop**.

The zip is already structured for the Resonite root. There is no DesktopBuddy Manager anymore.

## Setup Script

`setup\Setup-DesktopBuddy.bat` launches `setup\Setup-DesktopBuddy.ps1` with administrator privileges and performs the setup work that used to live in DesktopBuddy Manager:

- Registers SoftCam so the virtual camera appears as **DesktopBuddy - Camera**
- Installs VB-Cable so the virtual microphone appears as **CABLE Output**
- Disables VB-Cable loopback
- Installs required renderer dependencies: RenderiteHook and BepInEx.Renderer
- Checks that `DesktopBuddySharedTextureBridge.dll` is in the renderer plugin folder

The setup script is safe to run repeatedly. It checks existing dependencies first and skips work that is already done. A reboot may be required after VB-Cable installation.

## Troubleshooting

**DesktopBuddy does not appear in the context menu**

- Confirm `DesktopBuddy.dll` is in `<Resonite root>\rml_mods\`.
- Confirm Resonite Mod Loader is installed and working.
- Start from a vanilla Resonite renderer and run `setup\Setup-DesktopBuddy.bat`; other modloaders and unrelated renderer mods are currently unsupported.

**Virtual camera "DesktopBuddy - Camera" not showing**

- Register `DesktopBuddyNative\softcam64.dll` with `regsvr32` from an elevated terminal.
- Restart Resonite after registration.
- Restart Discord/Zoom/OBS; many apps cache the device list at startup.
- Check Windows Settings > Bluetooth & devices > Cameras.

**Virtual microphone "CABLE Output" not showing**

- Run `DesktopBuddyNative\VBCABLE_Setup_x64.exe` as administrator.
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

DesktopBuddy now serves remote viewer video with an embedded RTSP-over-TCP server. By default it listens on TCP port `8554` and advertises either your auto-detected public IP or the configured tunnel endpoint.

- For direct hosting, forward TCP port `8554` on your router or enable `auto_port_forward` in the config.
- For tunnel hosting, set `use_tunnel` to `true`; the bundled Pinggy support will advertise the tunnel host/port when connected.
- If the stream only works locally, check Windows Firewall, router port forwarding, CGNAT, and whether the configured/tunnel endpoint matches the URL in the DesktopBuddy log.

## Configuration

DesktopBuddy writes its Resonite Mod Loader config to:

```text
<Resonite root>\rml_config\DesktopBuddy.json
```

The config schema is currently `1.0.10`. Older config files are reset when the schema changes so new defaults are applied cleanly.

### Streaming

| Key | Default | Notes |
| --- | --- | --- |
| `bitrate` | `10` | Target video bitrate in Mbps. Encoders use variable bitrate with a peak around 120% of this value. |
| `streamFps` | `60` | Nominal FPS passed to the encoder for timing and GOP/keyframe math. Capture remains event-driven; this is not a sleep-based frame cap. |
| `keyframeIntervalMs` | `1000` | Maximum forced keyframe interval. Lower values can improve join/catch-up time but spend more bitrate on keyframes. |
| `maxStreamResolution` | `2560` | Maximum encoded long edge. Windows larger than this are GPU-scaled down before encoding. |
| `libVlcNetworkCachingMs` | `200` | Renderer-side libVLC network cache for RTSP streams. |
| `libVlcLiveCachingMs` | `200` | Renderer-side libVLC live cache for RTSP streams. |
| `libVlcFileCachingMs` | `100` | Renderer-side libVLC file cache fallback. |

### RTSP Endpoint

| Key | Default | Notes |
| --- | --- | --- |
| `ip_address` | `auto` | Public hostname/IP written into RTSP URLs. Use `auto` for public-IP detection, or set a hostname/IP manually. |
| `rtsp_port` | `8554` | TCP port for the embedded RTSP server. |
| `auto_port_forward` | `true` | Attempts automatic router port forwarding for the RTSP port when direct hosting is used. |
| `rtsp_transport` | `tcp` | DesktopBuddy currently serves interleaved RTSP/RTP over TCP. |

### Tunnel

| Key | Default | Notes |
| --- | --- | --- |
| `use_tunnel` | `true` | Use a TCP tunnel instead of the direct public IP/port-forward endpoint. |
| `tunnel_provider` | `pinggy` | Supported tunnel provider. |
| `pinggy_ssh_path` | `ssh` | OpenSSH executable used to start the Pinggy tunnel. |
| `pinggy_server` | `a.pinggy.io` | Pinggy server host. Pro accounts may use a different server. |
| `pinggy_token` | empty | Optional Pinggy account token for reserved ports, custom domains, or pro features. |
| `pinggy_remote_port` | `0` | Requested Pinggy remote TCP port. `0` lets Pinggy assign one. |
| `pinggy_listen_address` | empty | Advanced Pinggy listen-address override. Leave blank for normal TCP forwarding. |
| `pinggy_force_existing` | `true` | With a token, asks Pinggy to disconnect an existing tunnel using the same token before connecting. |

### Other

| Key | Default | Notes |
| --- | --- | --- |
| `spatialAudio` | `false` | Enables spatial audio routing through VB-Cable when available. |
| `checkForUpdates` | `true` | Checks GitHub releases for newer DesktopBuddy builds. |
| `useMediaMtx` | `false` | Optional external MediaMTX mode for users who explicitly want to provide their own RTSP server. |
| `mediaMtxHost` | empty | MediaMTX host when `useMediaMtx` is enabled. |
| `mediaMtxPort` | `8554` | MediaMTX RTSP port. |
| `mediaMtxStreamName` | empty | MediaMTX stream-name prefix. |

## Features

- GPU-accelerated capture via Windows.Graphics.Capture with a shared DX11 texture bridge into the renderer
- Hardware H.264/HEVC encoding via NVENC or AMF through FFmpeg
- Remote streaming via embedded RTSP-over-TCP, with optional Pinggy TCP tunnel support
- Per-window audio capture via WASAPI process loopback
- Virtual camera output as **DesktopBuddy - Camera**
- Virtual microphone output through VB-Cable as **CABLE Output**
- Touch, mouse, keyboard, and scroll input injection from VR controllers
- Child window detection for popups and dialogs
- Context menu integration for windows and monitors
- Optional external MediaMTX RTSP mode

## Usage

1. In Resonite, open the context menu.
2. Select **Desktop** to open the window/monitor picker.
3. Pick a window or monitor to spawn a viewer panel.
4. Interact with the panel using VR controllers.
5. Other users in the session see the stream through the configured RTSP endpoint.

## Prerequisites

- Windows 10+
- NVIDIA or AMD GPU
- Resonite with Resonite Mod Loader
- Vanilla Resonite renderer before setup, with no unrelated renderer mods

## Building

Install:

- .NET 10 SDK
- Windows SDK 10.0.19041.0+

Then build locally:

```cmd
scripts\build.bat -r
```

This builds the game-side mod and renderer-side shared texture bridge, deploys them into your local Resonite install, and restarts Resonite. Add `-d` for desktop mode:

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
  DesktopBuddyNative/
    avcodec-62.dll
    avformat-62.dll
    avutil-60.dll
    swresample-6.dll
    softcam64.dll
    VBCABLE_Setup_x64.exe
    VB-Cable driver files
  Renderer/BepInEx/plugins/
    DesktopBuddySharedTextureBridge.dll
```

## Credits

Special thanks to the projects and libraries DesktopBuddy builds on.

### Bundled or Packaged

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [ResoniteInterprocessLib](https://github.com/Nytra/ResoniteInterprocessLib) | Shared-source control messages between the Resonite mod and renderer-side shared texture bridge |
| [FFmpeg](https://github.com/FFmpeg/FFmpeg) | H.264/HEVC encoding libraries in `DesktopBuddyNative` |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | C# bindings for FFmpeg |
| [SoftCam](https://github.com/tshino/softcam) | DirectShow virtual camera filter |

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
| [ILRepack](https://github.com/gluck/il-repack) | Merging packaged game-side dependencies |
| [CsWinRT](https://github.com/microsoft/CsWinRT) | Windows Runtime interop support used by Windows.Graphics.Capture |

## Contributing

Contributions welcome. Areas where help is especially needed:

- Linux support
- Renderer compatibility cleanup
- Code review and testing

## License

AGPL-3.0 - see [LICENSE](LICENSE).

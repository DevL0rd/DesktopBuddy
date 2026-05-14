# DesktopBuddy

A BepisLoader/BepInEx mod for Resonite that spawns world-space desktop/window viewers with touch input, GPU-accelerated capture, shared GPU textures, remote streaming, and virtual camera/microphone output.

## Quick Start

1. Install DesktopBuddy from Thunderstore or a Thunderstore-compatible mod manager.
2. Make sure the package dependencies are installed: BepisLoader, BepisResoniteWrapper, InterprocessLib, BepInExRenderer, and RenderiteHook.
3. Enable BepisLoader by setting `enable=true` in `hookfxr.ini`, or add `--hookfxr-enable` to Resonite's Steam launch options.
4. Launch Resonite once. DesktopBuddy checks SoftCam, VB-Cable, and the local HTTP URL ACL on startup and asks for administrator permission only if setup work is needed.
5. Launch Resonite and open the context menu, then **Desktop**.

For manual development installs, copy the package layout into the Resonite root/profile:

```text
plugins/DesktopBuddy/
Renderer/BepInEx/plugins/DesktopBuddySharedTextureBridge/
```

## First-Run Setup

DesktopBuddy runs a first-start setup check from inside the mod because Thunderstore/BepisLoader installs files and dependencies, but does not run arbitrary setup scripts. The mod logs the setup check to the normal DesktopBuddy log and writes elevated helper details to `DesktopBuddyNative\DesktopBuddySetup.log`. If admin work is needed and Resonite is not elevated, it starts a hidden one-time elevated helper for only the missing actions and requests Windows UAC permission.

The setup helper is built into DesktopBuddy and only runs when one of the local Windows setup bits is missing, so normal launches do not prompt for UAC. The setup work is DesktopBuddy-owned local setup:

- Registers SoftCam so the virtual camera appears as **DesktopBuddy - Camera**
- Installs VB-Cable so the virtual microphone appears as **CABLE Output**
- Disables VB-Cable loopback
- Configures the local HTTP listener on `http://+:48080/`
- Checks for the renderer bridge DLL

Thunderstore dependencies handle BepInEx, BepisLoader, BepInExRenderer, RenderiteHook, and InterprocessLib.

## Troubleshooting

**DesktopBuddy does not appear in the context menu**

- Confirm `DesktopBuddy.dll` is in `<Resonite root>\plugins\DesktopBuddy\`.
- Confirm BepisLoader and BepisResoniteWrapper are installed.
- Check the BepInEx log for DesktopBuddy load errors.

**Virtual camera "DesktopBuddy - Camera" not showing**

- Let DesktopBuddy's first-run setup complete, or register `<Resonite root>\plugins\DesktopBuddy\DesktopBuddyNative\softcam64.dll` with `regsvr32` from an elevated terminal.
- Restart Resonite after registration.
- Restart Discord/Zoom/OBS; many apps cache the device list at startup.
- Check Windows Settings > Bluetooth & devices > Cameras.

**Virtual microphone "CABLE Output" not showing**

- Run `<Resonite root>\plugins\DesktopBuddy\DesktopBuddyNative\VBCABLE_Setup_x64.exe` as administrator.
- Reboot after installing VB-Cable.
- Check Windows Settings > System > Sound > Input.

**Streaming not working for other users**

DesktopBuddy serves remote viewer video as MPEG-TS over the built-in HTTP stream server, then exposes that server through the bundled Cloudflare Tunnel client. The local HTTP server listens on port `48080`, and public stream URLs look like `https://*.trycloudflare.com/stream/{streamId}`.

- Let DesktopBuddy's first-run setup complete so Windows allows the HTTP listener on `http://+:48080/`.
- Make sure `<Resonite root>\plugins\DesktopBuddy\DesktopBuddyNative\cloudflared.exe` is present.
- If the stream only works locally, check Windows Firewall and look for `[Tunnel] PUBLIC URL:` in the DesktopBuddy log.
- Optional MediaMTX mode still publishes RTSP to your own external MediaMTX server when explicitly configured.

## Configuration

DesktopBuddy writes fresh BepInEx config to:

```text
<Resonite root>\BepInEx\config\com.devl0rd.DesktopBuddy.cfg
```

Old Resonite Mod Loader JSON config is intentionally ignored.

### Streaming

| Key | Default | Notes |
| --- | --- | --- |
| `bitrate` | `10` | Target video bitrate in Mbps. Encoders use variable bitrate with a peak around 120% of this value. |
| `streamFps` | `60` | Nominal FPS passed to the encoder for timing. Capture remains event-driven; this is not a sleep-based frame cap. |
| `maxStreamResolution` | `2560` | Maximum encoded long edge. Windows larger than this are GPU-scaled down before encoding. |

### Other

| Key | Default | Notes |
| --- | --- | --- |
| `spatialAudio` | `false` | Enables spatial audio routing through VB-Cable when available. |
| `checkForUpdates` | `true` | Checks Thunderstore for newer DesktopBuddy versions. |
| `useMediaMtx` | `false` | Optional external MediaMTX mode for users who explicitly want to provide their own RTSP server. |
| `mediaMtxHost` | empty | MediaMTX host when `useMediaMtx` is enabled. |
| `mediaMtxPort` | `8554` | MediaMTX RTSP port. |
| `mediaMtxStreamName` | empty | MediaMTX stream-name prefix. |

## Features

- GPU-accelerated capture via Windows.Graphics.Capture with a shared DX11 texture bridge into the renderer
- Hardware H.264/HEVC encoding via NVENC or AMF through FFmpeg
- Remote streaming via MPEG-TS over HTTP through bundled Cloudflare Tunnel
- Per-window audio capture via WASAPI process loopback
- Virtual camera output as **DesktopBuddy - Camera**
- Virtual microphone output through VB-Cable as **CABLE Output**
- Touch, mouse, keyboard, and scroll input injection from VR controllers
- Child window detection for popups and dialogs
- Context menu integration for windows and monitors
- Optional external MediaMTX RTSP mode

## Building

Install:

- .NET 10 SDK
- Windows SDK 10.0.19041.0+

Then build locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Restart
```

This builds the game-side BepInEx plugin and renderer-side shared texture bridge, deploys them into the local Gale profile named `Default` when present, and restarts Resonite with that profile's BepInEx target. Add `-d` for desktop mode:

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

## Packaging

Thunderstore metadata lives in `thunderstore.toml`. You can build the Thunderstore package with TCLI:

```powershell
dotnet tool restore
scripts\sync-version.ps1
dotnet tcli build
```

You can also create the same package layout with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1
```

The package layout is:

```text
manifest.json
README.md
CHANGELOG.md
icon.png
INSTALL.txt
plugins/DesktopBuddy/
  DesktopBuddy.dll
  DesktopBuddy.sha
  DesktopBuddyNative/
    FFmpeg.AutoGen.dll
    Microsoft.Windows.SDK.NET.dll
    WinRT.Runtime.dll
    native FFmpeg, cloudflared, SoftCam, and VB-Cable files
Renderer/BepInEx/plugins/DesktopBuddySharedTextureBridge/
  DesktopBuddySharedTextureBridge.dll
```

The package does not include BepInEx, BepisLoader, RenderiteHook, BepInExRenderer, or InterprocessLib DLLs; those are declared Thunderstore dependencies.

## Publishing

`VERSION` is the source of truth for the plugin and package version. Run `scripts\sync-version.ps1` after changing it; the build generates the plugin version constant from that file and the sync script updates `thunderstore.toml`.

Local publish:

```powershell
copy .env.example .env
REM put your fresh Thunderstore service account token in .env
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy-thunderstore.ps1 -Publish
```

GitHub Actions publish is in `.github/workflows/publish-thunderstore.yml`. Add a repository secret named `TCLI_AUTH_TOKEN`, then push a tag like `v1.0.13` after updating `VERSION` to `1.0.13`.

## Credits

Special thanks to the projects and libraries DesktopBuddy builds on.

| Project | What DesktopBuddy uses it for |
| --- | --- |
| [BepisLoader](https://thunderstore.io/c/resonite/p/ResoniteModding/BepisLoader/) | Game-side BepInEx loader |
| [BepisResoniteWrapper](https://github.com/ResoniteModding/BepisResoniteWrapper) | Resonite engine-ready startup hook |
| [InterprocessLib](https://thunderstore.io/c/resonite/p/Nytra/InterprocessLib/) | Control messages between the game plugin and renderer bridge |
| [BepInEx.Renderer](https://github.com/ResoniteModding/BepInEx.Renderer) | Renderer-side BepInEx loader |
| [RenderiteHook](https://github.com/ResoniteModding/RenderiteHook) | Renderer-side hook support |
| [FFmpeg](https://github.com/FFmpeg/FFmpeg) | H.264/HEVC encoding libraries in `DesktopBuddyNative` |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | C# bindings for FFmpeg, packaged in `DesktopBuddyNative` |
| [cloudflared](https://github.com/cloudflare/cloudflared) | Bundled Cloudflare Tunnel client for public HTTPS stream URLs |
| [SoftCam](https://github.com/tshino/softcam) | DirectShow virtual camera filter |
| [VB-Cable](https://vb-audio.com/Cable/) | Virtual microphone driver; no public source repository is provided by VB-Audio |
| [Harmony](https://github.com/pardeike/Harmony) | Runtime patching |
| [CsWinRT](https://github.com/microsoft/CsWinRT) | Windows Runtime interop support used by Windows.Graphics.Capture |

## License

AGPL-3.0 - see [LICENSE](LICENSE).

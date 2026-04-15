# DesktopBuddy

A Resonite mod that spawns world-space desktop/window viewers with touch input, GPU-accelerated capture, remote streaming, and virtual camera/microphone output.

## Quick Start

### Option A — Manager (recommended)

1. Install [Resonite](https://store.steampowered.com/app/2519830/Resonite/) with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)
2. Download `DesktopBuddyManager.exe` from [Releases](https://github.com/DevL0rd/DesktopBuddy/releases) (inside the `.zip`, or download standalone if provided)
3. Place `DesktopBuddyManager.exe` in your Resonite root folder (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Resonite\`)
4. Run `DesktopBuddyManager.exe` **as administrator**
5. The manager will auto-detect your Resonite install, or browse to it manually
6. Click **Install / Update** — it will download the latest release zip, extract all files, register the virtual camera, and install the virtual microphone driver
7. **Restart your PC** when prompted — the virtual microphone driver requires a reboot
8. Launch Resonite and open the context menu → **Desktop** to get started

### Option B — Manual zip extract

1. Install [Resonite](https://store.steampowered.com/app/2519830/Resonite/) with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)
2. Download the latest `DesktopBuddy-Alpha-*.zip` from [Releases](https://github.com/DevL0rd/DesktopBuddy/releases)
3. Extract the zip **directly into your Resonite root folder** (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Resonite\`) — the zip is already structured to match
4. Run `DesktopBuddyManager.exe` **as administrator** to register the virtual camera and install the virtual microphone driver
5. Launch Resonite and open the context menu → **Desktop** to get started

## First-Run Setup (Automatic)

DesktopBuddy automatically sets up virtual devices on the first launch:

| Step | What happens | User action |
|------|-------------|-------------|
| Virtual camera registration | Registers "DesktopBuddy - Camera" DirectShow filter via `regsvr32` | Accept UAC prompt |
| Virtual microphone install | Installs VB-Cable virtual audio driver | Accept UAC prompt |

Both prompts may appear on your desktop monitor (behind VR). If you're in VR, check your desktop.

After accepting both prompts, **restart your PC**. The virtual microphone driver (VB-Cable) requires a reboot to become active. The virtual camera works immediately after a Resonite relaunch, but the mic needs a full restart.

On subsequent launches, no prompts will appear — the setup persists across updates.

## Troubleshooting

**Virtual camera "DesktopBuddy - Camera" not showing in Discord/Zoom/Chrome**
- Restart Resonite after the first-run setup
- Restart Discord/Zoom — they cache the device list at startup
- Check Windows Settings > Bluetooth & devices > Cameras to verify the device appears

**Virtual microphone "CABLE Output" not showing**
- Restart Resonite after the first-run setup
- If still missing, try a full PC restart — VB-Cable sometimes needs it
- Check Windows Settings > System > Sound > Input to verify "CABLE Output" appears

**Virtual camera shows black**
- Open a desktop window in DesktopBuddy first — the camera renders from the in-game camera on your panel
- Make sure a consumer app (Discord, etc.) has selected "DesktopBuddy - Camera" as its camera — the camera only renders when something is actively using it

**Virtual mic is silent**
- Make sure the mic indicator (small panel next to the camera) is **green** (unmuted). Click it to toggle.
- In Discord/Zoom, select **"CABLE Output"** as your microphone input
- The mic captures spatial in-game audio — make sure there are audio sources in the world

**Streaming not working for other users?**
- The mod runs a local HTTP server on port 48080. If streaming isn't working, run this command once as Administrator:
  ```
  netsh http add urlacl url=http://+:48080/ sddl="D:(A;;GX;;;S-1-1-0)"
  ```
  The mod tries to do this automatically on first run.

## Features

- **GPU-accelerated capture** via Windows.Graphics.Capture (WGC)
- **GPU BGRA-to-RGBA conversion** via D3D11 compute shader
- **Hardware H.264/HEVC encoding** via NVENC (NVIDIA) or AMF (AMD) through FFmpeg
- **Remote streaming** via MPEG-TS over Cloudflare Tunnel — other users see your desktop in VR
- **Per-window audio capture** via WASAPI process loopback
- **Virtual camera** — in-game camera view exposed as "DesktopBuddy - Camera" in Discord, Zoom, Chrome, OBS
- **Virtual microphone** — spatial in-game audio captured via AudioListener, routed to VB-Cable virtual mic for Discord/Zoom
- **Touch/mouse/keyboard input** injection from VR controllers
- **Child window detection** — popups and dialogs auto-spawn as separate panels
- **Context menu integration** — pick a window or monitor from the Resonite context menu
- **Auto-reconnecting Cloudflare tunnel** with error detection and restart

## Usage

1. In Resonite, open the context menu (right-click or equivalent VR gesture)
2. Select **Desktop** to open the window/monitor picker
3. Pick a window or monitor to spawn a viewer panel
4. Interact with the panel using VR controllers (touch, scroll, keyboard)
5. Other users in the session see the stream via Cloudflare Tunnel

### Virtual Camera

Each desktop panel has a small dark indicator mounted on top. When a consumer app (Discord, Zoom, Chrome, OBS) selects "DesktopBuddy - Camera" as its webcam, the indicator turns **red** and the in-game camera starts rendering automatically. Click the indicator to manually disable/enable the camera.

### Virtual Microphone

Next to the camera indicator is a **green** mic indicator. It captures spatial in-game audio from an AudioListener positioned at the panel. The audio is routed to VB-Cable's "CABLE Output" virtual microphone. Click the indicator to **mute/unmute** — it turns dark red when muted.

In Discord/Zoom, select **"CABLE Output"** as your microphone to share in-game audio.

## Prerequisites

- Windows 10+ with an NVIDIA or AMD GPU
- [Resonite](https://store.steampowered.com/app/2519830/Resonite/) with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)

### For building from source

- .NET 10 SDK
- Windows SDK 10.0.19041.0+ (for `fxc.exe` shader compiler)
- Visual Studio 2019+ Build Tools (for SoftCam DLL compilation)

## Building

```
scripts/build.bat -r
```

Builds the mod and restarts Resonite. The build process:
1. Compiles the HLSL compute shader (if `fxc.exe` available)
2. Builds the mod DLL with ILRepack (merges all managed dependencies)
3. Deploys to Resonite: `rml_mods/DesktopBuddy.dll`, `rml_libs/` (ffmpeg, softcam, cloudflared)

Add `-d` for desktop mode: `scripts/build.bat -r -d`

## Packaging

```
scripts/package.bat
```

Creates `DesktopBuddy-Alpha-<date>_<sha>.zip` ready to extract into the Resonite root:

```
DesktopBuddy-Alpha-*.zip
  DesktopBuddyManager.exe              ← manager lives at Resonite root
  rml_mods/
    DesktopBuddy.dll                   ← mod (all managed deps merged)
    DesktopBuddy.sha                   ← build SHA for update checks
  rml_libs/
    avcodec-62.dll … (FFmpeg DLLs)    ← runtime native libs
    softcam64.dll                      ← virtual camera filter
    cloudflared.exe                    ← Cloudflare Tunnel binary
  Renderer/BepInEx/plugins/
    DesktopBuddyRenderer.dll
  vbcable/
    VBCABLE_Setup_x64.exe + drivers    ← virtual audio cable installer
```

Extract to `<Resonite root>\` and run `DesktopBuddyManager.exe` as administrator.

## Third-Party Components

- **[SoftCam](https://github.com/tshino/softcam)** — MIT license — Virtual camera DirectShow filter
- **[VB-Cable](https://vb-audio.com/Cable/)** — Donationware by VB-Audio — Virtual audio cable driver. If you find it useful, please consider [donating](https://vb-audio.com/Cable/).
- **[FFmpeg](https://ffmpeg.org/)** — LGPL — Media encoding libraries
- **[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/)** — Cloudflare — Network tunneling

## Contributing

Contributions welcome! Areas where help is especially needed:

- **Linux support** — WGC is Windows-only, needs PipeWire/XDG portal capture + VA-API encoding
- **Code review** — if you spot anything, please open an issue or PR

## License

AGPL-3.0 — see [LICENSE](LICENSE).

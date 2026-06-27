## 1.0.20 - 2026-06-26

### Linux support
- Cloudflare tunnel now runs on native Linux (bundled native `cloudflared` binary, `/dev/null` config, exec-bit re-asserted; skips the Windows-only kill-job).
- Renamed the internal `IsLinuxProton` platform check to `IsLinux` (modern Resonite runs natively on Linux, not under Proton).
- Linux desktop audio: the native stream now carries audio (AAC) captured via PipeWire, excluding Resonite's own output.
- Linux virtual camera via v4l2loopback ("DesktopBuddy - Camera"), with a setup action to install/load the module.

### General
- New General-tab toggles: Dynamic lights (default off) and "New windows start private".
- Stream audio now defaults to muted (0) so viewers opt in.
- Diagnostics button can also upload the log and spawn it in-game.

## 1.0.18 - 2026-05-24

### Windows 10 support
- Reworked WGC and D3D11 capture lifecycle handling to avoid Windows 10 freezes during window spawn, resize, and teardown.
- Moved capture resize recreation and shared texture publishing onto safer lifecycle paths so the game update loop is not blocked by slow Windows graphics calls.
- Added current-size shared texture publishing so local panels wait for a valid frame/texture size before binding the renderer bridge.
- Added D3D11 adapter selection and renderer adapter hints so the capture device and renderer device are less likely to land on incompatible GPUs.
- Improved frame callback draining, shared texture release, and D3D resource cleanup to avoid use-after-release and dispose-while-copying races.
- Added more targeted logging around WGC frame pool recreation, shared texture creation, D3D device selection, and cleanup stalls.

### Runtime
- Cleaned up FFmpeg texture lifetime and resize behavior so encoder reinitialization does not reuse released D3D resources.
- Improved Cloudflare tunnel startup and shutdown handling, including process cleanup during local restart builds.
- Kept remote stream ownership and URL rebinding stable across resize-driven encoder replacement.
- Fixed several session cleanup paths so capture, bridge slots, encoders, and streams are released in a more predictable order.

### Shared Texture Bridge
- Added versioned shared texture bridge protocol messages for start, stop, running, stopped, and renderer device hints.
- Added generation checks so stale renderer acknowledgements cannot mark an old shared texture slot as running.
- Added locking around host bridge slot state because the game update path and InterprocessLib callbacks can touch the same slot arrays from different threads.
- Deferred renderer-side native texture/SRV release for a few frames to avoid Unity/render-thread use-after-free during texture replacement.
- Removed unused named shared texture plumbing from the WGC side; the bridge now uses shared handles.

### UI And Session Behavior
- Fixed viewer culling issues and simplified culling so users in no-clip are supported without physics-based checks.
- Improved quick-menu and settings-panel resize behavior while keeping the menu raycast path compatible with normal Resonite camera portals.
- Added adaptive screen lighting support for captured panels.
- Added private-start support for spawned desktop panels and kept private mode restoration stable.
- Improved window polling and auto-spawn behavior so DesktopBuddy tracks new windows with less work on the game thread.

### Diagnostics And Packaging
- Added a diagnostics collector script and included it in the package so users can send logs, Windows details, and existing crash dumps without creating huge live process dumps.

## 1.0.17 - 2026-05-19

### Build And Packaging
- Aligned the manual install zip, local build deploy, and Gale/Thunderstore final install layout so DesktopBuddy uses the same package-style paths everywhere.
- Fixed DesktopBuddy runtime lookup to resolve `DesktopBuddyRuntime` from `BepInEx\plugins\DevL0rd-DesktopBuddyRuntime\DesktopBuddy\DesktopBuddyRuntime`.
- Moved the renderer bridge manual/local deploy path to match Gale's package-wrapped renderer install path.
- Kept the runtime package version unchanged because the runtime payload files did not change.

### Runtime
- Fixed FFmpeg, Cloudflare tunnel, and SoftCam loading after the runtime payload split.
- Fixed the setup panel missing the Install button when the runtime package was installed by Gale but DesktopBuddy was looking in the wrong folder.

## 1.0.15 - 2026-05-18

### Build And Packaging
- Fixed zip layout.
- Split runtime payloads into a separate `DesktopBuddyRuntime` Thunderstore package.

## 1.0.14 - 2026-05-16

### Settings And UI
- Added a dedicated General settings tab after Viewers.
- Added General toggles for showing DesktopBuddy in the context menu and enabling throw-to-destroy.
- Moved experimental spatial audio from Devices to General.
- Changed the settings panel title to show `DesktopBuddy - version`.
- Replaced the generated context-menu Desktop icon with the packaged transparent DesktopBuddy icon.

### Streaming And Audio
- Fixed remote stream audio playback being tied to the owner's local stream preview state.
- Kept stream audio volume local to each user instead of resetting it from shared stream settings.
- Removed the global Audio tab volume slider to avoid confusing shared/local audio control.
- Fixed the remote stream visual missing the current panel curvature on spawn.

### Visual Fixes
- Reworked quick-menu blur so it attaches to the actual quick-menu curved mesh like the settings panel blur.
- Fixed quick-menu blur sizing so it follows the current rounded menu width during expand/collapse and resize.
- Removed custom render-order/render-queue overrides from blur effect materials where they were not needed.

### Build And Packaging
- Packaged and deployed `icon_transparent.png` with the game plugin so runtime UI can use the same icon asset as documentation.
- Kept Thunderstore/package metadata in sync with the new version.
- Disabled Thunderstore publishing while keeping the package layout for GitHub release zips.
- Updated GitHub releases to attach the install zip and show manual Gale/manual BepisLoader install paths before the changelog.
- Moved in-game update links and checks to GitHub releases.

## 1.0.12 - 2026-05-15

### Loader And Distribution
- Converted DesktopBuddy from Resonite Mod Loader to BepisLoader/BepInEx.
- Added Thunderstore-first package metadata with BepisLoader, BepisResoniteWrapper, InterprocessLib, BepInExRenderer, and RenderiteHook declared as dependencies.
- Removed the legacy RML manifest, RML deployment paths, RML config handling, bundled RML/BepInEx reference DLLs, and manual InterprocessLib source copy.
- Added GitHub Actions support for Thunderstore publishing and changed GitHub releases to point users to the Thunderstore package page instead of uploading a second install zip.
- Added a separate Thunderstore README focused on what DesktopBuddy is and what it does.

### First-Run Setup
- Added an in-world DesktopBuddy setup panel that appears from the Desktop context-menu item when local Windows setup is missing or outdated.
- Setup now checks only the Windows pieces DesktopBuddy owns: SoftCam registration, VB-Cable installation, VB-Cable loopback, and the local streaming URL ACL.
- Setup asks for administrator permission only after pressing Install, then refreshes the panel status and waits for Close before continuing startup.
- The setup panel can be closed to continue loading DesktopBuddy even when optional setup is skipped.
- Added packaged setup payload hashes so SoftCam and VB-Cable setup can be rerun when those bundled payloads change.

### Organization And Maintenance
- Reworked DesktopBuddy into a feature-sliced modular monolith with focused App, Configuration, Sessions, Streaming, Capture, Input, UI, Networking, VirtualDevices, Win32, and Utilities areas.
- Split the old DesktopBuddyMod, SessionLoop, SessionSpawner, SettingsPanel, FfmpegEncoder, WgcCapture, and WindowInput monoliths into smaller responsibility-focused files.
- Added shared UI primitives for curved panels, scrollbars, stick scrolling, and common styling.
- Cleaned up naming around SoftCam, VB-Cable, Cloudflare tunnel handling, stream servers, and port forwarding.
- Added `CODE_ORGANIZATION.md` guidance for keeping future changes dry and organized.

### Streaming And Runtime
- Moved shared stream ownership, release, refcount, and cleanup behavior into shared stream lifecycle/registry helpers.
- Split session update behavior into resize, cleanup, virtual device ticking, window polling, and event processing services.
- Improved built-in stream startup and delayed remote URL binding so stream URLs are attached when the encoder and public URL are actually ready.
- Kept Cloudflare tunnel support as the built-in remote HTTPS path while leaving external MediaMTX mode available for users who configure it.

### Settings And UI
- Broke the settings panel into focused tab, layout, primitive, style, viewer row, culling preview, update, and debug modules.
- Kept the current DesktopBuddy visual style while making settings controls reusable and less duplicated.
- Updated README and package documentation for BepisLoader, Thunderstore, current build scripts, and the new release flow.

### Build And Packaging
- Replaced batch build/package scripts with PowerShell scripts.
- Build and package scripts now generate `DesktopBuddySetupPayloads.md5` from the setup payloads.
- Packaging now uses the Thunderstore layout directly and includes `CHANGELOG.md` plus the Thunderstore README.
- Local build/deploy supports Gale profile selection while defaulting to the `Default` profile.
- Removed old setup scripts and no longer packages `INSTALL.txt`.




## 1.0.10 - Previous Release Notes

DesktopBuddy got a major in-world control upgrade in this release, with a new curved Settings panel, a redesigned quick menu, richer streaming controls, and cleaner release/install tooling.

### Highlights
- Added a curved DesktopBuddy Settings panel that matches the main panel shape and opens directly in-world.
- Redesigned the bottom quick menu as a compact hover pill with the new purple-to-blue visual theme.
- Added tabbed settings for viewers, stream quality, networking, virtual devices, debug logs, and update info.
- Added viewer-aware stream controls with per-user enable toggles and live culling status.
- Added frustum and distance-based viewer culling with an in-world preview guide.
- Added local-only stream playback gating so viewers outside the culling area stop receiving the stream while the panel itself remains visible.
- Added a short grace period when viewers leave the culling area so streams do not flicker on brief boundary crossings.
- Added stream presets for resolution, FPS, bitrate, encoder preference, and preferred GPU selection.
- Added network controls for Cloudflare, port forwarding, automatic external IP detection, manual host entry, and stream port settings.
- Added virtual device status cards for SoftCam, VB-Cable, virtual camera, virtual mic, and experimental spatial audio.
- Added a rounded virtual camera preview on the devices page.
- Added an in-game update/info page with project info, GitHub access, current version, changelog display, and reset-to-defaults.
- Added a combined debug log export that includes DesktopBuddy and renderer-side logs.
- Added an in-game debug log viewer that keeps the newest lines visible.

### Streaming And Audio
- Stream audio now plays at full source volume and is controlled locally by each user.
- Owner stream preview uses the same stream playback path as remote viewers.
- Stream playback now starts and stops per user instead of disabling shared panel visuals.
- Stream quality changes are applied through the runtime stream pipeline instead of requiring broad manual resets.

### Visual Polish
- Settings and quick-menu surfaces now use rounded blurred backdrops that match the curved DesktopBuddy panel.
- Controls, section headers, toggles, sliders, text fields, status badges, and scroll areas have been rethemed for a cleaner premium look.
- Status badges have been standardized across network and virtual-device pages.
- Viewer rows now use a cleaner list presentation with avatar-style icons.

### Build, Install, And Packaging
- Build scripts now refresh game-side and renderer-side dependency DLLs from the installed game where appropriate.
- Harmony and game/render-side references are refreshed from the local Resonite install.
- Packaging and installer scripts were updated for the dependency layout at the time.
- Release automation includes the changelog artifact used by the in-game update page.

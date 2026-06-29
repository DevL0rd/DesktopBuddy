## 1.1.0 - 2026-06-29

> **⚠️ Linux support is experimental and may have issues.** The capture → renderer-side path
> is currently a **CPU copy** rather than a full GPU pipeline — due to some complexity this is
> a temporary path and may cause performance issues until it's replaced with a proper
> end-to-end GPU pipeline. (Windows is unaffected.)

### Linux input now works
- Sharing a desktop now injects real input via xdg-desktop-portal `RemoteDesktop`. Capture and
  input share a single portal session, so there's one permission dialog and one restore token;
  re-sharing an already-authorized monitor skips the dialog.
- **All panel interaction is touch** — laser press, drag, and release map to portal touch events
  (`NotifyTouchDown`/`Motion`/`Up`), with the per-hand touch id as the touch slot, so multiple
  hands/users touching a panel are real multitouch. Dragging a panel is a touch-drag.
- Mouse-wheel and controller-stick scrolling work (smooth-axis scroll). Note: scrolling inside
  XWayland apps (e.g. Steam, Discord) can stutter — this is an XWayland smooth-scroll limitation; native-Wayland apps scroll smoothly.

## 1.0.20 - 2026-06-26

### Linux support (EXPERIMENTAL)
DesktopBuddy now runs on native Linux Resonite. **This is an early, experimental port.** The
core "share your desktop into Resonite" flow works, but several features are incomplete or
broken — please read the known issues below before relying on it.

- Desktop/window capture on Wayland via xdg-desktop-portal (PipeWire), with a Linux source
  picker in the context menu and saved sources for one-click re-sharing.
- Native streaming pipeline (FFmpeg) so remote viewers still get a stream.
- Desktop audio captured via PipeWire and muxed into the stream (AAC), excluding Resonite's
  own output.
- Virtual camera via v4l2loopback ("DesktopBuddy - Camera"), with a Devices-tab setup action
  that loads and configures the module. Install `ffmpeg`, `cloudflared`, and `v4l2loopback-dkms`
  yourself first — see the Linux prerequisites in the README; DesktopBuddy cannot install packages.
- Cloudflare tunnel runs natively using your system `cloudflared`.
- GPU frames are shared to the renderer through a native DMA-buf texture bridge.

**Known issues / not working yet on Linux:**
- **Input does not work** — you can see and hear the desktop, but mouse/keyboard input to the
  captured windows is not functional yet.
- **Direct single-window audio is broken** — only full-desktop / native-stream audio works;
  sharing an individual window's audio does not.
- Expect other rough edges; the Linux platform is a work in progress.

(Windows is unaffected and continues to work as before.)

### Settings & UI
- New "Dynamic lights" toggle (General tab, default off): casts an in-game light that matches
  the captured screen's average color.
- New "Windows I open start private" toggle so panels you spawn start in Private mode.
- Stream audio now defaults to muted (volume 0) so viewers opt in to hearing it.
- DesktopBuddy remembers your Linux desktop/window sources for instant re-sharing.
- The Diagnostics button can now also upload the log and spawn it in-game.

### Fixes
- Patched a Resonite engine bug where your avatar's `EyeManager` gets disabled (eyes freeze)
  while you look at your own DesktopBuddy panel. Resonite's idle eye-look targets the panel's
  local-only screen with a synced reference and throws "Cannot reference local targets from a
  non-local reference", which disables the `EyeManager`. This patch keeps the `EyeManager`
  from targeting local-only surfaces so the bug no longer triggers, until Resonite fixes it
  upstream.
- Stability fixes around stream URL binding, private/public toggling, panel resizing, and
  session cleanup.

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
# Renderer-Side Capture Architecture

## Problem

Resonite's "Splittening" architecture runs FrooxEngine (game logic, .NET 10) and Renderite
(Unity renderer) as **separate processes**. The old capture path performed WGC capture on the
game side, then shipped ~29MB BGRA frames across an 8MB named-pipe IPC queue per frame,
resulting in ~5fps and 170ms+ latency on the `Map()` call alone.

## Solution

Renderite already bundles **uWindowCapture (UWC)** — a native Unity plugin that captures
windows directly to GPU-resident `Texture2D` objects. We exploit this by:

1. Telling the game to create a `DesktopTextureProvider` with a **magic DisplayIndex**
2. The renderer plugin intercepts the magic index and plugs in a `UwcDisplaySource`
3. UWC handles all capture natively — **zero IPC per frame, zero CPU copy**

## Data Flow

```
Game Process (FrooxEngine, .NET 10)          Renderer Process (Renderite, Unity/Mono)
─────────────────────────────────────        ─────────────────────────────────────────
                                             DesktopBuddyRendererPlugin (BepInEx)
                                               │
1. User opens window                           │
2. CaptureChannel.RegisterSession(hwnd)        │
   writes to MMF ──────────────MMF──────────►  3. Update() polls MMF, sees StatusStart
                                               4. Creates UwcDisplaySource(hwnd)
                                               5. UwcManager.Find(hwnd) → UwcWindow
                                               6. Writes StatusRunning back to MMF
                                             
7. DesktopTextureProvider.DisplayIndex =       │
   MagicIndexBase + slot                       │
8. FrooxEngine sends SetDesktopTexture-        │
   Properties{displayIndex} via IPC ────────►  9. Harmony prefix on DesktopTextureAsset
                                                  .Handle() intercepts magic index
                                              10. Sets _source = our UwcDisplaySource
                                              11. UwcWindow.texture (GPU Texture2D)
                                                  is now the display texture
                                             
                                              UWC captures at native rate (~60fps)
                                              directly to GPU — no frame data crosses
                                              the process boundary.
```

## Key Components

### Game Side (`DesktopBuddy/`, .NET 10, RML mod)

| File | Purpose |
|------|---------|
| `Capture/CaptureSessionProtocol.cs` | MMF layout: 32 bytes × 16 sessions. Status codes, `MagicIndexBase = 10000` |
| `Capture/CaptureSessionChannel.cs` | Game-side MMF writer. `Open()`, `RegisterSession(hwnd, monitor)` → slot, `StopSession(slot)` |
| `Capture/DesktopTextureProviderPatch.cs` | Harmony prefix on `DesktopTextureProvider.UpdateAsset` — bypasses `UserspaceWorld` check for magic indices, drives `DesktopTexture` lifecycle via reflection |
| `DesktopBuddyMod.cs` | `OpenCaptureChannel()` reads `RenderSystem._messagingHost.QueueName` via reflection. `FinishStartStreaming` creates `DesktopTextureProvider` with magic DisplayIndex. `CleanupSession` calls `CaptureChannel.StopSession()` |
| `DesktopSession.cs` | `Texture` field is now `DesktopTextureProvider`. Added `CaptureSlot` and `LastKnownW/H` for resize tracking |

### Renderer Side (`DesktopBuddyRenderer/`, net472, BepInEx 5 plugin)

| File | Purpose |
|------|---------|
| `DesktopBuddyRendererPlugin.cs` | BepInEx plugin entry. `Update()` polls MMF, manages `UwcDisplaySource` lifecycle. Contains `DesktopTextureAssetHandlePatch` — Harmony prefix that intercepts magic `displayIndex` in `SetDesktopTextureProperties`, sets `_source` to our `UwcDisplaySource`, calls `TextureUpdated()` |
| `UwcDisplaySource.cs` | Implements `IDisplayTextureSource`. Wraps `UwcWindow` found by HWND. `TryBind()` → `UwcManager.Find(hwnd)`, sets `captureMode = BitBlt`, wires `onCaptured`/`onSizeChanged` events |
| `CaptureSessionProtocol.cs` | Renderer-side copy of the protocol (`DesktopBuddy.Shared` namespace) |

### Manager (`DesktopBuddyManager/`)

| File | Purpose |
|------|---------|
| `RendererDepsService.cs` | Auto-installs RenderiteHook + BepInEx.Renderer + copies DesktopBuddyRenderer.dll |
| `MainForm.cs` | 3 status dots for renderer dependency health |

## IPC Side-Channel (MMF)

A memory-mapped file named `{queuePrefix}DesktopBuddy_Cap` carries session control.
Each of the 16 slots is 32 bytes:

```
Offset  Size  Field
 0       4    sessionId (int32)
 4       8    hwnd (int64)
12       8    monitorHandle (int64)
20       4    status (0=idle, 1=start, 2=running, 3=stop)
24       4    width (set by renderer)
28       4    height (set by renderer)
```

The game writes `StatusStart` + hwnd; the renderer reads it, starts UWC capture, writes
`StatusRunning` + dimensions back. On cleanup, the game writes `StatusStop`.

## Magic DisplayIndex

`DesktopTextureProvider.DisplayIndex = 10000 + sessionSlot`

- Normal desktop displays use index 0–N (handled by Renderite's built-in `DuplicableDisplay`)
- Our captures use 10000+ so both Harmony patches (game + renderer) can distinguish them
- Game-side patch skips `UserspaceWorld` check for magic indices
- Renderer-side patch intercepts `SetDesktopTextureProperties` and plugs in `UwcDisplaySource`

## What Still Uses the Old Path

- **MJPEG remote streaming** — `DesktopStreamer` / `WgcCapture` still run WGC on the game
  side for the encoding pipeline (`OnGpuFrame` → `FfmpegEncoder`). This is independent of
  the local in-game display and is only active when remote streaming is enabled.
- **`DesktopTextureSource`** — Dead code, will be deleted in cleanup pass.

## Build

```
scripts/build.bat        # builds both DesktopBuddy.csproj + DesktopBuddyRenderer.csproj
scripts/build.bat -r     # also kills/restarts Resonite
```

Renderer plugin requires `lib/BepInEx.dll` and `lib/0Harmony.dll` (BepInEx 5.4.22) plus
Unity/Renderite DLLs from the Resonite Renderer Managed folder. NuGet.config adds the
BepInEx feed but the project currently uses direct DLL references from `lib/`.

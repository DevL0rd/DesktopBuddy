# Streaming Migration Plan: Game → Renderer

## Goal
Move video encoding + HTTP serving from the game (FrooxEngine) process to the renderer (Renderite/Unity) process. The renderer already captures window textures via UWC — reuse those for NVENC/AMF encoding instead of running a separate WGC capture on the game side.

## Current Architecture

```
GAME PROCESS (FrooxEngine, .NET 10)              RENDERER PROCESS (Unity 2019, net472)
┌─────────────────────────────────┐              ┌──────────────────────────────┐
│ WgcCapture → GPU texture        │              │ UWC → Unity Texture2D        │
│   ↓                             │   MMF slot   │   ↓                          │
│ FfmpegEncoder (NVENC/AMF)       │ ←──────────→ │ DisplayDriverPatch           │
│   ↓                             │              │   ↓                          │
│ MPEG-TS ring buffer             │              │ DesktopTextureAsset (display) │
│   ↓                             │              └──────────────────────────────┘
│ MjpegServer (:48080)            │
│   ↓                             │
│ cloudflared tunnel              │
│                                 │
│ AudioCapture (WASAPI loopback)  │
│   ↓ dual output:               │
│   → FfmpegEncoder (AAC mux)    │
│   → DesktopAudioSource (local)  │
└─────────────────────────────────┘
```

## Target Architecture

```
GAME PROCESS (FrooxEngine)                       RENDERER PROCESS (Unity 2019)
┌─────────────────────────────────┐              ┌──────────────────────────────────┐
│ AudioCapture (WASAPI loopback)  │              │ UWC → Unity Texture2D            │
│   ↓ dual output:               │  audio MMF   │   ↓ (already captured)           │
│   → audio MMF (PCM to renderer)│ ────────────→│ FfmpegEncoder (NVENC/AMF)        │
│   → DesktopAudioSource (local)  │              │   + audio from MMF → AAC mux     │
│                                 │              │   ↓                              │
│ VideoTextureProvider            │  stream URL  │ MPEG-TS ring buffer              │
│   ← URL from MMF               │ ←──────────  │   ↓                              │
│                                 │              │ MjpegServer (:48080)             │
│ Session UI / context menu       │   MMF slot   │   ↓                              │
│   → spawn/destroy via MMF       │ ────────────→│ cloudflared tunnel               │
└─────────────────────────────────┘              └──────────────────────────────────┘
```

## What Moves to Renderer
- `FfmpegEncoder.cs` — adapted for net472 + Unity D3D11 texture access
- `MjpegServer.cs` — HTTP server for MPEG-TS streaming
- cloudflared process management

## What Stays Game-Side
- `AudioCapture` (WASAPI process loopback) — needs PID, feeds both renderer stream + local spatial audio
- `AudioRouter` — COM per-process audio device redirect
- `VirtualMic` / `VirtualCamera` — Resonite integration
- `DesktopAudioSource` — Resonite spatial audio from AudioCapture ring
- Session management UI, context menu, `DesktopSession` tracking
- `VideoTextureProvider` URL setup (reads stream URL from MMF)

## What Gets Removed from Game-Side
- `WgcCapture` usage for streaming (entire class can stay for potential fallback but won't be called)
- `FfmpegEncoder` — no longer used game-side
- `MjpegServer` — moved to renderer
- `ConnectEncoder()` / `_sharedStreams` / `OnGpuFrame` callback chain
- `DesktopStreamer` no longer needs to do capture for streaming (only used for initial size probe)

---

## Implementation Phases

### Phase 1: Extend MMF Protocol
Add streaming fields to the session slot so the game can request streaming and the renderer can report back.

**Changes to `CaptureSessionProtocol` (both copies):**
- Increase `SessionSize` to fit new fields
- New fields per slot:
  - `[+32]` streamStatus (int32): 0=none, 1=requested, 2=active, 3=stopping
  - `[+36]` streamPort (int32): HTTP port the renderer is serving on (written by renderer)
  - `[+40..+295]` streamUrl (256-byte UTF8 string): full tunnel URL (written by renderer, or game composes from port)

**Game side:** After creating a session, write `streamStatus = 1` to request streaming.
**Renderer side:** On seeing `streamStatus == 1`, start encoder + server, write back port, set `streamStatus = 2`.

### Phase 2: Port FFmpeg Encoder to Renderer

**New files in `DesktopBuddyRenderer/`:**
- `FfmpegEncoder.cs` — adapted from game-side version:
  - net472 compatible (no `Span<T>`, use `unsafe` pointers)
  - Get D3D11 texture pointer via `UwcDisplaySource.UnityTexture.GetNativeTexturePtr()`
  - Same NVENC/AMF codec selection
  - Same ring buffer + MPEG-TS muxing
  - FFmpeg DLLs loaded from `../../ffmpeg/` relative to renderer

**Key adaptation:** Instead of receiving a texture via `QueueFrame()` callback, the encoder pulls the UWC texture each frame from the active `UwcDisplaySource`. The renderer's `Update()` loop drives encoding.

### Phase 3: Port MjpegServer to Renderer

**New file in `DesktopBuddyRenderer/`:**
- `MjpegServer.cs` — copied mostly as-is, net472 compatible
  - `HttpListener` works on net472
  - Routes `/stream/{slotId}` to the encoder for that session

### Phase 4: Audio Bridge (PCM Pipe)

**New shared MMF for audio per session:**
- Game's `AudioCapture` writes raw float32 PCM samples to a ring buffer in shared memory
- Renderer reads from it and feeds to FFmpeg AAC encoder
- Simple layout: `[0..3] writePos (int32), [4..7] readPos (int32), [8..N] float32 PCM ring`
- Name: `{prefix}DesktopBuddy_Audio_{slot}`

**Game side changes:**
- `AudioCapture` gets a `WriteToSharedRing(mmfAccessor)` method called alongside existing ring writes
- Or: a new `AudioBridge` class wraps the MMF write

**Renderer side:**
- `AudioBridge` class reads PCM from MMF, feeds to encoder's audio stream

### Phase 5: cloudflared on Renderer

**Move tunnel management to renderer:**
- Renderer launches `cloudflared` pointing at its own HTTP port
- Writes tunnel URL back to MMF (or a separate small MMF)
- Game reads URL, sets on `VideoTextureProvider`

**Alternative:** Keep cloudflared on game side, just point it at the renderer's port. Simpler — cloudflared doesn't care which process hosts the HTTP server.

**Decision:** Keep cloudflared game-side for now (less disruption). Game knows the renderer's stream port from MMF, launches cloudflared pointing to `localhost:{rendererPort}`.

### Phase 6: Clean Up Game Side

- Remove `WgcCapture` calls from streaming path
- Remove `FfmpegEncoder` instantiation
- Remove `MjpegServer` creation
- Remove `ConnectEncoder()`, `_sharedStreams`
- `DesktopStreamer` simplified to just probe window size (or get size from MMF width/height fields)
- `FinishStartStreaming` reads stream port/URL from MMF once renderer reports `streamStatus == 2`

---

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| net472 lacks `Span<T>` | Use `unsafe` pointers for buffer ops, same as FFmpeg.AutoGen already does |
| FFmpeg.AutoGen NuGet on net472 | v7.x supports netstandard2.0 which net472 can consume |
| GPU texture format mismatch | UWC gives BGRA8 — same format WGC gives, so same encoder path works |
| Unity main thread restriction | Encoding runs on background thread, only texture ptr read on main thread |
| Audio latency across processes | PCM ring buffer with ~100ms depth, same as current in-process ring |
| Renderer crashes | Game detects `streamStatus` stuck, can fall back or show error |

## File Summary

### New Renderer Files
- `DesktopBuddyRenderer/Encoding/FfmpegEncoder.cs`
- `DesktopBuddyRenderer/Networking/MjpegServer.cs`
- `DesktopBuddyRenderer/Audio/AudioBridge.cs`

### Modified Files
- `CaptureSessionProtocol.cs` (both copies) — extended session slot
- `DesktopBuddyRendererPlugin.cs` — encoder lifecycle, audio bridge, HTTP server
- `DesktopBuddyRenderer.csproj` — add FFmpeg.AutoGen, new files
- `DesktopBuddyMod.cs` — remove encoder/server, read stream URL from MMF, audio bridge writes
- `CaptureSessionChannel.cs` — write/read streaming fields

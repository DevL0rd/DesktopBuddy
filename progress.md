# Renderer-Side UWC Implementation Progress

> **Key discovery:** Renderite already has **uWindowCapture (UWC)** running!
> `UwcManager.instance.Find(hwnd)` → `UwcWindow.texture` → Unity `Texture2D` directly.
> Zero custom D3D11, zero compute shaders, zero WinRT interop needed.

> **Design:** `DesktopStreamer` stays independent for MJPEG/remote streaming.
> The renderer plugin handles *only* the local in-game display (GPU-native, zero IPC).
> **NO fallback to old IPC texture path.** Old `DesktopTextureSource` is dead code to be cleaned up later.

## Step 0: Prerequisites (DesktopBuddyManager)
- [x] Create `RendererDepsService.cs` — check/install RenderiteHook, BepInEx.Renderer, DesktopBuddyRenderer
- [x] Add 3 status dots to MainForm (RenderiteHook / BepInEx.Renderer / RendererPlugin)
- [x] Wire into DoInstall + RefreshStatusAsync

## Step 1: Game-Side Mod Changes
- [x] Create `CaptureSessionProtocol.cs` — shared MMF layout (32 bytes/session, MagicIndexBase=10000)
- [x] Create `CaptureSessionChannel.cs` — MMF side-channel writer (Open, RegisterSession, StopSession)
- [x] Open `CaptureSessionChannel` in `OnEngineInit` via `OpenCaptureChannel()` (reflection on `RenderSystem._messagingHost.QueueName`)
- [x] Harmony-patch `DesktopTextureProvider.UpdateAsset` to remove `UserspaceWorld` check (`DesktopTextureProviderPatch.cs`)
- [x] Modify `FinishStartStreaming` — use `DesktopTextureProvider` + magic DisplayIndex (no fallback)
- [x] Update `DesktopSession` — add `CaptureSlot` (int), `LastKnownW/H`, change `Texture` field to `DesktopTextureProvider`
- [x] Update resize path — renderer-side UWC handles texture resize, game only updates UI layout
- [x] Wire `CaptureChannel.StopSession()` into CleanupSession

## Step 2: DesktopBuddyRenderer (BepInEx Plugin) — UWC approach
- [x] Create `DesktopBuddyRenderer/DesktopBuddyRenderer.csproj` (net472, BepInEx.Core, Renderite.Unity, Assembly-CSharp refs)
- [x] Rewrite `DesktopBuddyRendererPlugin.cs` — Harmony patch on `DesktopTextureAsset.Handle`, MMF polling, UwcDisplaySource management
- [x] Create `UwcDisplaySource.cs` — `IDisplayTextureSource` wrapping `UwcWindow` (BitBlt capture, onCaptured/onSizeChanged events)
- [x] Create `CaptureSessionProtocol.cs` (renderer-side copy, `DesktopBuddy.Shared` namespace)
- [x] Add to solution (`DesktopBuddy.sln` updated)
- [x] Delete old `RendererCaptureSession.cs` (DIY D3D11/WGC — superseded by UWC)

## Step 3: Build Integration
- [x] Update `scripts/build.bat` to build DesktopBuddyRenderer
- [x] Update `DesktopBuddyManager.csproj` to embed DesktopBuddyRenderer.dll as payload
- [x] Add `NuGet.config` with BepInEx feed + direct DLL refs in `lib/`
- [x] Verify all projects compile (0 errors both sides)

## Step 4: Documentation
- [x] Create `RENDERER_CAPTURE_OVERVIEW.md` — architecture, data flow, component index

## Step 5: Cleanup (later)
- [ ] Delete `DesktopTextureSource.cs` entirely
- [ ] Gut `WgcCapture.cs` — remove `_textureTarget`, `SetTextureTarget()`, staging/Map/WriteFrameDirect
- [ ] Remove `DesktopStreamer.SetTextureTarget()`
- [ ] Create overview document

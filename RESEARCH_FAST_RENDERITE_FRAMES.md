# Plan: Renderer-Side WGC Capture (GPU-Native, Zero IPC Per Frame)

## The Core Idea

Move the WGC capture **entirely into the Renderite (Unity) process**. The game-side mod tells the renderer plugin *which window or monitor* to capture via a lightweight side-channel (named shared memory or a named pipe — just a string). The renderer plugin runs WGC + BGRA→RGBA convert on its own D3D11 device, produces a native Unity `Texture2D` from the captured frame, and updates it every frame — all GPU-local, zero IPC, zero CPU readback.

The FrooxEngine side becomes trivial: create a `DesktopTextureProvider`-style renderer asset (or a custom `DynamicRendererAsset`) that the renderer plugin populates directly. Alternatively, use Unity's `Texture2D.CreateExternalTexture` with a shared D3D11 texture handle, so both sides reference the same GPU allocation.

The existing game-side code (`DesktopStreamer`, `WgcCapture`) **stays for the MJPEG/remote streaming path** — that path encodes with NVENC and is already smooth. Only the local in-game display changes.

---

## Why the Current Path Is Slow

```
WGC frame arrives (GPU texture, BGRA)
    → compute shader BGRA→RGBA (GPU, fast ~1ms)
    → CopyResource to staging texture (GPU→CPU boundary)
    → ID3D11DeviceContext::Map()               ← ~170ms GPU readback on 5120×1440
    → memcpy into SharedMemoryBlockLease       ← ~5ms, 29MB
    → SetTexture2DProperties IPC cmd          ← background queue
    → SetTexture2DFormat IPC cmd              ← background queue
    → SetTexture2DData IPC cmd (29MB via shm) ← background queue, 8MB cap
    → Renderite receives, uploads to Unity GPU texture
    → callback: _uploadInFlight = false
```

**Total round-trip: ~200ms. Effective fps: ~5.**

The background queue is 8MB. One 5120×1440 RGBA frame is 29MB. Flooding it with fire-and-forget calls (our first attempt) blocked all other asset loading. The `_uploadInFlight` gate prevents flooding but caps us at one frame per round-trip.

---

## Why This Approach Works

- `VideoTexture` and `DesktopTexture` (the built-in ones) are `DynamicRendererAsset` — they live entirely on the renderer side. No per-frame pixel data crosses IPC at all.
- `DesktopTexture` in particular does WGC on the renderer side already (see `DesktopTextureProvider` → `SetDesktopTextureProperties` command → renderer captures the display). We want the same GPU-native path but with **window-level selection** and a communication channel from our mod.
- Unity 2019.4 supports `Texture2D.CreateExternalTexture(IntPtr nativeTex)` where `nativeTex` is an `ID3D11Texture2D*` or `ID3D11ShaderResourceView*`. We can create a D3D11 shared texture (DXGI NT handle), GPU-copy into it from WGC on the renderer side, and Unity wraps it — no CPU ever touches it.

---

## Architecture Overview

```
┌─────────────────────────────────┐       ┌──────────────────────────────────────────┐
│  FrooxEngine (Resonite .NET)    │       │  Renderite (Unity 2019.4.19f1)           │
│                                 │       │                                          │
│  DesktopBuddyMod (RML)         │       │  DesktopBuddyRenderer (BepInEx plugin)   │
│    ┌───────────────────────┐    │       │    ┌──────────────────────────────────┐  │
│    │ DesktopTextureSource  │    │       │    │ RendererCaptureSession           │  │
│    │  (thin FrooxEngine    │    │       │    │  - WGC via WinRT                 │  │
│    │   component, creates  │◄───┼───────┼────│  - BGRA→RGBA compute shader      │  │
│    │   asset ID & slot)    │    │  IPC  │    │  - GPU copy → Unity Texture2D    │  │
│    └───────────────────────┘    │  (cmd)│    │  - UpdateExternalTexture each frm│  │
│                                 │       │    └──────────────────────────────────┘  │
│    Side-channel (shm/pipe):     │       │                                          │
│    "capture window=0xABCD"  ────┼───────┼───►  reads target, starts WGC session   │
│    "capture monitor=\\.\D1" ────┼───────┼───►  or monitor capture                 │
└─────────────────────────────────┘       └──────────────────────────────────────────┘
```

---

## Part 1: Injecting BepInEx into Renderite — Use RenderiteHook

### What RenderiteHook Does (source confirmed)

`RenderiteHook` is an RML mod (`rml_mods/RenderiteHook.dll`) that patches `RenderSystem.StartRenderer` using a Harmony IL transpiler. It intercepts after the `StringBuilder.ToStringAndClear()` call that assembles the renderer launch args string, then:

1. **`CopyDoorstopFiles(renderSystem)`** — copies `winhttp.dll` + BepInEx doorstop config from a `Doorstop/` subfolder next to itself into the Renderite directory (`renderSystem.RendererPath` → directory).
2. **Appends** `--doorstop-enabled true --doorstop-target-assembly <BepInEx.dll path>` to the args string passed to Bootstrapper.

Resonite's `RenderSystem.StartRenderer()` (decompiled, confirmed):
```csharp
private async Task<Process> StartRenderer()
{
    string text = $"-QueueName {_messagingHost.QueueName} -QueueCapacity {_messagingHost.QueueCapacity}";
    if (_bootstrapper != null)
        return await _bootstrapper.StartRenderer(text);  // ← RenderiteHook patches here
    return Process.Start(new ProcessStartInfo(RendererPath, text) {
        UseShellExecute = false,
        WorkingDirectory = "Renderer"
    });
}
```

`BootstrapperManager.StartRenderer(string startArgs)` sends `startArgs` over the bootstrapper named pipe, the bootstrapper process launches `Renderite.Renderer.exe` with those args.

**`renderSystem.RendererPath`** = `{AppPath}/Renderer/Renderite.Renderer.exe` — Doorstop files go into `{AppPath}/Renderer/`.

### Why We Use RenderiteHook Instead of DIY

The Harmony IL transpiler on an async state machine `MoveNext` is the most brittle kind of IL patching. The compiler-generated state machine class name (`<StartRenderer>d__XX`) and IL offsets shift whenever Resonite is compiled. RenderiteHook is already battle-tested, maintained by the Resonite modding community, and handles this correctly. We eliminate the maintenance burden entirely.

**Our approach**: DesktopBuddyManager auto-installs RenderiteHook (alongside BepInEx.Renderer) from its GitHub releases — the user never manually touches it. It appears as a checked status dot in the manager UI. DesktopBuddy itself does **zero injection code**.

---

## Part 2: What DesktopBuddyManager Auto-Installs

Three components, all installed/updated silently by DesktopBuddyManager with status dots:

### RenderiteHook (RML mod)
- Installed to: `{ResonitePath}/rml_mods/RenderiteHook.dll`
- GitHub: `https://github.com/ResoniteModding/RenderiteHook/releases/latest`
- This handles all Doorstop injection into Renderite — we do nothing
- Brings its own `Doorstop/winhttp.dll` + `.doorstop_config` as a subfolder alongside the DLL

### BepInEx.Renderer (BepInEx for Unity/Renderite)
From confirmed releases (v5.4.233001, Sep 2025):
- **BepInEx framework files** for Unity Mono: `BepInEx/core/` DLLs, `BepInEx.Unity.Mono.Launcher.dll`
- Doorstop NOT included (RenderiteHook handles it)
- Installed to: `{ResonitePath}/Renderer/BepInEx/`
- GitHub: `https://github.com/ResoniteModding/BepInEx.Renderer/releases/download/v5.4.233001/ResoniteModding-BepInExRenderer-5.4.233001.zip`

### DesktopBuddyRenderer (our BepInEx plugin)
- Installed to: `{ResonitePath}/Renderer/BepInEx/plugins/DesktopBuddyRenderer.dll`
- Built by our solution, copied there by DesktopBuddyManager

DesktopBuddyManager check order:
1. Check `{ResonitePath}/rml_mods/RenderiteHook.dll` → install if missing
2. Check `{ResonitePath}/Renderer/BepInEx/core/` → install BepInEx.Renderer if missing
3. Check `{ResonitePath}/Renderer/BepInEx/plugins/DesktopBuddyRenderer.dll` → install/update if missing or outdated

---

## Part 3: The Renderer Plugin — DesktopBuddyRenderer

This is a **BepInEx plugin** (Unity Mono, .NET 4.x) running inside Renderite. It:

### 3a. Starts at game load
```csharp
[BepInPlugin("net.desktopbuddy.renderer", "DesktopBuddyRenderer", "1.0.0")]
public class DesktopBuddyRendererPlugin : BaseUnityPlugin
{
    void Awake()
    {
        StartCoroutine(CaptureLoop());
        // Open named shared memory / pipe to receive commands from game side
        _commandChannel = new NamedPipeServerStream("DesktopBuddy_RenderCmd_" + pid);
    }
}
```

### 3b. Command channel: what the game side sends

A simple protocol over a **named pipe** (or memory-mapped file). The key is the Resonite process PID so each session is unique.

Message format (simple text line):
```
START_WINDOW 0xABCD1234      ← hwnd as hex
START_MONITOR \\.\DISPLAY1   ← monitor device name
STOP
```

The FrooxEngine mod sends this when `DesktopTextureSource.Initialize()` is called — we know the HWND or monitor handle at that point because `SpawnStreaming(world, hwnd, title, monitorHandle)` already has it.

### 3c. WGC Capture on the Renderer Side

**Reuse our existing WgcCapture code** — it's pure D3D11 + WinRT, no FrooxEngine dependencies. We extract the core into a shared assembly or just copy the relevant parts:

- `WgcCapture` (D3D11 device creation, WinRT frame pool, compute shader BGRA→RGBA)
- `BgraToRgba.cso` (embedded shader)  
- Window/monitor capture via `IGraphicsCaptureItemInterop`

In the renderer plugin, we **skip the staging + Map path entirely**. Instead:

```
WGC frame arrives (GPU texture, BGRA, on renderer's D3D11 device)
    → compute shader BGRA→RGBA into _convertedTexture  (~1ms)
    → GPU copy _convertedTexture → _sharedTexture       (~0.5ms)  
    → Unity: tex.UpdateExternalTexture(sharedTexPtr)    (~0ms, just pointer swap)
    → Unity renders with tex next frame
```

No Map. No CPU copy. No IPC. **Total ~2ms.**

### 3d. Linking to a Unity Texture

```csharp
// On first frame or resize:
_sharedTex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
// Get Unity's native D3D11 device via SystemInfo / native plugin interface
// Open or create shared D3D11 texture on same device
// Pass native ptr:
_unityTex = Texture2D.CreateExternalTexture(width, height, TextureFormat.RGBA32, false, true, nativeTexPtr);

// Each frame:
_unityTex.UpdateExternalTexture(newNativePtr); // if ptr changed (resize)
// Or simpler: just GPU-copy into the same texture Unity already wraps
```

For simplicity, we can use a **single shared D3D11 texture** (created by the renderer plugin on Unity's device). The WGC compute shader writes RGBA into it. Unity wraps it with `CreateExternalTexture`. Each frame we just dispatch the compute shader — Unity's texture pointer never changes, no `UpdateExternalTexture` needed.

### 3e. How the Game Side References It

The FrooxEngine side needs a texture asset ID that the renderer knows about. Two approaches:

**Option A: Custom DynamicRendererAsset command** — send a new IPC command type `StartRendererCapture { assetId, pipeName }` that registers the capture session in Renderite. The renderer plugin intercepts this (via Harmony patch on command handler) and starts the WGC session for that asset ID. Then the renderer plugin updates that asset's Unity texture directly. This requires patching the renderer's command handler.

**Option B: Use DesktopTexture asset class** — the existing `DesktopTexture` / `DesktopTextureProvider` already has a registered asset manager on the renderer side (`RenderSystem.DesktopTextures`). We patch the renderer side's `SetDesktopTextureProperties` handler to accept our custom display index encoding (e.g. negative index = read HWND from side channel). This is the cleanest — no new IPC command types.

**Option C (simplest): Side-channel texture name** — game side creates a `DesktopTextureProvider` with a special `DisplayIndex` value (e.g. `9999 + sessionId`). Renderer plugin Harmony-patches the Unity-side handler for `SetDesktopTextureProperties`, intercepts our magic index, and instead starts a WGC session targeting the HWND from the side channel.

**Recommended: Option B/C** — piggyback on the existing `DesktopTextureProvider`/`DesktopTexture` flow. The game side creates a `DesktopTextureProvider`, we intercept it in the renderer plugin and redirect to WGC capture.

---

## Part 4: What Stays in the Game Side Mod

| Component | Keep? | Notes |
|-----------|-------|-------|
| `WgcCapture.cs` (full) | YES — for MJPEG/remote streaming path | Encoding side still needs it |
| `DesktopStreamer.cs` | YES — for MJPEG | Same |
| `FfmpegEncoder.cs` | YES | Remote streaming |
| `MjpegServer.cs` | YES | Remote streaming |
| `DesktopTextureSource.cs` | REPLACE | Replace with `DesktopTextureProvider` usage + side-channel command |
| `WindowEnumerator.cs` | YES | Still needed to enumerate windows by HWND for the user to pick |
| `DesktopBuddyMod.cs` — SpawnStreaming | MODIFY | Send HWND to renderer plugin instead of doing WGC locally |

The key change in `DesktopBuddyMod.SpawnStreaming`:
```csharp
// OLD: creates DesktopStreamer (WGC on game side)
// NEW: writes HWND to side channel, creates DesktopTextureProvider component
//      with magic DisplayIndex, renderer plugin picks up the HWND and starts WGC

var texSlot = root.AddSlot("Texture", persistent: false);
texSlot.Tag = "Local";
var provider = texSlot.AttachComponent<DesktopTextureProvider>();
provider.DisplayIndex.Value = _captureSessionRegistry.Register(hwnd, monitorHandle);
// Registry maps session int → side-channel entry
// Renderer plugin reads the side-channel when it sees that DisplayIndex
```

No more `DesktopTextureSource`, no more `_uploadInFlight`, no more `SetFromBitmap2D`.

---

## Part 5: Side-Channel Design

A **named memory-mapped file** per Resonite session (using `engine.SharedMemoryPrefix` as prefix). Layout:

```
[4 bytes: sessionId]
[8 bytes: HWND (IntPtr)]
[8 bytes: monitorHandle (IntPtr)]  
[4 bytes: status flags]  // 0=idle, 1=start, 2=stop
```

- Game side writes: sessionId, HWND/monitorHandle, sets status=start
- Renderer plugin polls or uses an event to detect status=start, starts WGC, sets status=running
- Game side polls for running confirmation
- On stop: game side sets status=stop, renderer plugin tears down WGC session

Named: `"{engine.SharedMemoryPrefix}_DesktopBuddy_Cap_{sessionId}"`

Since `engine.SharedMemoryPrefix` is already used for the main Renderite IPC (`_messagingHost.QueueName` is based on it), the renderer plugin can reconstruct it by reading the `QueueName` argument from its command-line args (`-QueueName {prefix}Primary`).

---

## Implementation Steps

### Step 0: Prerequisites (DesktopBuddyManager)
- Check `{ResonitePath}/rml_mods/RenderiteHook.dll` → download from GitHub releases if missing
  - RenderiteHook ships its `Doorstop/` subfolder alongside the DLL — extract preserving structure
- Check `{ResonitePath}/Renderer/BepInEx/core/` → download BepInEx.Renderer if missing
  - URL: `https://github.com/ResoniteModding/BepInEx.Renderer/releases/download/v5.4.233001/ResoniteModding-BepInExRenderer-5.4.233001.zip`
  - Extract into `{ResonitePath}/Renderer/`
- Copy `DesktopBuddyRenderer.dll` into `{ResonitePath}/Renderer/BepInEx/plugins/`
- Add 3 status dots to MainForm: RenderiteHook / BepInEx.Renderer / RendererPlugin

### Step 1: DesktopBuddy.dll (game-side RML mod — NO injection code needed)
- **No Harmony patch on StartRenderer** — RenderiteHook handles all of that
- Replace `DesktopTextureSource` usage with `DesktopTextureProvider` + session registry
- Write HWND/monitorHandle to MMF side-channel when starting a session

### Step 2: New project — `DesktopBuddyRenderer` (BepInEx plugin, Unity Mono .NET 4.x)
- New project in solution: `DesktopBuddyRenderer/DesktopBuddyRenderer.csproj`
- Target: `net472` (Unity Mono inside Renderite)
- References: `UnityEngine.dll` from Renderite, `BepInEx.dll`, `0Harmony.dll`
- Extract `WgcCapture` core (D3D11/WinRT capture + compute shader) into shared assembly OR recompile for .NET 4.7.2
- Plugin reads `QueueName` from CLI args to reconstruct shared memory prefix
- Harmony-patches renderer-side `SetDesktopTextureProperties` handler
- On magic DisplayIndex: read MMF, get HWND, start WGC on own D3D11 device (Unity's)
- Each WGC frame: compute convert → GPU copy → `UpdateExternalTexture` on Unity texture

### Step 3: Build Integration
- `scripts/build.bat` compiles both `DesktopBuddy.dll` and `DesktopBuddyRenderer.dll`
- `package.bat` copies `DesktopBuddyRenderer.dll` into the release package
- DesktopBuddyManager installs it to the right BepInEx plugins folder

---

## Key Code References

### RenderSystem.StartRenderer (decompiled — what we patch)
```csharp
// RenderSystem.cs line ~372873
private async Task<Process> StartRenderer()
{
    // text = "-QueueName {prefix}Primary -QueueCapacity 8388608"
    string text = $"-{"QueueName"} {_messagingHost.QueueName} -{"QueueCapacity"} {_messagingHost.QueueCapacity}";
    if (_bootstrapper != null)
        return await _bootstrapper.StartRenderer(text); // bootstrapper sends to Resonite.exe which launches Renderite
    return Process.Start(new ProcessStartInfo(RendererPath, text) { ... });
}
// RendererPath = "{AppPath}/Renderer/Renderite.Renderer.exe"
```

### BootstrapperManager.StartRenderer (what happens after patch)
```csharp
// Sends startArgs over named pipe _bootstrapperIn
// Bootstrapper process launches Renderite.Renderer.exe with those args
// Waits for "RENDERITE_STARTED:{pid}" response
// Returns Process.GetProcessById(pid)
```

### RenderiteHook (what it does so we don't have to)
- Harmony IL transpiler on `RenderSystem.<StartRenderer>d__XX.MoveNext`
- Finds `ToStringAndClear` in the IL stream, injects `OpCodes.Call` to its hook after it
- Hook copies `Doorstop/winhttp.dll` + `.doorstop_config` to `{AppPath}/Renderer/`, appends doorstop args
- **We install it, we don't replicate it**

### DesktopTexture (decompiled — what the renderer side handles)
```csharp
public class DesktopTexture : DynamicRendererAsset<DesktopTexture>, ITexture2D
{
    public void Update(int index, Action onUpdated)
    {
        // Sends SetDesktopTextureProperties { assetId, displayIndex } to renderer
        // Renderer captures that display index and calls back
    }
    public void HandlePropertiesUpdate(DesktopTexturePropertiesUpdate update) { ... }
}
```

### DesktopTextureProvider (decompiled — what we reuse on game side)
```csharp
// Only works in Userspace world currently (check in UpdateAsset)
// We either patch away the world check, or create our own minimal asset type
// that sends the same SetDesktopTextureProperties command with our magic index
protected override void UpdateAsset()
{
    if (base.World != Userspace.UserspaceWorld) return; // ← patch this out
    _desktopTex.Update(DisplayIndex.Value, OnTextureCreated);
}
```

### Unity APIs (Renderite = Unity 2019.4.19f1)
```csharp
// Create Unity texture wrapping a D3D11 texture pointer
// nativeTex = ID3D11Texture2D* or ID3D11ShaderResourceView*
Texture2D.CreateExternalTexture(int w, int h, TextureFormat.RGBA32, false, true, IntPtr nativeTex)

// If the underlying pointer changes (resize):
tex.UpdateExternalTexture(IntPtr newNativeTex)

// To get Unity's own D3D11 device for sharing:
SystemInfo.graphicsDeviceType // should be Direct3D11
// Use GL.IssuePluginEvent or native plugin to get ID3D11Device*
```

---

## What We Do NOT Need

- **BepisLoader** — that's for BepInEx in the FrooxEngine process. We run in the Renderite (Unity) process.
- **DIY Harmony transpiler on StartRenderer** — RenderiteHook already does this; no IL patching in DesktopBuddy.
- **Encoding / MJPEG for local view** — WGC runs on the renderer's GPU device natively.
- **SetFromBitmap2D / _uploadInFlight / background IPC queue** — gone from local display path.
- **GPU readback (Map/staging)** — eliminated entirely. Frame never leaves GPU.

---

## Expected Performance

| Path | Now | After |
|------|-----|-------|
| GPU readback | ~170ms | **0ms** |
| CPU memcpy | ~5ms | **0ms** |
| IPC (29MB/frame) | ~30ms | **0ms** |
| Compute shader convert | ~1ms | ~1ms (same) |
| Unity texture update | N/A | ~0.5ms (GPU copy) |
| **Effective FPS** | **~5 fps** | **~60 fps (WGC native)** |

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using HarmonyLib;
using Renderite.Unity;
using UnityEngine;
using UnityEngine.Rendering;

namespace DesktopBuddyRenderer
{
    [BepInPlugin("net.desktopbuddy.renderer", "DesktopBuddyRenderer", "1.0.0")]
    public class DesktopBuddyRendererPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static DesktopBuddyRendererPlugin Instance;

        private string _queuePrefix;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private float _pollTimer;
        private const float PollInterval = 0.1f; // 100ms

        // Diagnostic counters
        private int _pollCount;
        private float _diagTimer;
        private const float DiagInterval = 5.0f; // dump state every 5s
        private int _frameCount;

        // Maps session slot → active UwcDisplaySource
        private readonly Dictionary<int, UwcDisplaySource> _activeSources = new Dictionary<int, UwcDisplaySource>();

        // Maps magic displayIndex → UwcDisplaySource (for the Harmony patch to look up)
        private static readonly Dictionary<int, UwcDisplaySource> _indexToSource = new Dictionary<int, UwcDisplaySource>();

        // Pending binds — UWC may not have enumerated the window yet
        private readonly List<(int slot, UwcDisplaySource source)> _pendingBinds = new List<(int, UwcDisplaySource)>();

        // Streaming infrastructure
        private MjpegServer _streamServer;
        private CloudflareTunnel _tunnel;
        private readonly Dictionary<int, FfmpegEncoder> _slotEncoders = new Dictionary<int, FfmpegEncoder>();
        private readonly Dictionary<int, AudioBridge> _slotAudioBridges = new Dictionary<int, AudioBridge>();
        private IntPtr _d3dDevice;
        private readonly object _d3dContextLock = new object();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ID3D11DeviceChildGetDevice(IntPtr self, out IntPtr ppDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IUnknownQueryInterface(IntPtr self, ref Guid riid, out IntPtr ppvObject);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint IUnknownRelease(IntPtr self);

        // IID_ID3D11VideoDevice = {10EC4D5B-975A-4689-B9E4-D0AAC30FE333}
        private static readonly Guid IID_ID3D11VideoDevice = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");


        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo("DesktopBuddyRenderer starting...");

            _queuePrefix = ParseQueuePrefix();
            if (_queuePrefix == null)
            {
                Log.LogWarning("Could not parse QueueName from command line — side-channel disabled");
                return;
            }

            // Apply Harmony patches
            var harmony = new Harmony("net.desktopbuddy.renderer");
            harmony.PatchAll();

            // Start streaming server + tunnel immediately
            // (they wait for encoder requests via MMF, but server+tunnel need time to come up)
            _streamServer = new MjpegServer(48080);
            _streamServer.Start();

            _tunnel = new CloudflareTunnel(_queuePrefix, 48080);
            _tunnel.Start();

            Log.LogInfo($"DesktopBuddyRenderer ready, queue prefix: {_queuePrefix}");
        }

        private void Update()
        {
            if (_queuePrefix == null) return;
            _frameCount++;

            try
            {
                UpdateInner();
            }
            catch (Exception ex)
            {
                Log.LogError($"[Update] EXCEPTION in frame {_frameCount}: {ex}");
            }
        }

        private void UpdateInner()
        {            // Tick all active sources every frame (request capture + detect texture readiness)
            foreach (var kv in _activeSources)
            {
                kv.Value.Tick();

                // Feed frames to active encoders
                if (_slotEncoders.TryGetValue(kv.Key, out var encoder) && encoder.IsInitialized)
                {
                    var tex = kv.Value.UnityTexture;
                    if (tex != null)
                    {
                        encoder.QueueFrame(tex.GetNativeTexturePtr(), (uint)tex.width, (uint)tex.height);
                    }
                }
            }

            // Retry pending UWC binds
            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, source) = _pendingBinds[i];
                if (source.TryBind())
                {
                    _pendingBinds.RemoveAt(i);
                    int offset = slot * CaptureSessionProtocol.SessionSize;
                    _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusRunning);
                    _accessor.Write(offset + CaptureSessionProtocol.OffWidth, source.Width);
                    _accessor.Write(offset + CaptureSessionProtocol.OffHeight, source.Height);
                    Log.LogInfo($"[PendingBind] Slot {slot} now bound: {source.Width}x{source.Height}, " +
                        $"texture={(source.UnityTexture != null ? "ready" : "null")}, " +
                        $"magicIdx={CaptureSessionProtocol.MagicIndexBase + slot}, " +
                        $"inIndexMap={_indexToSource.ContainsKey(CaptureSessionProtocol.MagicIndexBase + slot)}");
                }
            }

            // Periodic diagnostics
            _diagTimer += Time.unscaledDeltaTime;
            if (_diagTimer >= DiagInterval)
            {
                _diagTimer = 0f;
                Log.LogInfo($"[Diag] frame={_frameCount} polls={_pollCount} " +
                    $"activeSources={_activeSources.Count} indexMap={_indexToSource.Count} " +
                    $"pendingBinds={_pendingBinds.Count} encoders={_slotEncoders.Count} " +
                    $"mmf={((_mmf != null) ? "open" : "null")} " +
                    $"server={(_streamServer != null ? "running" : "null")} " +
                    $"tunnel={(_tunnel != null ? "running" : "null")}");

                // Dump active source details
                foreach (var kv in _activeSources)
                {
                    var src = kv.Value;
                    Log.LogInfo($"[Diag]   slot={kv.Key}: valid={src.IsValid} " +
                        $"tex={(src.UnityTexture != null ? $"{src.Width}x{src.Height}" : "null")} " +
                        $"magicIdx={CaptureSessionProtocol.MagicIndexBase + kv.Key} " +
                        $"inIndexMap={_indexToSource.ContainsKey(CaptureSessionProtocol.MagicIndexBase + kv.Key)} " +
                        $"hasEncoder={_slotEncoders.ContainsKey(kv.Key)}");
                }

                // Dump pending binds
                foreach (var (slot, src) in _pendingBinds)
                {
                    Log.LogInfo($"[Diag]   pendingBind slot={slot}: valid={src.IsValid}");
                }

                // Dump indexToSource map
                foreach (var kv in _indexToSource)
                {
                    Log.LogInfo($"[Diag]   indexMap[{kv.Key}]: valid={kv.Value.IsValid} " +
                        $"tex={(kv.Value.UnityTexture != null ? "ready" : "null")}");
                }

                // Scan first 64 MMF slots for any non-idle status
                if (_accessor != null)
                {
                    for (int d = 0; d < 64; d++)
                    {
                        int doff = d * CaptureSessionProtocol.SessionSize;
                        int dstatus = _accessor.ReadInt32(doff + CaptureSessionProtocol.OffStatus);
                        int dstream = _accessor.ReadInt32(doff + CaptureSessionProtocol.OffStreamStatus);
                        if (dstatus != CaptureSessionProtocol.StatusIdle || dstream != CaptureSessionProtocol.StreamNone)
                        {
                            long dhwnd = _accessor.ReadInt64(doff + CaptureSessionProtocol.OffHwnd);
                            Log.LogInfo($"[Diag]   mmf[{d}]: status={dstatus} stream={dstream} hwnd=0x{dhwnd:X} " +
                                $"tracked={_activeSources.ContainsKey(d)}");
                        }
                    }
                }
            }

            _pollTimer += Time.unscaledDeltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;
            _pollCount++;

            // Try to open the MMF if not yet open
            if (_mmf == null)
            {
                try
                {
                    var mmfName = CaptureSessionProtocol.GetMmfName(_queuePrefix + "Primary");
                    _mmf = MemoryMappedFile.OpenExisting(mmfName);
                    _accessor = _mmf.CreateViewAccessor(0, CaptureSessionProtocol.TotalSize,
                        MemoryMappedFileAccess.ReadWrite);
                    Log.LogInfo($"[MMF] Opened: {mmfName} (size={CaptureSessionProtocol.TotalSize})");
                }
                catch (System.IO.FileNotFoundException)
                {
                    if (_pollCount % 50 == 1)
                        Log.LogInfo("[MMF] Waiting for game to create MMF...");
                    return;
                }
                catch (Exception ex)
                {
                    Log.LogError($"[MMF] Open failed: {ex}");
                    return;
                }
            }

            // Poll each session slot
            for (int i = 0; i < CaptureSessionProtocol.MaxSessions; i++)
            {
                int offset = i * CaptureSessionProtocol.SessionSize;
                int status = _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStatus);

                if (status == CaptureSessionProtocol.StatusStart && !_activeSources.ContainsKey(i))
                {
                    long hwndRaw = _accessor.ReadInt64(offset + CaptureSessionProtocol.OffHwnd);
                    long monitorRaw = _accessor.ReadInt64(offset + CaptureSessionProtocol.OffMonitor);
                    var hwnd = new IntPtr(hwndRaw);

                    Log.LogInfo($"[Poll] StatusStart slot={i} hwnd=0x{hwndRaw:X} monitor=0x{monitorRaw:X}");

                    var source = new UwcDisplaySource(hwnd);
                    _activeSources[i] = source;

                    int magicIndex = CaptureSessionProtocol.MagicIndexBase + i;
                    _indexToSource[magicIndex] = source;
                    Log.LogInfo($"[Poll] Registered: activeSources[{i}], indexToSource[{magicIndex}]");

                    if (source.TryBind())
                    {
                        _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusRunning);
                        _accessor.Write(offset + CaptureSessionProtocol.OffWidth, source.Width);
                        _accessor.Write(offset + CaptureSessionProtocol.OffHeight, source.Height);
                        Log.LogInfo($"[Poll] Slot {i} immediately bound: {source.Width}x{source.Height} " +
                            $"texture={(source.UnityTexture != null ? "ready" : "null")}");
                    }
                    else
                    {
                        _pendingBinds.Add((i, source));
                        Log.LogInfo($"[Poll] Slot {i} queued for UWC bind (hwnd=0x{hwndRaw:X} not yet in UWC)");
                    }
                }
                else if (status == CaptureSessionProtocol.StatusStop)
                {
                    bool wasTracked = _activeSources.ContainsKey(i);
                    if (wasTracked)
                    {
                        Log.LogInfo($"[Poll] StatusStop slot={i} (was tracked, cleaning up)");
                        int magicIndex = CaptureSessionProtocol.MagicIndexBase + i;
                        _indexToSource.Remove(magicIndex);
                        StopSlotEncoder(i);
                        _activeSources[i].Dispose();
                        _activeSources.Remove(i);
                    }
                    else
                    {
                        Log.LogInfo($"[Poll] StatusStop slot={i} (was NOT tracked — missed transition, recycling)");
                    }

                    _pendingBinds.RemoveAll(p => p.slot == i);
                    _accessor.Write(offset + CaptureSessionProtocol.OffStatus, CaptureSessionProtocol.StatusIdle);
                }

                // --- Streaming status poll ---
                int streamStatus = _accessor.ReadInt32(offset + CaptureSessionProtocol.OffStreamStatus);
                if (streamStatus == CaptureSessionProtocol.StreamRequested && !_slotEncoders.ContainsKey(i))
                {
                    Log.LogInfo($"[Poll] StreamRequested slot={i} (hasSource={_activeSources.ContainsKey(i)})");
                    StartSlotEncoder(i, offset);
                }
                else if (streamStatus == CaptureSessionProtocol.StreamStopping)
                {
                    bool hadEncoder = _slotEncoders.ContainsKey(i);
                    Log.LogInfo($"[Poll] StreamStopping slot={i} (hadEncoder={hadEncoder})");
                    if (hadEncoder)
                        StopSlotEncoder(i);
                    _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamNone);
                    _accessor.Write(offset + CaptureSessionProtocol.OffStreamPort, 0);
                }
            }
        }

        private void StartSlotEncoder(int slot, int offset)
        {
            if (!_activeSources.TryGetValue(slot, out var source))
            {
                Log.LogWarning($"[Streaming] Slot {slot}: no capture source registered at all, aborting");
                _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamNone);
                return;
            }
            if (!source.IsValid || source.Width == 0 || source.Height == 0)
            {
                // Source exists but UWC hasn't delivered size yet — leave StreamRequested, retry next poll
                Log.LogInfo($"[Streaming] Slot {slot}: source not yet valid (valid={source.IsValid} size={source.Width}x{source.Height}), retrying next poll");
                return;
            }

            EnsureD3DDevice();
            if (_d3dDevice == IntPtr.Zero)
            {
                Log.LogError("[Streaming] Cannot start encoder: D3D11 device not available");
                _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamNone);
                return;
            }

            EnsureStreamServer();

            var encoder = new FfmpegEncoder(slot);
            var audioBridge = new AudioBridge(_queuePrefix, slot);
            audioBridge.TryOpen(); // May not be available yet, encoder handles null audio gracefully

            encoder.StartInitializeAsync(_d3dDevice, (uint)source.Width, (uint)source.Height, _d3dContextLock,
                audioBridge.IsOpen ? audioBridge : null);

            _slotEncoders[slot] = encoder;
            _slotAudioBridges[slot] = audioBridge;
            _streamServer.RegisterEncoder(slot, encoder);

            _accessor.Write(offset + CaptureSessionProtocol.OffStreamPort, _streamServer.Port);
            _accessor.Write(offset + CaptureSessionProtocol.OffStreamStatus, CaptureSessionProtocol.StreamActive);

            Log.LogInfo($"[Streaming] Slot {slot}: encoder starting, port={_streamServer.Port}");
        }

        private void StopSlotEncoder(int slot)
        {
            if (_slotEncoders.TryGetValue(slot, out var encoder))
            {
                _streamServer?.UnregisterEncoder(slot);
                encoder.Stop();
                encoder.Dispose();
                _slotEncoders.Remove(slot);
                Log.LogInfo($"[Streaming] Slot {slot}: encoder stopped");
            }
            if (_slotAudioBridges.TryGetValue(slot, out var bridge))
            {
                bridge.Dispose();
                _slotAudioBridges.Remove(slot);
            }
        }

        private void EnsureStreamServer()
        {
            // Server now started in Awake, this is a no-op guard
            if (_streamServer != null) return;
            _streamServer = new MjpegServer(48080);
            _streamServer.Start();
            Log.LogInfo("[Streaming] MjpegServer started on port 48080");
        }

        private unsafe void EnsureD3DDevice()
        {
            if (_d3dDevice != IntPtr.Zero) return;

            // Get Unity's own D3D11 device via COM vtable on a temporary RenderTexture.
            // Unity's texture is ID3D11Texture2D -> ID3D11DeviceChild, vtable slot 3 = GetDevice.
            // We MUST use the same device Unity uses so CopySubresourceRegion works on Unity textures.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            {
                Log.LogError("[Streaming] Graphics device is not D3D11 — cannot encode");
                return;
            }

            var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            var texPtr = rt.GetNativeTexturePtr();
            UnityEngine.Object.Destroy(rt);

            if (texPtr == IntPtr.Zero)
            {
                Log.LogError("[Streaming] GetNativeTexturePtr returned null — cannot get D3D11 device");
                return;
            }

            // vtable[3] = ID3D11DeviceChild::GetDevice(ID3D11Device**)
            void** vtable = *(void***)texPtr.ToPointer();
            var fnPtr = new IntPtr(vtable[3]);
            var getDevice = Marshal.GetDelegateForFunctionPointer<ID3D11DeviceChildGetDevice>(fnPtr);
            getDevice(texPtr, out _d3dDevice);
            // GetDevice() AddRefs the returned device; we hold that reference until OnDestroy

            if (_d3dDevice == IntPtr.Zero)
            {
                Log.LogError("[Streaming] GetDevice returned null");
                return;
            }

            Log.LogInfo($"[Streaming] Got Unity D3D11 device: 0x{_d3dDevice:X}");

            // QueryInterface for ID3D11VideoDevice — NVENC requires the device was created with
            // D3D11_CREATE_DEVICE_VIDEO_SUPPORT (0x800). If QI fails, encoding will crash.
            var iidVideo = IID_ID3D11VideoDevice;
            void** devVtable = *(void***)_d3dDevice.ToPointer();
            var qi = Marshal.GetDelegateForFunctionPointer<IUnknownQueryInterface>(new IntPtr(devVtable[0]));
            int hrQi = qi(_d3dDevice, ref iidVideo, out IntPtr videoDevPtr);
            if (hrQi == 0 && videoDevPtr != IntPtr.Zero)
            {
                Log.LogInfo("[Streaming] Unity D3D device SUPPORTS ID3D11VideoDevice — NVENC should work");
                void** vdVtable = *(void***)videoDevPtr.ToPointer();
                var release = Marshal.GetDelegateForFunctionPointer<IUnknownRelease>(new IntPtr(vdVtable[2]));
                release(videoDevPtr);
            }
            else
            {
                Log.LogWarning($"[Streaming] Unity D3D device does NOT support ID3D11VideoDevice (hr=0x{hrQi:X8}) — NVENC will crash! Need separate device with VIDEO_SUPPORT flag.");
            }
        }

        /// <summary>
        /// Look up the UwcDisplaySource for a magic displayIndex, or null if not ours.
        /// Called from the Harmony patch on DesktopTextureAsset.Handle.
        /// </summary>
        internal static UwcDisplaySource GetSourceForDisplayIndex(int displayIndex)
        {
            _indexToSource.TryGetValue(displayIndex, out var source);
            return source;
        }

        private static string ParseQueuePrefix()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-QueueName", StringComparison.OrdinalIgnoreCase))
                {
                    var queueName = args[i + 1];
                    if (queueName.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
                        return queueName.Substring(0, queueName.Length - 7);
                    return queueName;
                }
            }
            return null;
        }

        private void OnDestroy()
        {
            // Stop tunnel first
            _tunnel?.Dispose();
            _tunnel = null;

            // Stop all encoders
            foreach (var slot in new List<int>(_slotEncoders.Keys))
                StopSlotEncoder(slot);
            _slotEncoders.Clear();
            _slotAudioBridges.Clear();

            _streamServer?.Dispose();
            _streamServer = null;

            if (_d3dDevice != IntPtr.Zero)
            {
                Marshal.Release(_d3dDevice);
                _d3dDevice = IntPtr.Zero;
            }

            foreach (var kv in _activeSources)
                kv.Value.Dispose();
            _activeSources.Clear();
            _indexToSource.Clear();
            _pendingBinds.Clear();

            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }

    /// <summary>
    /// Harmony postfix on DisplayDriver.TryGetDisplayTexture(int index).
    /// For magic indices (>= MagicIndexBase), return our UwcDisplaySource.
    /// The normal DesktopTextureAsset.Update() flow then handles RegisterRequest/TextureUpdated.
    /// </summary>
    [HarmonyPatch(typeof(DisplayDriver), "TryGetDisplayTexture")]
    internal static class DisplayDriverPatch
    {
        static void Postfix(int index, ref IDisplayTextureSource __result)
        {
            if (index < CaptureSessionProtocol.MagicIndexBase)
                return; // Normal display — keep original result

            var source = DesktopBuddyRendererPlugin.GetSourceForDisplayIndex(index);
            if (source != null)
            {
                __result = source;
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayDriverPatch] index={index} → UwcDisplaySource (IsValid={source.IsValid}, " +
                    $"texture={(source.UnityTexture != null ? "ready" : "null")}, {source.Width}x{source.Height})");
            }
            else
            {
                DesktopBuddyRendererPlugin.Log.LogInfo(
                    $"[DisplayDriverPatch] index={index} → no source registered yet");
            }
        }
    }
}

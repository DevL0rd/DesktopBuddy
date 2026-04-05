using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

/// <summary>
/// Controls per-process audio volume via Windows Audio Session API (WASAPI).
/// Uses raw COM vtable calls for reliability in .NET hosted environments.
/// </summary>
internal static class WindowVolume
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7B636-1D60-4DB6-885B-6B97D88FAB25");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr obj);

    /// <summary>Set volume for a specific process (0.0 - 1.0).</summary>
    public static bool SetProcessVolume(uint processId, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, sessionMgr = IntPtr.Zero, sessionEnum = IntPtr.Zero;
        try
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out enumerator);
            if (hr < 0) { ResoniteModLoader.ResoniteMod.Msg($"[WindowVolume] CoCreateInstance failed: 0x{hr:X8}"); return false; }

            // IMMDeviceEnumerator::GetDefaultAudioEndpoint (vtable index 4)
            hr = VTable<GetDefaultAudioEndpointDelegate>(enumerator, 4)(enumerator, 0, 1, out device);
            if (hr < 0) return false;

            // IMMDevice::Activate (vtable index 3)
            var iidSm = IID_IAudioSessionManager2;
            hr = VTable<ActivateDelegate>(device, 3)(device, ref iidSm, 0x17, IntPtr.Zero, out sessionMgr);
            if (hr < 0) return false;

            // IAudioSessionManager2::GetSessionEnumerator (vtable index 5)
            hr = VTable<GetSessionEnumeratorDelegate>(sessionMgr, 5)(sessionMgr, out sessionEnum);
            if (hr < 0) return false;

            // IAudioSessionEnumerator::GetCount (vtable index 3)
            hr = VTable<GetCountDelegate>(sessionEnum, 3)(sessionEnum, out int count);
            if (hr < 0) return false;

            // Get target process name for matching (audio PID may differ from window PID for browsers)
            string targetName = null;
            try { targetName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName.ToLowerInvariant(); } catch { }

            for (int i = 0; i < count; i++)
            {
                IntPtr sessionCtl = IntPtr.Zero, simpleVol = IntPtr.Zero;
                try
                {
                    hr = VTable<GetSessionDelegate>(sessionEnum, 4)(sessionEnum, i, out sessionCtl);
                    if (hr < 0 || sessionCtl == IntPtr.Zero) continue;

                    // Call GetProcessId (vtable slot 14) directly — QI to IAudioSessionControl2
                    // fails on some systems but the vtable method is still present
                    hr = VTable<GetProcessIdDelegate>(sessionCtl, 14)(sessionCtl, out uint pid);
                    if (hr < 0 || pid == 0) continue;

                    // Match by exact PID or by process name (browsers use child processes for audio)
                    bool match = pid == processId;
                    if (!match && targetName != null)
                    {
                        try { match = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant() == targetName; } catch { }
                    }

                    if (match)
                    {
                        var iidVol = IID_ISimpleAudioVolume;
                        hr = Marshal.QueryInterface(sessionCtl, ref iidVol, out simpleVol);
                        if (hr < 0 || simpleVol == IntPtr.Zero) continue;

                        var guid = Guid.Empty;
                        hr = VTable<SetMasterVolumeDelegate>(simpleVol, 3)(simpleVol, volume, ref guid);
                        if (hr >= 0) return true;
                    }
                }
                finally
                {
                    if (simpleVol != IntPtr.Zero) Marshal.Release(simpleVol);
                    if (sessionCtl != IntPtr.Zero) Marshal.Release(sessionCtl);
                }
            }
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WindowVolume] SetProcessVolume failed: {ex.Message}");
        }
        finally
        {
            if (sessionEnum != IntPtr.Zero) Marshal.Release(sessionEnum);
            if (sessionMgr != IntPtr.Zero) Marshal.Release(sessionMgr);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
        }
        return false;
    }

    /// <summary>Set system master volume (0.0 - 1.0).</summary>
    public static bool SetMasterVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, endpointVol = IntPtr.Zero;
        try
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out enumerator);
            if (hr < 0) return false;

            hr = VTable<GetDefaultAudioEndpointDelegate>(enumerator, 4)(enumerator, 0, 1, out device);
            if (hr < 0) return false;

            var iidEpv = IID_IAudioEndpointVolume;
            hr = VTable<ActivateDelegate>(device, 3)(device, ref iidEpv, 0x17, IntPtr.Zero, out endpointVol);
            if (hr < 0) return false;

            // IAudioEndpointVolume::SetMasterVolumeLevelScalar (vtable index 7)
            // IUnknown(0-2), RegisterControlChangeNotify(3), UnregisterControlChangeNotify(4),
            // GetChannelCount(5), SetMasterVolumeLevel(6), SetMasterVolumeLevelScalar(7)
            var guid = Guid.Empty;
            hr = VTable<SetMasterVolumeLevelScalarDelegate>(endpointVol, 7)(endpointVol, volume, ref guid);
            return hr >= 0;
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WindowVolume] SetMasterVolume failed: {ex.Message}");
        }
        finally
        {
            if (endpointVol != IntPtr.Zero) Marshal.Release(endpointVol);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
        }
        return false;
    }

    // --- Raw COM vtable helpers ---

    private static T VTable<T>(IntPtr comObj, int slot) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);

    // IMMDevice::Activate
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr obj);

    // IAudioSessionManager2::GetSessionEnumerator
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionEnumeratorDelegate(IntPtr self, out IntPtr enumerator);

    // IAudioSessionEnumerator::GetCount
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out int count);

    // IAudioSessionEnumerator::GetSession
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionDelegate(IntPtr self, int index, out IntPtr session);

    // IAudioSessionControl2::GetProcessId
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProcessIdDelegate(IntPtr self, out uint pid);

    // ISimpleAudioVolume::SetMasterVolume
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeDelegate(IntPtr self, float level, ref Guid eventContext);

    // IAudioEndpointVolume::SetMasterVolumeLevelScalar
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeLevelScalarDelegate(IntPtr self, float level, ref Guid eventContext);
}

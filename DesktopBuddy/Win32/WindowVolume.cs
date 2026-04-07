using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

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

    public static bool SetProcessVolume(uint processId, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, sessionMgr = IntPtr.Zero, sessionEnum = IntPtr.Zero;
        try
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out enumerator);
            if (hr < 0) { Log.Msg($"[WindowVolume] CoCreateInstance failed: 0x{hr:X8}"); return false; }

            hr = VTable<GetDefaultAudioEndpointDelegate>(enumerator, 4)(enumerator, 0, 1, out device);
            if (hr < 0) return false;

            var iidSm = IID_IAudioSessionManager2;
            hr = VTable<ActivateDelegate>(device, 3)(device, ref iidSm, 0x17, IntPtr.Zero, out sessionMgr);
            if (hr < 0) return false;

            hr = VTable<GetSessionEnumeratorDelegate>(sessionMgr, 5)(sessionMgr, out sessionEnum);
            if (hr < 0) return false;

            hr = VTable<GetCountDelegate>(sessionEnum, 3)(sessionEnum, out int count);
            if (hr < 0) return false;

            string targetName = null;
            try { targetName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName.ToLowerInvariant(); } catch (Exception ex) { Log.Msg($"[WindowVolume] Process name lookup failed for pid={processId}: {ex.Message}"); }

            for (int i = 0; i < count; i++)
            {
                IntPtr sessionCtl = IntPtr.Zero, simpleVol = IntPtr.Zero;
                try
                {
                    hr = VTable<GetSessionDelegate>(sessionEnum, 4)(sessionEnum, i, out sessionCtl);
                    if (hr < 0 || sessionCtl == IntPtr.Zero) continue;

                    hr = VTable<GetProcessIdDelegate>(sessionCtl, 14)(sessionCtl, out uint pid);
                    if (hr < 0 || pid == 0) continue;

                    bool match = pid == processId;
                    if (!match && targetName != null)
                    {
                        try { match = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant() == targetName; } catch (Exception ex) { Log.Msg($"[WindowVolume] Process match check failed for pid={pid}: {ex.Message}"); }
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
            Log.Msg($"[WindowVolume] SetProcessVolume failed: {ex.Message}");
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

            var guid = Guid.Empty;
            hr = VTable<SetMasterVolumeLevelScalarDelegate>(endpointVol, 7)(endpointVol, volume, ref guid);
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Log.Msg($"[WindowVolume] SetMasterVolume failed: {ex.Message}");
        }
        finally
        {
            if (endpointVol != IntPtr.Zero) Marshal.Release(endpointVol);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
        }
        return false;
    }

    private static T VTable<T>(IntPtr comObj, int slot) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionEnumeratorDelegate(IntPtr self, out IntPtr enumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionDelegate(IntPtr self, int index, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProcessIdDelegate(IntPtr self, out uint pid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeDelegate(IntPtr self, float level, ref Guid eventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeLevelScalarDelegate(IntPtr self, float level, ref Guid eventContext);
}

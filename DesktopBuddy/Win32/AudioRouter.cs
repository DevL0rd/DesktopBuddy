using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

/// <summary>
/// Routes a process's audio output to a specific device using the undocumented
/// IAudioPolicyConfig WinRT interface. Used to redirect window audio to VB-Cable
/// (null sink) so the user only hears it spatially in-game.
/// </summary>
internal static class AudioRouter
{
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    // IAudioPolicyConfig (Windows 10 21H2+ / Windows 11)
    // IID: {AB3D4648-E242-459F-B02F-541C70306324}
    // Activated via RoGetActivationFactory("Windows.Media.Internal.AudioPolicyConfig")
    //
    // Vtable (IInspectable-based):
    //   0-2: IUnknown (QI, AddRef, Release)
    //   3-5: IInspectable (GetIids, GetRuntimeClassName, GetTrustLevel)
    //   6-24: Various volume/ringer methods
    //   25: SetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, IntPtr hstringDeviceId)
    //   26: GetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, out IntPtr hstringDeviceId)
    //   27: ClearAllPersistedApplicationDefaultEndpoints()
    private const int VT_SetPersistedDefaultAudioEndpoint = 25;

    private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";

    // Pre-21H2 IID
    private static readonly Guid IID_IAudioPolicyConfig_Pre21H2 = new("2A59116D-6C4F-45E0-A74F-707E3FEF9258");
    // 21H2+ IID
    private static readonly Guid IID_IAudioPolicyConfig_21H2 = new("AB3D4648-E242-459F-B02F-541C70306324");

    private static IntPtr _factory;
    private static bool _initFailed;

    private static unsafe bool EnsureFactory()
    {
        if (_factory != IntPtr.Zero) return true;
        if (_initFailed) return false;

        try
        {
            string className = "Windows.Media.Internal.AudioPolicyConfig";
            WindowsCreateString(className, (uint)className.Length, out IntPtr hClassName);

            // Try 21H2+ first, fall back to pre-21H2
            var iid = IID_IAudioPolicyConfig_21H2;
            int hr = RoGetActivationFactory(hClassName, ref iid, out _factory);
            if (hr < 0 || _factory == IntPtr.Zero)
            {
                iid = IID_IAudioPolicyConfig_Pre21H2;
                hr = RoGetActivationFactory(hClassName, ref iid, out _factory);
            }
            WindowsDeleteString(hClassName);

            if (hr < 0 || _factory == IntPtr.Zero)
            {
                Log.Msg($"[AudioRouter] RoGetActivationFactory failed: 0x{hr:X8}");
                _initFailed = true;
                return false;
            }
            Log.Msg($"[AudioRouter] IAudioPolicyConfig factory acquired");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[AudioRouter] Init failed: {ex.Message}");
            _initFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Redirect a process's audio output to a specific device.
    /// deviceEndpointId is the MMDevice short ID like "{0.0.0.00000000}.{guid}".
    /// </summary>
    internal static unsafe void SetProcessOutputDevice(uint processId, string deviceEndpointId)
    {
        if (!EnsureFactory()) return;

        try
        {
            IntPtr hDeviceId = IntPtr.Zero;
            if (!string.IsNullOrEmpty(deviceEndpointId))
            {
                var fullId = $"{MMDEVAPI_TOKEN}{deviceEndpointId}{DEVINTERFACE_AUDIO_RENDER}";
                Log.Msg($"[AudioRouter] Full device path: {fullId}");
                WindowsCreateString(fullId, (uint)fullId.Length, out hDeviceId);
            }

            var vtable = *(IntPtr**)_factory;
            var setFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)vtable[VT_SetPersistedDefaultAudioEndpoint];

            // Set for both eConsole(0) and eMultimedia(1) roles
            int hr1 = setFn(_factory, processId, 0 /*eRender*/, 0 /*eConsole*/, hDeviceId);
            int hr2 = setFn(_factory, processId, 0 /*eRender*/, 1 /*eMultimedia*/, hDeviceId);

            if (hDeviceId != IntPtr.Zero)
                WindowsDeleteString(hDeviceId);

            if (hr1 < 0 || hr2 < 0)
                Log.Msg($"[AudioRouter] SetPersistedDefaultAudioEndpoint failed: console=0x{hr1:X8} multimedia=0x{hr2:X8}");
            else
                Log.Msg($"[AudioRouter] Redirected PID {processId} to {deviceEndpointId}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[AudioRouter] SetProcessOutputDevice error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset a process's audio output back to the system default.
    /// </summary>
    internal static unsafe void ResetProcessToDefault(uint processId)
    {
        if (!EnsureFactory()) return;

        try
        {
            var vtable = *(IntPtr**)_factory;
            var setFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)vtable[VT_SetPersistedDefaultAudioEndpoint];

            setFn(_factory, processId, 0 /*eRender*/, 0 /*eConsole*/, IntPtr.Zero);
            setFn(_factory, processId, 0 /*eRender*/, 1 /*eMultimedia*/, IntPtr.Zero);

            Log.Msg($"[AudioRouter] Reset PID {processId} to system default");
        }
        catch (Exception ex)
        {
            Log.Msg($"[AudioRouter] ResetProcessToDefault error: {ex.Message}");
        }
    }
}

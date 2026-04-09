using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal static class AudioRouter
{
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private const int VT_SetPersistedDefaultAudioEndpoint = 25;

    private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";

    private static readonly Guid IID_IAudioPolicyConfig_Pre21H2 = new("2A59116D-6C4F-45E0-A74F-707E3FEF9258");
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

            int hr1 = setFn(_factory, processId, 0, 0, hDeviceId);
            int hr2 = setFn(_factory, processId, 0, 1, hDeviceId);

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

    internal static unsafe void ResetProcessToDefault(uint processId)
    {
        if (!EnsureFactory()) return;

        try
        {
            var vtable = *(IntPtr**)_factory;
            var setFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)vtable[VT_SetPersistedDefaultAudioEndpoint];

            setFn(_factory, processId, 0, 0, IntPtr.Zero);
            setFn(_factory, processId, 0, 1, IntPtr.Zero);

            Log.Msg($"[AudioRouter] Reset PID {processId} to system default");
        }
        catch (Exception ex)
        {
            Log.Msg($"[AudioRouter] ResetProcessToDefault error: {ex.Message}");
        }
    }
}

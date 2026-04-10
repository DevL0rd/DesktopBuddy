using System;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal static class VBCableSetup
{
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    internal static bool IsInstalled()
    {
        try
        {
            return FindCableInputDeviceId() != null;
        }
        catch { return false; }
    }

    internal static unsafe string FindCableInputDeviceId()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr enumerator);
        if (hr < 0 || enumerator == IntPtr.Zero) return null;

        try
        {
            var vtable = *(IntPtr**)enumerator;
            var enumFn = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, out IntPtr, int>)vtable[3];
            hr = enumFn(enumerator, 0, 1, out IntPtr collection);
            if (hr < 0 || collection == IntPtr.Zero) return null;

            try
            {
                var colVt = *(IntPtr**)collection;
                var getCountFn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)colVt[3];
                getCountFn(collection, out uint count);

                for (uint i = 0; i < count; i++)
                {
                    var itemFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)colVt[4];
                    itemFn(collection, i, out IntPtr device);
                    if (device == IntPtr.Zero) continue;

                    try
                    {
                        var devVt = *(IntPtr**)device;
                        var getIdFn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)devVt[5];
                        getIdFn(device, out IntPtr idPtr);
                        string deviceId = Marshal.PtrToStringUni(idPtr);
                        Marshal.FreeCoTaskMem(idPtr);

                        var openPropsFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)devVt[4];
                        openPropsFn(device, 0, out IntPtr props);
                        if (props != IntPtr.Zero)
                        {
                            string friendlyName = GetDeviceFriendlyName(props);
                            Marshal.Release(props);

                            if (friendlyName != null && friendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Msg($"[VBCable] Found CABLE Input: {deviceId}");
                                return deviceId;
                            }
                        }
                    }
                    finally { Marshal.Release(device); }
                }
            }
            finally { Marshal.Release(collection); }
        }
        finally { Marshal.Release(enumerator); }

        return null;
    }

    private static unsafe string GetDeviceFriendlyName(IntPtr propertyStore)
    {
        var propKey = stackalloc byte[20];
        var guid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        *(Guid*)propKey = guid;
        *(uint*)(propKey + 16) = 14;

        var psVt = *(IntPtr**)propertyStore;
        var getValueFn = (delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, int>)psVt[5];
        var propVariant = stackalloc byte[24];
        for (int j = 0; j < 24; j++) propVariant[j] = 0;
        int hr = getValueFn(propertyStore, propKey, propVariant);
        if (hr < 0) return null;

        ushort vt = *(ushort*)propVariant;
        if (vt == 31)
        {
            IntPtr strPtr = *(IntPtr*)(propVariant + 8);
            return Marshal.PtrToStringUni(strPtr);
        }
        return null;
    }
}

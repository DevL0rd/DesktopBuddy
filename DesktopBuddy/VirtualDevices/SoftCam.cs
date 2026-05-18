using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopBuddy;

internal static class SoftCam
{
    private const string DllName = "softcam64";
    private const string FilterClsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";

    static SoftCam()
    {
        NativeLibrary.SetDllImportResolver(typeof(SoftCam).Assembly, (name, asm, path) =>
        {
            if (name != DllName) return IntPtr.Zero;
            string dllPath = FindDll();
            if (dllPath != null && NativeLibrary.TryLoad(dllPath, out IntPtr handle))
                return handle;
            return IntPtr.Zero;
        });
    }

    internal static bool IsFilterRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{FilterClsid}");
            return key != null;
        }
        catch { return false; }
    }

    internal static string FindDll()
    {
        string path = DesktopBuddyRuntimePaths.FindFile("softcam64.dll");
        if (File.Exists(path))
            return Path.GetFullPath(path);

        Log.Msg($"[SoftCam] Missing softcam64.dll at {path}");
        return null;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr scCreateCamera(int width, int height, float framerate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr scCreateCameraEx(int width, int height, float framerate, int pixelFormat, int frameFlags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void scDeleteCamera(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void scSendFrame(IntPtr camera, IntPtr imageBits);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void scSendFrameEx(IntPtr camera, IntPtr imageBits, int sourceStride);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool scWaitForConnection(IntPtr camera, float timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool scIsConnected(IntPtr camera);
}

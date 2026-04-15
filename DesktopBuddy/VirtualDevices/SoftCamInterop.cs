using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

internal static class SoftCamInterop
{
    private const string DllName = "softcam64";

    static SoftCamInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(SoftCamInterop).Assembly, (name, asm, path) =>
        {
            if (name != DllName) return IntPtr.Zero;
            string dllPath = FindDll();
            if (dllPath != null && NativeLibrary.TryLoad(dllPath, out IntPtr handle))
                return handle;
            return IntPtr.Zero;
        });
    }

    internal static string FindDll()
    {
        var modDir = Path.GetDirectoryName(typeof(SoftCamInterop).Assembly.Location) ?? "";
        string[] candidates =
        {
            Path.Combine(modDir, "..", "rml_libs", "softcam64.dll"),
            Path.Combine(modDir, "rml_libs", "softcam64.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rml_libs", "softcam64.dll"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return Path.GetFullPath(c);
        }
        return null;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr scCreateCamera(int width, int height, float framerate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void scDeleteCamera(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void scSendFrame(IntPtr camera, IntPtr imageBits);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool scWaitForConnection(IntPtr camera, float timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool scIsConnected(IntPtr camera);
}

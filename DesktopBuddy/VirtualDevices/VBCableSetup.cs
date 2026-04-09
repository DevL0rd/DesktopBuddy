using System;
using System.Diagnostics;
using System.IO;
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

    internal static void DisableCableLoopback()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable");
            if (key == null) return;

            var val = key.GetValue("VBAudioCableWDM_LoopBack");
            if (val is int loopback && loopback == 0)
            {
                Log.Msg("[VBCable] LoopBack already disabled");
                return;
            }

            Log.Msg("[VBCable] LoopBack is enabled, disabling...");
            var scriptPath = Path.Combine(Path.GetTempPath(), "desktopbuddy_vbcable.ps1");
            File.WriteAllText(scriptPath, string.Join("\n",
                "Set-ItemProperty -Path 'HKLM:\\Software\\VB-Audio\\Cable' -Name 'VBAudioCableWDM_LoopBack' -Value 0 -Force",
                "Restart-Service AudioEndpointBuilder -Force",
                "Restart-Service AudioSrv -Force"));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            Log.Msg($"[VBCable] LoopBack disable + audio restart exit: {proc?.ExitCode}");

            try { File.Delete(scriptPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Msg($"[VBCable] DisableCableLoopback error: {ex.Message}");
        }
    }

    internal static bool Install()
    {
        string installer = FindInstaller();
        if (installer == null)
        {
            Log.Msg("[VBCable] Installer not found");
            return false;
        }

        try
        {
            Log.Msg($"[VBCable] Running installer: {installer}");
            var psi = new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-i -h",
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = true,
                Verb = "runas"
            };
            var proc = Process.Start(psi);
            if (proc == null) { Log.Msg("[VBCable] Failed to start installer process"); return false; }
            proc.WaitForExit(60000);
            Log.Msg($"[VBCable] Installer exited with code {proc.ExitCode}");
            bool installed = IsInstalled();
            Log.Msg($"[VBCable] Post-install check: {(installed ? "driver found" : "driver NOT found — reboot may be required")}");
            return installed || proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Msg($"[VBCable] Install error: {ex.Message}");
            return false;
        }
    }

    internal static string FindInstaller()
    {
        var modDir = Path.GetDirectoryName(typeof(VBCableSetup).Assembly.Location) ?? "";
        string[] candidates =
        {
            Path.Combine(modDir, "..", "vbcable", "VBCABLE_Setup_x64.exe"),
            Path.Combine(modDir, "vbcable", "VBCABLE_Setup_x64.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vbcable", "VBCABLE_Setup_x64.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return Path.GetFullPath(c);
        }
        return null;
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

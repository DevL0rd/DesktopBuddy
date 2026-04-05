using System;
using System.Runtime.InteropServices;
using System.Text;

// Test: call IAudioSessionControl2::GetProcessId via raw vtable on the
// IAudioSessionControl pointer returned by GetSession, without QI.
// IAudioSessionControl has 9 own methods (slots 3-11).
// IAudioSessionControl2 adds: GetSessionIdentifier(12), GetSessionInstanceIdentifier(13), GetProcessId(14).
// If the object actually implements both, calling slot 14 should work even without QI.

class Program
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr obj);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDefaultAudioEndpointDel(IntPtr self, int dataFlow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ActivateDel(IntPtr self, ref Guid iid, uint clsCtx, IntPtr ap, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSessionEnumDel(IntPtr self, out IntPtr e);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetCountDel(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSessionDel(IntPtr self, int index, out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetProcessIdDel(IntPtr self, out uint pid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetMasterVolDel(IntPtr self, float level, ref Guid ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMasterVolDel(IntPtr self, out float level);

    static T VT<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    static void Main()
    {
        uint chromePid = 0;
        var sb = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowText(hwnd, sb, 256);
            string t = sb.ToString();
            if (t.Contains("Chrome") || t.Contains("Google Chrome"))
            {
                GetWindowThreadProcessId(hwnd, out uint p);
                chromePid = p;
                Console.WriteLine($"Found Chrome: pid={p} title='{t}'");
                return false;
            }
            return true;
        }, IntPtr.Zero);

        var CLSID = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var IID_Enum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        var IID_SM2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        var IID_Vol = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        var clsid = CLSID; var iid = IID_Enum;
        CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out IntPtr enumerator);
        VT<GetDefaultAudioEndpointDel>(enumerator, 4)(enumerator, 0, 1, out IntPtr device);
        var iidSm = IID_SM2;
        VT<ActivateDel>(device, 3)(device, ref iidSm, 0x17, IntPtr.Zero, out IntPtr mgr);
        VT<GetSessionEnumDel>(mgr, 5)(mgr, out IntPtr sEnum);
        VT<GetCountDel>(sEnum, 3)(sEnum, out int count);
        Console.WriteLine($"Sessions: {count}\n");

        for (int i = 0; i < count; i++)
        {
            int hr = VT<GetSessionDel>(sEnum, 4)(sEnum, i, out IntPtr ctl);
            if (hr < 0 || ctl == IntPtr.Zero) continue;

            // Call GetProcessId (slot 14) directly on the session control pointer
            // without QI to IAudioSessionControl2
            uint pid = 0;
            try
            {
                hr = VT<GetProcessIdDel>(ctl, 14)(ctl, out pid);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{i}] GetProcessId crashed: {ex.Message}");
                Marshal.Release(ctl);
                continue;
            }

            string name = "???";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

            var iidV = IID_Vol;
            Marshal.QueryInterface(ctl, ref iidV, out IntPtr vol);
            float cur = -1;
            if (vol != IntPtr.Zero) { VT<GetMasterVolDel>(vol, 4)(vol, out cur); }

            string match = pid == chromePid ? " <-- CHROME" : "";
            Console.WriteLine($"  [{i}] PID={pid} ({name}) vol={cur:F2} hr=0x{hr:X8}{match}");

            if (pid == chromePid && chromePid != 0 && vol != IntPtr.Zero)
            {
                var g = Guid.Empty;
                hr = VT<SetMasterVolDel>(vol, 3)(vol, 0.7f, ref g);
                Console.WriteLine($"      -> SetVolume(0.7): 0x{hr:X8}");
            }

            if (vol != IntPtr.Zero) Marshal.Release(vol);
            Marshal.Release(ctl);
        }

        Marshal.Release(sEnum); Marshal.Release(mgr); Marshal.Release(device); Marshal.Release(enumerator);
    }
}

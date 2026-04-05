// Run with: dotnet script test_volume.csx
// Or: dotnet run in a console project
// This is a standalone test for WindowVolume COM calls

using System;
using System.Runtime.InteropServices;

// --- Find Chrome's PID from its window ---
[DllImport("user32.dll")]
static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
[DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
[DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hWnd);

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

// Find a Chrome window
IntPtr chromeHwnd = IntPtr.Zero;
uint chromePid = 0;
var sb = new System.Text.StringBuilder(256);

EnumWindows((hwnd, _) =>
{
    if (!IsWindowVisible(hwnd)) return true;
    GetWindowText(hwnd, sb, 256);
    string title = sb.ToString();
    if (title.Contains("Chrome") || title.Contains("Google Chrome"))
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        chromeHwnd = hwnd;
        chromePid = pid;
        Console.WriteLine($"Found Chrome: hwnd=0x{hwnd:X} pid={pid} title='{title}'");
        return false;
    }
    return true;
}, IntPtr.Zero);

if (chromePid == 0)
{
    Console.WriteLine("Chrome not found. Listing all audio sessions instead...");
}

// --- COM setup ---
var CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
var IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
var IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
var IID_IAudioSessionControl2 = new Guid("BFB7B636-1D60-4DB6-885B-6B97D88FAB25");
var IID_ISimpleAudioVolume = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

[DllImport("ole32.dll")]
static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr obj);

T VTable<T>(IntPtr comObj, int slot) where T : Delegate
{
    IntPtr vtbl = Marshal.ReadIntPtr(comObj);
    IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
    return Marshal.GetDelegateForFunctionPointer<T>(fn);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr obj);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetSessionEnumeratorDelegate(IntPtr self, out IntPtr enumerator);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetCountDelegate(IntPtr self, out int count);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetSessionDelegate(IntPtr self, int index, out IntPtr session);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetProcessIdDelegate(IntPtr self, out uint pid);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int SetMasterVolumeDelegate(IntPtr self, float level, ref Guid eventContext);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetMasterVolumeDelegate(IntPtr self, out float level);

// Create enumerator
var clsid = CLSID_MMDeviceEnumerator;
var iid = IID_IMMDeviceEnumerator;
int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out IntPtr enumerator);
Console.WriteLine($"CoCreateInstance: hr=0x{hr:X8}");

hr = VTable<GetDefaultAudioEndpointDelegate>(enumerator, 4)(enumerator, 0, 1, out IntPtr device);
Console.WriteLine($"GetDefaultAudioEndpoint: hr=0x{hr:X8}");

var iidSm = IID_IAudioSessionManager2;
hr = VTable<ActivateDelegate>(device, 3)(device, ref iidSm, 0x17, IntPtr.Zero, out IntPtr sessionMgr);
Console.WriteLine($"Activate SessionManager2: hr=0x{hr:X8}");

hr = VTable<GetSessionEnumeratorDelegate>(sessionMgr, 5)(sessionMgr, out IntPtr sessionEnum);
Console.WriteLine($"GetSessionEnumerator: hr=0x{hr:X8}");

hr = VTable<GetCountDelegate>(sessionEnum, 3)(sessionEnum, out int count);
Console.WriteLine($"Session count: {count}");
Console.WriteLine();

bool found = false;
for (int i = 0; i < count; i++)
{
    hr = VTable<GetSessionDelegate>(sessionEnum, 4)(sessionEnum, i, out IntPtr sessionCtl);
    if (hr < 0 || sessionCtl == IntPtr.Zero) continue;

    var iid2 = IID_IAudioSessionControl2;
    hr = Marshal.QueryInterface(sessionCtl, ref iid2, out IntPtr session2);
    if (hr < 0)
    {
        Console.WriteLine($"  Session {i}: QI for IAudioSessionControl2 failed: 0x{hr:X8}");
        Marshal.Release(sessionCtl);
        continue;
    }

    hr = VTable<GetProcessIdDelegate>(session2, 14)(session2, out uint pid);

    string procName = "???";
    try { procName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

    // Get current volume
    var iidVol = IID_ISimpleAudioVolume;
    hr = Marshal.QueryInterface(sessionCtl, ref iidVol, out IntPtr simpleVol);
    float curVol = -1;
    if (hr >= 0 && simpleVol != IntPtr.Zero)
    {
        VTable<GetMasterVolumeDelegate>(simpleVol, 4)(simpleVol, out curVol);
        Marshal.Release(simpleVol);
    }

    Console.WriteLine($"  Session {i}: PID={pid} ({procName}) volume={curVol:F2} {(pid == chromePid ? " <-- CHROME MATCH" : "")}");

    if (pid == chromePid && chromePid != 0)
    {
        found = true;
        // Try setting volume to 0.5
        hr = Marshal.QueryInterface(sessionCtl, ref iidVol, out IntPtr vol2);
        if (hr >= 0)
        {
            var guid = Guid.Empty;
            hr = VTable<SetMasterVolumeDelegate>(vol2, 3)(vol2, 0.5f, ref guid);
            Console.WriteLine($"  -> SetMasterVolume(0.5): hr=0x{hr:X8}");
            Marshal.Release(vol2);
        }
    }

    Marshal.Release(session2);
    Marshal.Release(sessionCtl);
}

if (chromePid != 0 && !found)
    Console.WriteLine($"\nChrome PID {chromePid} not found in any audio session!");

// Cleanup
Marshal.Release(sessionEnum);
Marshal.Release(sessionMgr);
Marshal.Release(device);
Marshal.Release(enumerator);

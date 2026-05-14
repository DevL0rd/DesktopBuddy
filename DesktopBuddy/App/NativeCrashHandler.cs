using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{


    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnhandledExceptionFilterDelegate(IntPtr exceptionPointers);

    private static UnhandledExceptionFilterDelegate _nativeCrashDelegate;
    private static IntPtr _previousFilter;

    private static void InstallNativeCrashHandler()
    {
        try
        {
            _nativeCrashDelegate = NativeCrashFilter;
            IntPtr fp = Marshal.GetFunctionPointerForDelegate(_nativeCrashDelegate);
            _previousFilter = SetUnhandledExceptionFilter(fp);
            Log.Msg("[NativeCrash] Handler installed");
        }
        catch (Exception ex)
        {
            Log.Msg($"[NativeCrash] Failed to install handler: {ex.Message}");
        }
    }

    private static int NativeCrashFilter(IntPtr exceptionPointersPtr)
    {
        try
        {
            IntPtr recordPtr = Marshal.ReadIntPtr(exceptionPointersPtr, 0);
            uint code = (uint)Marshal.ReadInt32(recordPtr, 0);
            IntPtr address = Marshal.ReadIntPtr(recordPtr, IntPtr.Size == 8 ? 24 : 12);

            string msg = $"[NativeCrash] FATAL: code=0x{code:X8} addr=0x{address:X}\n";

            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule mod in proc.Modules)
                {
                    long modBase = mod.BaseAddress.ToInt64();
                    long modEnd = modBase + mod.ModuleMemorySize;
                    if (address.ToInt64() >= modBase && address.ToInt64() < modEnd)
                    {
                        long offset = address.ToInt64() - modBase;
                        msg += $"[NativeCrash] Faulting module: {mod.ModuleName}+0x{offset:X} ({mod.FileName})\n";
                        break;
                    }
                }
            }
            catch { }

            try
            {
                msg += $"[NativeCrash] Managed stack:\n{Environment.StackTrace}\n";
            }
            catch { }

            Log.Msg(msg);
        }
        catch
        {
            try { Log.Msg("[NativeCrash] FATAL: crash handler failed to log details"); } catch { }
        }

        return 0;
    }
}

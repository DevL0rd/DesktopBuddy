using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static readonly HashSet<string> DesktopBuddyManagedDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "FFmpeg.AutoGen",
        "Microsoft.Windows.SDK.NET",
        "WinRT.Runtime",
    };

    private static bool _managedDependencyResolverInstalled;

    private static void InstallManagedDependencyResolver()
    {
        if (_managedDependencyResolverInstalled)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += ResolveDesktopBuddyManagedDependency;
        _managedDependencyResolverInstalled = true;
    }

    private static Assembly ResolveDesktopBuddyManagedDependency(object sender, ResolveEventArgs args)
    {
        string assemblyName = new AssemblyName(args.Name).Name;
        if (!DesktopBuddyManagedDependencies.Contains(assemblyName))
            return null;

        foreach (string path in GetDesktopBuddyManagedDependencyPaths(assemblyName))
        {
            try
            {
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                Log.Msg($"[Dependencies] Failed to load {assemblyName} from {path}: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDesktopBuddyManagedDependencyPaths(string assemblyName)
    {
        string fileName = assemblyName + ".dll";
        string modDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        yield return Path.Combine(modDir, "..", "DesktopBuddyNative", fileName);
        yield return Path.Combine(modDir, "DesktopBuddyNative", fileName);
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "DesktopBuddyNative", fileName);
    }
}

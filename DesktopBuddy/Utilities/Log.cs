using System;
using System.IO;

namespace DesktopBuddy;

internal static class Log
{
    internal static readonly string FilePath;

    static Log()
    {
        var resoniteDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".") ?? ".";
        var logsDir = Path.Combine(resoniteDir, "Logs");
        if (!Directory.Exists(logsDir))
            logsDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        FilePath = Path.Combine(logsDir, "DesktopBuddy.log");
    }

    internal static void Msg(string msg)
    {
        ResoniteModLoader.ResoniteMod.Msg(msg);
        try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    internal static void Error(string msg)
    {
        ResoniteModLoader.ResoniteMod.Error(msg);
        try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] ERROR: {msg}\n"); } catch { }
    }

    internal static void StartSession()
    {
        try { File.WriteAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DesktopBuddy session started\n"); } catch { }
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace DesktopBuddy;

internal static class Log
{
    internal static readonly string FilePath;

    private static readonly BlockingCollection<LogEntry> _queue = new(4096);
    private static readonly Thread _writerThread;

    private struct LogEntry
    {
        public string Timestamp;
        public string Message;
        public bool IsError;
    }

    static Log()
    {
        var resoniteDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".") ?? ".";
        var logsDir = Path.Combine(resoniteDir, "Logs");
        if (!Directory.Exists(logsDir))
            logsDir = Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? ".";
        var machineName = Environment.MachineName;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        FilePath = Path.Combine(logsDir, $"DesktopBuddy_{machineName}_{timestamp}.log");

        _writerThread = new Thread(WriterLoop) { Name = "DesktopBuddy:Log", IsBackground = true };
        _writerThread.Start();
    }

    internal static void Msg(string msg)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _queue.TryAdd(new LogEntry { Timestamp = ts, Message = msg, IsError = false });
    }

    internal static void Error(string msg)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        _queue.TryAdd(new LogEntry { Timestamp = ts, Message = msg, IsError = true });
    }

    internal static void StartSession()
    {
        try { File.WriteAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DesktopBuddy session started\n"); } catch { }
    }

    private static void WriterLoop()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (entry.IsError)
                    ResoniteModLoader.ResoniteMod.Error(entry.Message);
                else
                    ResoniteModLoader.ResoniteMod.Msg(entry.Message);
            }
            catch { }

            var prefix = entry.IsError ? "ERROR: " : "";
            try { File.AppendAllText(FilePath, $"[{entry.Timestamp}] {prefix}{entry.Message}\n"); } catch { }
        }
    }
}

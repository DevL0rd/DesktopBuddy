using System;
using System.IO;

// No namespace — must be accessible from top-level statements in Program.cs
// as well as from within the DesktopBuddyManager namespace.

/// <summary>
/// Thread-safe file logger. Call <see cref="Init"/> once with the log file path,
/// then use <see cref="Write"/> from any thread.  All output is also echoed to
/// <see cref="Console"/> for debugging headless relay phases.
/// </summary>
internal static class Logger
{
    private static readonly object _lock = new();
    private static string? _path;

    /// <summary>Open (or create/append) the log file at <paramref name="path"/>.</summary>
    internal static void Init(string path)
    {
        lock (_lock)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Write session header
                File.AppendAllText(path,
                    $"\r\n=== DesktopBuddyManager session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Write a timestamped line to both the log file and Console.</summary>
    internal static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            if (_path == null) return;
            try { File.AppendAllText(_path, line + "\r\n"); }
            catch { /* best-effort */ }
        }
    }
}

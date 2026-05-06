using System;
using System.Diagnostics;
using System.IO;

namespace DesktopBuddyRenderer
{
    internal static class RendererDiagnostics
    {
        private static readonly object Lock = new object();
        private static string _path;

        internal static string Path
        {
            get
            {
                if (_path != null) return _path;

                try
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    var dir = string.IsNullOrEmpty(exe)
                        ? AppDomain.CurrentDomain.BaseDirectory
                        : System.IO.Path.GetDirectoryName(exe);
                    _path = System.IO.Path.Combine(dir ?? AppDomain.CurrentDomain.BaseDirectory, "DesktopBuddyRenderer.diagnostics.log");
                }
                catch
                {
                    _path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopBuddyRenderer.diagnostics.log");
                }

                return _path;
            }
        }

        internal static void Log(string message)
        {
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
            }
        }

        internal static void LogException(string message, Exception ex)
        {
            Log($"{message}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
        }
    }
}

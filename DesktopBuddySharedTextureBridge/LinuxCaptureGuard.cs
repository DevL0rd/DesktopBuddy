using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;

namespace DesktopBuddySharedTextureBridge
{
    /// <summary>
    /// Crash-loop breaker for the Linux native capture start.
    ///
    /// Starting a PipeWire capture runs unmanaged code inside the Wine renderer. If that
    /// call faults, the renderer dies outright and FrooxEngine follows it down with a
    /// FORCE CRASH, so there is no managed exception to catch and no way to recover in
    /// process. Instead we record that a start was in flight, on disk, before making the
    /// call. A start that never completes leaves the marker behind, and after enough
    /// consecutive losses we stop attempting the native path at all: the desktop panel
    /// stays blank, but the session survives.
    /// </summary>
    internal static class LinuxCaptureGuard
    {
        private const int DisableThreshold = 2;
        private const string GuardFileName = "linux-capture.guard";

        private static ManualLogSource _log;
        private static string _path;
        private static int _failures;

        /// <summary>True when repeated crashes have taken the native capture path out of service.</summary>
        internal static bool NativeCaptureDisabled { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;

            try
            {
                string dir = Path.GetDirectoryName(typeof(LinuxCaptureGuard).Assembly.Location);
                if (string.IsNullOrEmpty(dir))
                    return;

                _path = Path.Combine(dir, GuardFileName);
                _failures = ReadFailureCount();

                if (_failures <= 0)
                    return;

                if (_failures >= DisableThreshold)
                {
                    NativeCaptureDisabled = true;
                    _log?.LogError(
                        $"[LinuxCapture] Native capture disabled: the renderer died during capture start {_failures} times in a row. " +
                        $"Desktop panels will stay blank this session. Delete '{_path}' to re-enable.");
                }
                else
                {
                    _log?.LogWarning(
                        $"[LinuxCapture] Previous renderer session died during capture start (strike {_failures} of {DisableThreshold}); retrying.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Guard init failed, continuing unguarded: {ex.Message}");
            }
        }

        /// <summary>Records that a native start is about to run. Returns false if the path is out of service.</summary>
        internal static bool BeginNativeStart(uint nodeId)
        {
            if (NativeCaptureDisabled)
            {
                _log?.LogError($"[LinuxCapture] Refusing native capture start for node={nodeId}: path disabled after repeated crashes.");
                return false;
            }

            WriteFailureCount(_failures + 1);
            return true;
        }

        /// <summary>Clears the in-flight marker once the native call has returned, however it returned.</summary>
        internal static void EndNativeStart()
        {
            // Surviving the call is what matters here, not the status code it produced:
            // a clean failure is reported through normal channels and is not a crash.
            _failures = 0;
            try
            {
                if (_path != null && File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Could not clear guard marker: {ex.Message}");
            }
        }

        private static int ReadFailureCount()
        {
            try
            {
                if (_path == null || !File.Exists(_path))
                    return 0;

                string text = File.ReadAllText(_path).Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0
                    ? count
                    : 1;
            }
            catch
            {
                return 0;
            }
        }

        private static void WriteFailureCount(int count)
        {
            try
            {
                if (_path == null)
                    return;

                File.WriteAllText(_path, count.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Could not write guard marker: {ex.Message}");
            }
        }
    }
}

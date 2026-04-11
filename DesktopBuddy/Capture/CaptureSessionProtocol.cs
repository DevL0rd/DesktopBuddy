using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DesktopBuddy;

/// <summary>
/// Layout of a single capture session in the MMF:
///   [0..3]   sessionId      (int32)
///   [4..11]  hwnd           (int64, IntPtr)
///   [12..19] monitorHandle  (int64, IntPtr)
///   [20..23] status         (int32: 0=idle, 1=start, 2=running, 3=stop)
///   [24..27] width          (int32, set by renderer after capture starts)
///   [28..31] height         (int32, set by renderer after capture starts)
///   [32..35] streamStatus   (int32: 0=none, 1=requested, 2=active, 3=stopping)
///   [36..39] streamPort     (int32, set by renderer when stream is active)
///   [40..43] processId      (uint32, set by game for audio capture)
///   [44..47] reserved
///   Total: 48 bytes per session
/// </summary>
internal static class CaptureSessionProtocol
{
    public const int SessionSize = 48;
    public const int MaxSessions = 4096;
    public const int TotalSize = SessionSize * MaxSessions;

    // Capture status
    public const int StatusIdle = 0;
    public const int StatusStart = 1;
    public const int StatusRunning = 2;
    public const int StatusStop = 3;

    // Stream status
    public const int StreamNone = 0;
    public const int StreamRequested = 1;
    public const int StreamActive = 2;
    public const int StreamStopping = 3;

    // Field offsets within a session slot
    public const int OffSessionId = 0;
    public const int OffHwnd = 4;
    public const int OffMonitor = 12;
    public const int OffStatus = 20;
    public const int OffWidth = 24;
    public const int OffHeight = 28;
    public const int OffStreamStatus = 32;
    public const int OffStreamPort = 36;
    public const int OffProcessId = 40;
    public const int OffMonitorIndex = 44; // -1 for window captures, >= 0 for monitor captures

    public static string GetMmfName(string queueName)
    {
        var prefix = queueName;
        if (prefix.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
            prefix = prefix.Substring(0, prefix.Length - 7);
        return prefix + "DesktopBuddy_Cap";
    }

    public static string GetAudioMmfName(string queueName, int slot)
    {
        var prefix = queueName;
        if (prefix.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
            prefix = prefix.Substring(0, prefix.Length - 7);
        return prefix + $"DesktopBuddy_Audio_{slot}";
    }

    public const int MagicIndexBase = 10000;

    // Audio bridge MMF layout
    public const int AudioRingSamples = 48000 * 2 * 2; // ~2 sec stereo float32
    public const int AudioHeaderSize = 16; // writePos(8) + readPos(8)
    public const int AudioRingBytes = AudioRingSamples * 4; // float32
    public const int AudioMmfSize = AudioHeaderSize + AudioRingBytes;

    // Tunnel URL MMF layout: [0..3] urlLength (int32), [4..4+urlLength-1] UTF-8 URL bytes
    public const int TunnelMmfSize = 512;

    public static string GetTunnelMmfName(string queueName)
    {
        var prefix = queueName;
        if (prefix.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
            prefix = prefix.Substring(0, prefix.Length - 7);
        return prefix + "DesktopBuddy_Tunnel";
    }
}

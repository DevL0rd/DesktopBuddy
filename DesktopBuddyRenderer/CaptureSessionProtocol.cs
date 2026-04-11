using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DesktopBuddy.Shared
{

/// <summary>
/// Layout of a single capture session in the MMF:
///   [0..3]   sessionId    (int32)
///   [4..11]  hwnd         (int64, IntPtr)
///   [12..19] monitorHandle(int64, IntPtr)
///   [20..23] status       (int32: 0=idle, 1=start, 2=running, 3=stop)
///   [24..27] width        (int32, set by renderer after capture starts)
///   [28..31] height       (int32, set by renderer after capture starts)
///   Total: 32 bytes per session
/// </summary>
public static class CaptureSessionProtocol
{
    public const int SessionSize = 32;
    public const int MaxSessions = 4096;
    public const int TotalSize = SessionSize * MaxSessions;

    public const int StatusIdle = 0;
    public const int StatusStart = 1;
    public const int StatusRunning = 2;
    public const int StatusStop = 3;

    public static string GetMmfName(string queueName)
    {
        // queueName is like "{prefix}Primary" — strip "Primary" to get the prefix
        var prefix = queueName;
        if (prefix.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
            prefix = prefix.Substring(0, prefix.Length - 7);
        return prefix + "DesktopBuddy_Cap";
    }

    /// <summary>
    /// Magic DisplayIndex offset. The game-side uses DisplayIndex = MagicIndexBase + sessionSlot
    /// so the renderer plugin can distinguish our captures from normal DesktopTexture usage.
    /// </summary>
    public const int MagicIndexBase = 10000;
}
}

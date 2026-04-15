using System;

namespace DesktopBuddy.Shared
{
    internal static class CaptureSessionProtocol
    {
        public const int SessionSize = 32;
        public const int MaxSessions = 4096;
        public const int TotalSize = SessionSize * MaxSessions;

        public const int OffsetSessionId = 0;
        public const int OffsetHwnd = 4;
        public const int OffsetMonitor = 12;
        public const int OffsetStatus = 20;
        public const int OffsetWidth = 24;
        public const int OffsetHeight = 28;

        public const int StatusIdle = 0;
        public const int StatusStart = 1;
        public const int StatusRunning = 2;
        public const int StatusStop = 3;

        public const int MagicIndexBase = 10000;

        public static string GetMmfName(string queueName)
        {
            var prefix = queueName;
            if (prefix.EndsWith("Primary", StringComparison.OrdinalIgnoreCase))
                prefix = prefix.Substring(0, prefix.Length - 7);
            return prefix + "DesktopBuddy_Cap";
        }
    }
}

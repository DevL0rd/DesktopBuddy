using System;
using System.IO;
using FFmpeg.AutoGen;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private static readonly object _ffmpegInitLock = new();

    public static void SetFfmpegPath()
    {
        lock (_ffmpegInitLock)
        {
            if (_ffmpegPathSet) return;

            string dllDir = FindFfmpegDlls();
            if (dllDir == null)
            {
                Log.Msg("[FFmpeg] FATAL: Could not find FFmpeg shared libraries (avcodec, avformat, avutil)");
                return;
            }

            ffmpeg.RootPath = dllDir;
            DynamicallyLoadedBindings.Initialize();
            Log.Msg($"[FFmpeg] Library path: {dllDir}");

            uint ver = ffmpeg.avcodec_version();
            Log.Msg($"[FFmpeg] avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

            _ffmpegPathSet = true;
        }
    }

    public static string FindFfmpegDlls()
    {
        string dir = Path.GetFullPath(DesktopBuddyRuntimePaths.GetDirectory());
        if (File.Exists(Path.Combine(dir, "avcodec-62.dll")))
            return dir;

        Log.Msg($"[FFmpeg] Missing FFmpeg libraries at {dir}");
        return null;
    }

}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

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
        var modDir = Path.GetDirectoryName(typeof(FfmpegEncoder).Assembly.Location) ?? "";
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(modDir, "..", "DesktopBuddyNative")),
            Path.GetFullPath(Path.Combine(modDir, "DesktopBuddyNative")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopBuddyNative")),
        };
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "avcodec-62.dll")))
                return dir;
        }
        return null;
    }

    public static void PrewarmHardwareEncoder(IntPtr d3dDevice, object d3dContextLock)
    {
        lock (_ffmpegInitLock)
        {
            if (_hardwareEncoderPrewarmed) return;
            if (d3dDevice == IntPtr.Zero)
            {
                Log.Msg("[FFmpeg] Hardware encoder prewarm skipped: no D3D device");
                return;
            }

            using var encoder = new FfmpegEncoder(0);
            if (encoder.Initialize(d3dDevice, 640, 360, d3dContextLock))
            {
                _hardwareEncoderPrewarmed = true;
                Log.Msg("[FFmpeg] Hardware encoder prewarmed");
            }
            else
            {
                Log.Msg("[FFmpeg] Hardware encoder prewarm failed");
            }
        }
    }

}

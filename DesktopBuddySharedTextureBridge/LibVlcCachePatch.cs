using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UMP;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    [HarmonyPatch(typeof(MediaPlayerStandalone), MethodType.Constructor, new[] { typeof(MonoBehaviour), typeof(GameObject[]), typeof(PlayerOptionsStandalone), typeof(int?) })]
    internal static class LibVlcCachePatch
    {
        private static DateTime _lastLoadUtc;
        private static CacheSettings _settings = CacheSettings.Default;
        private static bool _loggedConfigPath;

        private static void Prefix(PlayerOptionsStandalone options)
        {
            try
            {
                if (options == null) return;

                var settings = LoadSettings();
                options.NetworkCaching = settings.NetworkCachingMs;
                options.LiveCaching = settings.LiveCachingMs;
                options.FileCaching = settings.FileCachingMs;

                SharedTextureBridgePlugin.LogInfo(
                    $"[LibVLC] Cache options: network={options.NetworkCaching}ms live={options.LiveCaching}ms file={options.FileCaching}ms");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LibVLC] Prefix failed", ex);
            }
        }

        private static CacheSettings LoadSettings()
        {
            if ((DateTime.UtcNow - _lastLoadUtc).TotalSeconds < 2)
                return _settings;

            _lastLoadUtc = DateTime.UtcNow;
            string path = FindConfigPath();
            if (path == null || !File.Exists(path))
            {
                if (!_loggedConfigPath)
                {
                    _loggedConfigPath = true;
                    SharedTextureBridgePlugin.LogWarning("[LibVLC] DesktopBuddy BepInEx config not found; using low-latency cache defaults");
                }
                _settings = CacheSettings.Default;
                return _settings;
            }

            if (!_loggedConfigPath)
            {
                _loggedConfigPath = true;
                SharedTextureBridgePlugin.LogInfo($"[LibVLC] Reading DesktopBuddy cache config from {path}");
            }

            try
            {
                string configText = File.ReadAllText(path);
                _settings = new CacheSettings
                {
                    NetworkCachingMs = ReadCacheMs(configText, "libVlcNetworkCachingMs", CacheSettings.Default.NetworkCachingMs),
                    LiveCachingMs = ReadCacheMs(configText, "libVlcLiveCachingMs", CacheSettings.Default.LiveCachingMs),
                    FileCachingMs = ReadCacheMs(configText, "libVlcFileCachingMs", CacheSettings.Default.FileCachingMs),
                };
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LibVLC] Failed to read DesktopBuddy cache config", ex);
                _settings = CacheSettings.Default;
            }

            return _settings;
        }

        private static string FindConfigPath()
        {
            string gameRoot = Paths.GameRootPath;
            string rendererParent = Directory.GetParent(gameRoot)?.FullName;
            string current = Directory.GetCurrentDirectory();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string assemblyDir = Path.GetDirectoryName(typeof(SharedTextureBridgePlugin).Assembly.Location);
            const string configFile = "com.devl0rd.DesktopBuddy.cfg";

            string[] candidates =
            {
                Path.Combine(assemblyDir ?? "", "..", "..", "..", "..", "BepInEx", "config", configFile),
                Path.Combine(gameRoot ?? "", "BepInEx", "config", configFile),
                Path.Combine(rendererParent ?? "", "BepInEx", "config", configFile),
                Path.Combine(current ?? "", "BepInEx", "config", configFile),
                Path.Combine(baseDir ?? "", "..", "..", "..", "BepInEx", "config", configFile),
                Path.Combine(baseDir ?? "", "..", "..", "..", "..", "BepInEx", "config", configFile),
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }

            return null;
        }

        private static int ReadInt(string configText, string key, int fallback)
        {
            string escapedKey = Regex.Escape(key);
            var match = Regex.Match(configText, "\"" + escapedKey + "\"\\s*:\\s*(-?\\d+)");
            if (!match.Success)
                match = Regex.Match(configText, "(?m)^\\s*" + escapedKey + "\\s*=\\s*(-?\\d+)\\s*$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value))
                return fallback;

            if (value < 0) return 0;
            if (value > 5000) return 5000;
            return value;
        }

        private static int ReadCacheMs(string json, string key, int fallback)
        {
            return ReadInt(json, key, fallback);
        }

        private struct CacheSettings
        {
            public int NetworkCachingMs;
            public int LiveCachingMs;
            public int FileCachingMs;

            public static CacheSettings Default => new CacheSettings
            {
                NetworkCachingMs = 300,
                LiveCachingMs = 300,
                FileCachingMs = 300,
            };
        }
    }
}

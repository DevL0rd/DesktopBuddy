using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DesktopBuddyRenderer
{
    /// <summary>
    /// Patches UMPVideoTextureBehaviour.InitializePlayer to use low-latency VLC options:
    ///   UseTCP = true, all caching = 0
    /// </summary>
    [HarmonyPatch]
    static class VlcLowLatencyPatch
    {
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("UMPVideoTextureBehaviour");
            if (type == null)
            {
                Debug.LogWarning("[DesktopBuddy] UMPVideoTextureBehaviour not found, VLC patch skipped");
                return null;
            }
            var method = AccessTools.Method(type, "InitializePlayer", new[] { typeof(int) });
            if (method == null)
                Debug.LogWarning("[DesktopBuddy] InitializePlayer method not found, VLC patch skipped");
            return method;
        }

        static bool Prefix(MonoBehaviour __instance, int outputSampleRate)
        {
            try
            {
                PatchedInitializePlayer(__instance, outputSampleRate);
                return false; // skip original
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DesktopBuddy] VLC low-latency patch failed, falling back to original: {ex}");
                return true; // run original
            }
        }

        static void PatchedInitializePlayer(MonoBehaviour instance, int outputSampleRate)
        {
            var instanceType = instance.GetType();

            // --- Create PlayerOptionsStandalone with low-latency settings ---
            var optStandaloneType = AccessTools.TypeByName("UMP.PlayerOptionsStandalone");
            if (optStandaloneType == null) throw new Exception("PlayerOptionsStandalone type not found");

            var options = Activator.CreateInstance(optStandaloneType, new object[] { null });

            SetProp(options, "FixedVideoSize", Vector2.zero);
            SetProp(options, "FlipVertically", true);
            SetProp(options, "UseTCP", true);
            SetProp(options, "FileCaching", 0);
            SetProp(options, "LiveCaching", 0);
            SetProp(options, "DiskCaching", 0);
            SetProp(options, "NetworkCaching", 0);

            // HardwareDecoding = PlayerOptions.States.Disable
            var statesType = AccessTools.TypeByName("UMP.PlayerOptions+States");
            if (statesType == null) statesType = AccessTools.TypeByName("UMP.PlayerOptions/States");
            if (statesType != null)
                SetProp(options, "HardwareDecoding", Enum.Parse(statesType, "Disable"));

            // LogDetail = LogLevels.Disable
            var logType = AccessTools.TypeByName("UMP.LogLevels");
            if (logType != null)
                SetProp(options, "LogDetail", Enum.Parse(logType, "Disable"));

            Debug.Log("[DesktopBuddy] VLC options: UseTCP=true, all caching=0 (low-latency mode)");

            // --- Create MediaPlayer ---
            var mediaPlayerType = AccessTools.TypeByName("UMP.MediaPlayer");
            if (mediaPlayerType == null) throw new Exception("MediaPlayer type not found");

            // MediaPlayer(MonoBehaviour, GameObject[], PlayerOptions, int)
            var mediaPlayer = Activator.CreateInstance(mediaPlayerType, new object[] { instance, null, options, outputSampleRate });

            // --- Set fields on instance ---
            var mpField = instanceType.GetField("mediaPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
            mpField.SetValue(instance, mediaPlayer);

            var playerProp = mediaPlayerType.GetProperty("Player");
            var standalonePlayer = playerProp.GetValue(mediaPlayer);

            var spField = instanceType.GetField("standalonePlayer", BindingFlags.NonPublic | BindingFlags.Instance);
            spField.SetValue(instance, standalonePlayer);

            // --- Subscribe events (matching original) ---
            var spType = standalonePlayer.GetType();

            // standalonePlayer.OnPlaySamples += StandalonePlayer_OnPlaySamples
            HookEvent(spType, standalonePlayer, "OnPlaySamples",
                instanceType, instance, "StandalonePlayer_OnPlaySamples");

            // standalonePlayer.OnFlushSamples += StandalonePlayer_OnFlushSamples
            HookEvent(spType, standalonePlayer, "OnFlushSamples",
                instanceType, instance, "StandalonePlayer_OnFlushSamples");

            // standalonePlayer.OnPause += StandalonePlayer_OnPause
            HookEvent(spType, standalonePlayer, "OnPause",
                instanceType, instance, "StandalonePlayer_OnPause");

            // standalonePlayer.OnResume += StandalonePlayer_OnResume
            HookEvent(spType, standalonePlayer, "OnResume",
                instanceType, instance, "StandalonePlayer_OnResume");

            // mediaPlayer.EventManager.PlayerPositionChangedListener += PositionChanged
            var eventMgr = mediaPlayerType.GetProperty("EventManager").GetValue(mediaPlayer);
            var emType = eventMgr.GetType();

            HookEvent(emType, eventMgr, "PlayerPositionChangedListener",
                instanceType, instance, "PositionChanged");

            // mediaPlayer.EventManager.PlayerImageReadyListener += OnTextureCreated
            HookEvent(emType, eventMgr, "PlayerImageReadyListener",
                instanceType, instance, "OnTextureCreated");

            // mediaPlayer.EventManager.PlayerEndReachedListener += EndReached
            HookEvent(emType, eventMgr, "PlayerEndReachedListener",
                instanceType, instance, "EndReached");

            // mediaPlayer.EventManager.PlayerEncounteredErrorListener += EventManager_PlayerEncounteredErrorListener
            HookEvent(emType, eventMgr, "PlayerEncounteredErrorListener",
                instanceType, instance, "EventManager_PlayerEncounteredErrorListener");

            // mediaPlayer.EventManager.PlayerPreparedListener += EventManager_PlayerPreparedListener
            HookEvent(emType, eventMgr, "PlayerPreparedListener",
                instanceType, instance, "EventManager_PlayerPreparedListener");
        }

        static void SetProp(object obj, string name, object value)
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
                prop.SetValue(obj, value);
        }

        static void HookEvent(Type sourceType, object source, string eventName,
            Type targetType, object target, string methodName)
        {
            var evt = sourceType.GetEvent(eventName);
            if (evt == null)
            {
                // Try as a delegate field (UMP uses public delegate fields, not C# events)
                var field = sourceType.GetField(eventName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    var method = targetType.GetMethod(methodName,
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (method != null)
                    {
                        var del = Delegate.CreateDelegate(field.FieldType, target, method);
                        var existing = (Delegate)field.GetValue(source);
                        field.SetValue(source, Delegate.Combine(existing, del));
                    }
                }
                return;
            }

            var handler = targetType.GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (handler != null)
            {
                var del = Delegate.CreateDelegate(evt.EventHandlerType, target, handler);
                evt.AddEventHandler(source, del);
            }
        }
    }
}

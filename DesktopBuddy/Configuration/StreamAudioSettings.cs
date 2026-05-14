using System;
using System.Linq;
using System.Threading;
using Awwdio;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    internal static string NormalizeViewerCullingMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "distance" ? "distance" : "frustum";
    }

    internal static string NormalizeEncoderPreference(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value switch
        {
            "hevc_nvenc" or "h264_nvenc" or
            "hevc_amf" or "h264_amf" or
            "hevc_qsv" or "h264_qsv" or
            "libx264" or "libx265" => value,
            _ => "auto"
        };
    }

    internal static float NormalizeStreamAudioOutputVolume(float value) => Math.Clamp(value, 0f, 1f);

    internal static string NormalizeStreamAudioGlobalMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "auto" || value == "global" ? value : "positional";
    }

    internal static string NormalizeStreamAudioDistanceSpace(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "local" ? "local" : "global";
    }

    internal static string NormalizeStreamAudioTypeGroup(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value switch
        {
            "soundeffect" or "sound_effect" => "sound_effect",
            "voice" => "voice",
            "ui" or "user_interface" => "ui",
            _ => "multimedia"
        };
    }

    internal static string NormalizeStreamAudioRolloffMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value == "logarithmic_fade_off" || value == "logarithmicfadeoff" ? "logarithmic_fade_off" : "linear";
    }

    private static bool? ParseStreamAudioGlobalMode(string value)
    {
        return NormalizeStreamAudioGlobalMode(value) switch
        {
            "auto" => null,
            "positional" => false,
            _ => true
        };
    }

    private static AudioDistanceSpace ParseStreamAudioDistanceSpace(string value)
    {
        return NormalizeStreamAudioDistanceSpace(value) == "local"
            ? AudioDistanceSpace.Local
            : AudioDistanceSpace.Global;
    }

    private static AudioTypeGroup ParseStreamAudioTypeGroup(string value)
    {
        return NormalizeStreamAudioTypeGroup(value) switch
        {
            "sound_effect" => AudioTypeGroup.SoundEffect,
            "voice" => AudioTypeGroup.Voice,
            "ui" => AudioTypeGroup.UI,
            _ => AudioTypeGroup.Multimedia
        };
    }

    private static AudioRolloffCurve ParseStreamAudioRolloffMode(string value)
    {
        return NormalizeStreamAudioRolloffMode(value) == "linear"
            ? AudioRolloffCurve.Linear
            : AudioRolloffCurve.LogarithmicFadeOff;
    }

    internal static void ApplyStreamAudioSettings(DesktopSession session)
    {
        if (session == null || Config == null)
            return;

        try
        {
            float outputVolume = NormalizeStreamAudioOutputVolume(Config.GetValue(StreamAudioOutputVolume));
            if (session.VideoTexture != null && !session.VideoTexture.IsDestroyed)
                session.VideoTexture.Volume.Value = 1f;

            if (session.StreamVolumeSlider != null && !session.StreamVolumeSlider.IsDestroyed)
                session.StreamVolumeSlider.Value.Value = outputVolume;

            var output = session.StreamAudioOutput;
            if (output == null || output.IsDestroyed)
                return;

            output.Volume.Value = outputVolume;
            output.Global.Value = ParseStreamAudioGlobalMode(Config.GetValue(StreamAudioGlobalMode));
            output.Spatialize.Value = Config.GetValue(StreamAudioSpatialize);
            output.SpatialBlend.Value = Math.Clamp(Config.GetValue(StreamAudioSpatialBlend), 0f, 1f);
            output.DistanceSpace.Value = ParseStreamAudioDistanceSpace(Config.GetValue(StreamAudioDistanceSpace));
            output.DopplerLevel.Value = Math.Clamp(Config.GetValue(StreamAudioDopplerLevel), 0f, 1f);
            output.Pitch.Value = Math.Clamp(Config.GetValue(StreamAudioPitch), 0.5f, 2f);
            output.IgnoreAudioEffects.Value = Config.GetValue(StreamAudioIgnoreAudioEffects);
            output.AudioTypeGroup.Value = ParseStreamAudioTypeGroup(Config.GetValue(StreamAudioTypeGroup));
            output.RolloffMode.Value = ParseStreamAudioRolloffMode(Config.GetValue(StreamAudioRolloffMode));
            output.MinDistance.Value = Math.Clamp(Config.GetValue(StreamAudioMinDistance), 0f, 10f);
            output.MaxDistance.Value = Math.Clamp(Config.GetValue(StreamAudioMaxDistance), 1f, 50f);
            output.SpatializationStartDistance.Value = Math.Clamp(Config.GetValue(StreamAudioSpatializationStartDistance), 0f, 10f);
            output.SpatializationTransitionRange.Value = Math.Clamp(Config.GetValue(StreamAudioSpatializationTransitionRange), 0f, 10f);
            output.Priority.Value = Math.Clamp(Config.GetValue(StreamAudioPriority), 0, 256);
            output.MinScale.Value = Math.Clamp(Config.GetValue(StreamAudioMinScale), 0f, 1000f);
            output.MaxScale.Value = Math.Clamp(Config.GetValue(StreamAudioMaxScale), 0f, 1000f);
        }
        catch (Exception ex)
        {
            Msg($"[StreamAudio] Failed to apply settings: {ex.Message}");
        }
    }

    internal static void ApplyStreamAudioSettingsToAllSessions()
    {
        foreach (var session in ActiveSessions.ToList())
        {
            if (session == null || session.Cleaned)
                continue;
            ApplyStreamAudioSettings(session);
        }
    }

    internal static int RuntimeBitrateMbps => Math.Clamp(Volatile.Read(ref _runtimeBitrateMbps), 1, 200);
    internal static int RuntimeStreamFps => Math.Clamp(Volatile.Read(ref _runtimeStreamFps), 1, 240);
    internal static int RuntimeMaxStreamResolution => Math.Clamp(Volatile.Read(ref _runtimeMaxStreamResolution), 128, 8192);
    internal static string RuntimeEncoderPreference => NormalizeEncoderPreference(_runtimeEncoderPreference);

}

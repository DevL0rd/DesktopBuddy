using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void BuildAudioTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Stream Audio");
        AddOptionRow(ui, state, "Mode", NormalizeStreamAudioGlobalMode(Config?.GetValue(StreamAudioGlobalMode)),
            new[] { ("global", "Global"), ("auto", "Auto"), ("positional", "Positional") },
            value =>
            {
                SaveConfigValue(StreamAudioGlobalMode, NormalizeStreamAudioGlobalMode(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 3, cellWidth: 126f);

        AddSectionHeader(ui, "Spatial Output");
        AddCheckbox(ui, state, "Spatialize", Config?.GetValue(StreamAudioSpatialize) ?? true, value =>
        {
            SaveConfigValue(StreamAudioSpatialize, value);
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Spatial blend", Config?.GetValue(StreamAudioSpatialBlend) ?? 1f, 0f, 1f, value =>
        {
            SaveConfigValue(StreamAudioSpatialBlend, Math.Clamp(value, 0f, 1f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddOptionRow(ui, state, "Distance space", NormalizeStreamAudioDistanceSpace(Config?.GetValue(StreamAudioDistanceSpace)),
            new[] { ("global", "Global"), ("local", "Local") },
            value =>
            {
                SaveConfigValue(StreamAudioDistanceSpace, NormalizeStreamAudioDistanceSpace(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 2, cellWidth: 126f);
        AddOptionRow(ui, state, "Rolloff", NormalizeStreamAudioRolloffMode(Config?.GetValue(StreamAudioRolloffMode)),
            new[] { ("logarithmic_fade_off", "Log fade"), ("linear", "Linear") },
            value =>
            {
                SaveConfigValue(StreamAudioRolloffMode, NormalizeStreamAudioRolloffMode(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 2, cellWidth: 126f);
        AddFloatSlider(ui, state, "Min distance", Config?.GetValue(StreamAudioMinDistance) ?? 1f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioMinDistance, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Max distance", Config?.GetValue(StreamAudioMaxDistance) ?? 30f, 1f, 50f, value =>
        {
            SaveConfigValue(StreamAudioMaxDistance, Math.Clamp(value, 1f, 50f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Spatial start", Config?.GetValue(StreamAudioSpatializationStartDistance) ?? 0.01f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioSpatializationStartDistance, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Transition range", Config?.GetValue(StreamAudioSpatializationTransitionRange) ?? 0.01f, 0f, 10f, value =>
        {
            SaveConfigValue(StreamAudioSpatializationTransitionRange, Math.Clamp(value, 0f, 10f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Min scale", Config?.GetValue(StreamAudioMinScale) ?? 0f, 0f, 1000f, value =>
        {
            SaveConfigValue(StreamAudioMinScale, Math.Clamp(value, 0f, 1000f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Max scale", Config?.GetValue(StreamAudioMaxScale) ?? 1000f, 0f, 1000f, value =>
        {
            SaveConfigValue(StreamAudioMaxScale, Math.Clamp(value, 0f, 1000f));
            ApplyStreamAudioSettingsToAllSessions();
        });

        AddSectionHeader(ui, "Playback");
        AddOptionRow(ui, state, "Type group", NormalizeStreamAudioTypeGroup(Config?.GetValue(StreamAudioTypeGroup)),
            new[] { ("multimedia", "Multimedia"), ("sound_effect", "Sound"), ("voice", "Voice"), ("ui", "UI") },
            value =>
            {
                SaveConfigValue(StreamAudioTypeGroup, NormalizeStreamAudioTypeGroup(value));
                ApplyStreamAudioSettingsToAllSessions();
            }, preferredColumns: 4, cellWidth: 108f);
        AddCheckbox(ui, state, "Ignore audio effects", Config?.GetValue(StreamAudioIgnoreAudioEffects) ?? true, value =>
        {
            SaveConfigValue(StreamAudioIgnoreAudioEffects, value);
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Pitch", Config?.GetValue(StreamAudioPitch) ?? 1f, 0.5f, 2f, value =>
        {
            SaveConfigValue(StreamAudioPitch, Math.Clamp(value, 0.5f, 2f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddFloatSlider(ui, state, "Doppler", Config?.GetValue(StreamAudioDopplerLevel) ?? 0f, 0f, 1f, value =>
        {
            SaveConfigValue(StreamAudioDopplerLevel, Math.Clamp(value, 0f, 1f));
            ApplyStreamAudioSettingsToAllSessions();
        });
        AddIntField(ui, state, "Priority", Config?.GetValue(StreamAudioPriority) ?? 128, 0, 256, value =>
        {
            SaveConfigValue(StreamAudioPriority, Math.Clamp(value, 0, 256));
            ApplyStreamAudioSettingsToAllSessions();
        });
    }

}

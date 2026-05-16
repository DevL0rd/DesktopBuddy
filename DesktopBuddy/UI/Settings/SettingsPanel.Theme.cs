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

    private const float SettingsPanelZOffset = -0.018f;
    private const int SettingsPanelRenderQueue = SettingsUiRenderQueue;
    private const float SettingsStickScrollDeadzone = 0.16f;
    private const float SettingsStickScrollPixelsPerTick = 36f;
    private static readonly colorX SettingsBg = new(0.055f, 0.06f, 0.072f, 0.84f);
    private static readonly colorX SettingsPanel = new(0.085f, 0.095f, 0.115f, 0.94f);
    private static readonly colorX SettingsPanelSoft = new(0.115f, 0.125f, 0.15f, 0.94f);
    private static readonly colorX SettingsAccent = new(0.16f, 0.42f, 0.48f, 0.98f);
    private static readonly colorX SettingsAccentSoft = new(0.13f, 0.24f, 0.28f, 0.96f);
    private static readonly colorX SettingsGradientPurple = new(0.68f, 0.08f, 1f, 0.98f);
    private static readonly colorX SettingsGradientBlue = new(0.05f, 0.5f, 1f, 0.98f);
    private static readonly colorX SettingsGradientMid = new(0.35f, 0.22f, 0.95f, 0.98f);
    private static readonly UiGradientPalette SettingsGradientPalette = new(SettingsGradientPurple, SettingsGradientMid, SettingsGradientBlue);
    private static readonly UiScrollbarStyle SettingsScrollbarStyle = new(
        new colorX(0.07f, 0.078f, 0.095f, 0.72f),
        new colorX(0.02f, 0.024f, 0.032f, 0.55f),
        SettingsAccentSoft,
        SettingsGradientPalette);
    private static readonly colorX SettingsExperimentalOrange = new(1f, 0.48f, 0.08f, 0.98f);
    private static readonly colorX SettingsStatusGood = new(0.12f, 0.58f, 0.28f, 0.98f);
    private static readonly colorX SettingsStatusWarn = new(1f, 0.5f, 0.08f, 0.98f);
    private static readonly colorX SettingsStatusBad = new(0.72f, 0.1f, 0.16f, 0.98f);
    private static readonly colorX SettingsStatusNeutral = new(0.26f, 0.3f, 0.38f, 0.98f);
    private static readonly colorX SettingsText = new(0.93f, 0.94f, 0.97f, 1f);
    private static readonly colorX SettingsSubtext = new(0.68f, 0.72f, 0.78f, 1f);
    private static readonly Uri DefaultViewerAvatar = new("resdb:///bb7d7f1414e0c0a44b4684ecd2a5dc2086c18b3f70c9ed53d467fe96af94e9a9.png");

    private static readonly (SettingsPanelTab Tab, string Label, string Glyph)[] SettingsTabs =
    {
        (SettingsPanelTab.Viewers, "Viewers", "\U0001F465"),
        (SettingsPanelTab.General, "General", "\u2699"),
        (SettingsPanelTab.Stream, "Stream", "\U0001F4E1"),
        (SettingsPanelTab.Network, "Network", "\u2601"),
        (SettingsPanelTab.Devices, "Devices", "\U0001F3A5"),
        (SettingsPanelTab.Audio, "Audio", "\U0001F50A"),
        (SettingsPanelTab.Debug, "Debug", "\U0001F9F0"),
        (SettingsPanelTab.UpdateInfo, "Info", "\u2139"),
    };

    private static readonly (int Value, string Label)[] StreamResolutionOptions =
    {
        (1280, "720p"),
        (1920, "1080p"),
        (2560, "1440p"),
        (3840, "4K"),
    };

    private static readonly (int Value, string Label)[] StreamFpsOptions =
    {
        (30, "30"),
        (60, "60"),
        (90, "90"),
        (120, "120"),
    };

}

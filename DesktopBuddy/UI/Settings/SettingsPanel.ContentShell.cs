using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void BuildSettingsPanelShell(SettingsPanelState state, DesktopSession session)
    {
        state.Canvas.Slot.DestroyChildren();

        var ui = new UIBuilder(state.Canvas);
        var rounded = state.Canvas.Slot.GetComponent<SpriteProvider>();
        if (rounded == null)
            rounded = state.Canvas.Slot.AttachComponent<SpriteProvider>();
        rounded = TextureProviderSettings.ClampWrap(rounded);
        rounded.Texture.Target = UIBuilder.GetCircleTexture(state.Canvas.World);
        rounded.Borders.Value = float4.One * 0.49f;
        rounded.FixedSize.Value = 28f;
        ui.Style.ButtonSprite = rounded;
        ui.Style.NineSliceSizing = NineSliceSizing.FixedSize;
        ui.Style.MinHeight = 44f;
        ui.Style.PreferredHeight = 44f;

        var bg = ui.Image(new colorX(0.055f, 0.06f, 0.072f, 0.8f));
        state.ModalRect = bg.RectTransform;
        SetSettingsModalRect(state);
        bg.Sprite.Target = rounded;
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.VerticalLayout(14f, paddingTop: 22f, paddingRight: 22f, paddingBottom: 22f, paddingLeft: 22f);

        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleHeight = -1f;
        var header = ui.Empty("Header");
        var headerUi = new UIBuilder(header);
        headerUi.LayoutTarget = header;
        var headerLayout = headerUi.HorizontalLayout(12f, 0f, 0f, 0f, 0f, Alignment.MiddleCenter);
        headerLayout.ForceExpandWidth.Value = true;
        headerLayout.ForceExpandHeight.Value = true;

        headerUi.Style.FlexibleWidth = 1f;
        headerUi.Style.MinHeight = 56f;
        headerUi.Style.PreferredHeight = 56f;
        var title = headerUi.Text($"DesktopBuddy - {DesktopBuddyVersion}", bestFit: true, alignment: Alignment.MiddleLeft);
        title.Size.Value = 28f;
        title.Color.Value = SettingsText;

        headerUi.Style.FlexibleWidth = -1f;
        headerUi.Style.MinWidth = 42f;
        headerUi.Style.PreferredWidth = 42f;
        headerUi.Style.MinHeight = 38f;
        headerUi.Style.PreferredHeight = 38f;
        var close = headerUi.Button(OfficialAssets.Common.Icons.Cross, SettingsPanelSoft, SettingsText);
        StyleSettingsButton(close, false);
        close.LocalPressed += (_, data) =>
        {
            if (state.SurfaceSlot != null && !state.SurfaceSlot.IsDestroyed)
                state.SurfaceSlot.ActiveSelf = false;
            if (state.RenderHost != null && !state.RenderHost.IsDestroyed)
                state.RenderHost.ActiveSelf = false;
            StopVirtualCameraPreview(session);
            FlushSettingsConfig();
        };

        ui.Style.MinHeight = 62f;
        ui.Style.PreferredHeight = 62f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.FlexibleWidth = 1f;
        state.TabRoot = ui.Empty("Tabs");

        ui.Style.FlexibleHeight = 1f;
        ui.Style.MinHeight = 0f;
        ui.Style.FlexibleWidth = 1f;
        state.ContentRoot = ui.Empty("Content");

        RebuildSettingsPanel(state, session);
    }

    private static void RebuildSettingsPanel(SettingsPanelState state, DesktopSession session)
    {
        session ??= state.Session;
        SyncLiveCullingStateFromConfig(state);
        RebuildSettingsTabs(state, session);
        RebuildSettingsContent(state, session);
        UpdateCullingPreview(session, state);
    }

    private static void RebuildSettingsTabs(SettingsPanelState state, DesktopSession session)
    {
        state.TabRoot.DestroyChildren();
        DestroyLayoutControllers(state.TabRoot);
        var ui = new UIBuilder(state.TabRoot);
        ui.LayoutTarget = state.TabRoot;
        ui.HorizontalLayout(8f, 0f, 2f, 0f, 2f, Alignment.MiddleCenter);
        foreach (var tab in SettingsTabs)
        {
            ui.Style.MinWidth = 86f;
            ui.Style.PreferredWidth = 86f;
            ui.Style.MinHeight = 54f;
            ui.Style.PreferredHeight = 54f;
            ui.Style.FlexibleWidth = -1f;
            ui.Style.FlexibleHeight = -1f;
            bool active = tab.Tab == state.ActiveTab;
            var tint = active ? SettingsAccent : SettingsPanelSoft;
            var btn = ui.Button(tab.Glyph, tint);
            StyleSettingsButton(btn, active);
            btn.Slot.Name = "Tab " + tab.Label;
            if (btn.Label != null)
            {
                btn.Label.Size.Value = 25f;
                btn.Label.Color.Value = SettingsText;
                btn.Label.Align = Alignment.MiddleCenter;
            }
            var captured = tab.Tab;
            btn.LocalPressed += (_, data) =>
            {
                state.ActiveTab = captured;
                RebuildSettingsPanel(state, session);
            };
        }
    }

    private static void RebuildSettingsContent(SettingsPanelState state, DesktopSession session)
    {
        session ??= state.Session;
        StopVirtualCameraPreview(session);
        state.ContentRoot.DestroyChildren();
        DestroyLayoutControllers(state.ContentRoot);
        var ui = new UIBuilder(state.ContentRoot);
        state.DebugLogText = null;
        state.DebugLogScroll = null;
        state.ContentScroll = null;
        state.DebugLogContent = null;
        state.Scrollbars.Clear();
        ui.LayoutTarget = state.ContentRoot;
        ui.VerticalLayout(0f, 0f, 0f, 0f, 0f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: true);
        var contentUi = BeginRoundedScroll(ui, state, "SettingsContentScroll", 0f, Alignment.TopLeft, out var contentScroll);
        state.ContentScroll = contentScroll;

        switch (state.ActiveTab)
        {
            case SettingsPanelTab.Viewers:
                BuildViewersTab(contentUi, state, session);
                break;
            case SettingsPanelTab.General:
                BuildGeneralTab(contentUi, state);
                break;
            case SettingsPanelTab.Stream:
                BuildStreamTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Audio:
                BuildAudioTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Network:
                BuildNetworkTab(contentUi, state);
                break;
            case SettingsPanelTab.Devices:
                BuildDevicesTab(contentUi, state, session);
                break;
            case SettingsPanelTab.Debug:
                BuildDebugTab(contentUi, state, session);
                break;
            case SettingsPanelTab.UpdateInfo:
                BuildUpdateInfoTab(contentUi, state, session);
                break;
        }
        ScheduleScrollbarGeometryUpdate(state);
    }
}

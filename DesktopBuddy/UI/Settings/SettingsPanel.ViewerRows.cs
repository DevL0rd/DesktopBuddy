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

    private static void AddViewerRow(UIBuilder ui, SettingsPanelState state, DesktopSession session, User user)
    {
        EnsureViewerStreamOverride(session, user);

        ui.Style.MinHeight = 58f;
        ui.Style.PreferredHeight = 58f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var bg = ui.Image(new colorX(0.105f, 0.112f, 0.13f, 0.92f));
        var rounded = CreateRoundedSprite(bg.Slot, state.Canvas.World, 14f);
        bg.Sprite.Target = rounded;
        bg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(bg.RectTransform);
        ui.LayoutTarget = bg.Slot;
        ui.HorizontalLayout(10f, paddingTop: 8f, paddingRight: 10f, paddingBottom: 8f, paddingLeft: 10f, childAlignment: Alignment.MiddleLeft);

        ui.Style.MinWidth = 42f;
        ui.Style.PreferredWidth = 42f;
        ui.Style.MinHeight = 42f;
        ui.Style.PreferredHeight = 42f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = -1f;
        var avatarRoot = ui.Empty("Avatar");
        var avatarUi = new UIBuilder(avatarRoot);
        avatarUi.Style.MinWidth = 42f;
        avatarUi.Style.PreferredWidth = 42f;
        avatarUi.Style.MinHeight = 42f;
        avatarUi.Style.PreferredHeight = 42f;
        var avatarSprite = CreateRoundedSprite(avatarRoot, state.Canvas.World, 10f);
        avatarUi.SpriteMask(avatarSprite, true, out var maskImage);
        maskImage.Tint.Value = new colorX(0.16f, 0.17f, 0.2f, 1f);
        maskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        avatarUi.Nest();
        var avatarTex = TextureProviderSettings.ClampWrap(avatarRoot.AttachComponent<StaticTexture2D>());
        avatarTex.URL.Value = DefaultViewerAvatar;
        if (!string.IsNullOrWhiteSpace(user?.UserID))
        {
            LoadViewerAvatarIcon(avatarRoot, avatarTex, user.UserID);
        }
        var avatar = avatarUi.RawImage(avatarTex, preserveAspect: true);
        avatar.Tint.Value = colorX.White;
        avatarUi.NestOut();

        ui.Style.MinWidth = 0f;
        ui.Style.PreferredWidth = 0f;
        ui.Style.MinHeight = 42f;
        ui.Style.PreferredHeight = 42f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.FlexibleHeight = -1f;
        var name = ui.Text(user?.UserName ?? "", bestFit: true, alignment: Alignment.MiddleLeft);
        name.Size.Value = 19f;
        name.Color.Value = new colorX(0.91f, 0.92f, 0.95f, 1f);

        ui.Style.MinWidth = 86f;
        ui.Style.PreferredWidth = 86f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        ui.Style.FlexibleWidth = -1f;
        string statusText = GetViewerCullingBadgeText(session, user);
        colorX statusColor = GetViewerCullingBadgeColor(session, user);
        var statusBadge = ui.Image(statusColor);
        StyleBadgePill(statusBadge, statusColor);
        ui.NestInto(statusBadge.RectTransform);
        ui.LayoutTarget = statusBadge.Slot;
        ui.HorizontalLayout(0f, childAlignment: Alignment.MiddleCenter);
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = 28f;
        var statusLabel = ui.Text(statusText, bestFit: true, alignment: Alignment.MiddleCenter);
        statusLabel.Size.Value = 12f;
        statusLabel.Color.Value = SettingsText;
        ui.NestOut();

        ui.Style.MinWidth = 58f;
        ui.Style.PreferredWidth = 58f;
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        ui.Style.FlexibleWidth = -1f;
        bool isOwner = IsOwnerViewer(session, user);
        bool viewerEnabled = !isOwner && IsViewerStreamEnabled(session, user);
        var toggle = ui.Button("On", SettingsAccentSoft);
        StyleSettingsButton(toggle, true);
        UpdateToggleButton(toggle, viewerEnabled);
        toggle.LocalPressed += (_, _) =>
        {
            if (isOwner)
            {
                UpdateToggleButton(toggle, false);
                return;
            }
            viewerEnabled = !viewerEnabled;
            SetViewerStreamEnabled(session, user, viewerEnabled);
            UpdateToggleButton(toggle, viewerEnabled);
            statusLabel.Content.Value = GetViewerCullingBadgeText(session, user);
            StyleBadgePill(statusBadge, GetViewerCullingBadgeColor(session, user));
        };
        ui.NestOut();
    }

    private static void LoadViewerAvatarIcon(Slot avatarRoot, StaticTexture2D avatarTex, string userId)
    {
        if (avatarRoot == null || avatarRoot.IsDestroyed ||
            avatarTex == null || avatarTex.IsDestroyed ||
            string.IsNullOrWhiteSpace(userId))
            return;

        var world = avatarRoot.World;
        var engine = world?.Engine;
        if (world == null || engine?.Cloud?.Users == null)
            return;

        Task.Run(async () =>
        {
            try
            {
                var cloudResult = await engine.Cloud.Users.GetUserCached(userId).ConfigureAwait(false);
                if (cloudResult == null || !cloudResult.IsOK)
                    return;

                string iconUrl = cloudResult.Entity?.Profile?.IconUrl;
                if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri))
                    return;

                world.RunInUpdates(0, () =>
                {
                    if (avatarRoot == null || avatarRoot.IsDestroyed ||
                        avatarTex == null || avatarTex.IsDestroyed)
                        return;

                    avatarTex.URL.Value = iconUri;
                });
            }
            catch
            {

            }
        });
    }

}

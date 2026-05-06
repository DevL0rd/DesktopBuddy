using System;
using System.Collections.Generic;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

internal enum DesktopBuddyButtonAction
{
    ToggleBar,
    TogglePreview,
    ResyncStream,
}

public class DesktopBuddyButtonActions : Component, IButtonPressReceiver
{
    private DesktopBuddyButtonAction _action;
    private Slot _root;
    private Slot _barSlot;
    private Slot _barBackSlot;
    private Slot _displaySlot;
    private Canvas _barCanvas;
    private Canvas _barBackCanvas;
    private Slot _expandPanel;
    private ValueField<float> _widthField;
    private SmoothValue<float> _widthSmooth;
    private ValueUserOverride<bool> _streamVisibility;
    private VideoTextureProvider _videoTexture;
    private Image _previewIndicator;
    private float _canvasScale;
    private float _worldHalfWidth;
    private float _barYPosition;
    private float _barHeight;
    private float _collapsedWidth;
    private float _expandedWidth;
    private float _lastBarWidth;
    private bool _barExpanded;
    private readonly HashSet<User> _previewUsers = new();

    internal void ConfigureAction(DesktopBuddyButtonAction action)
    {
        _action = action;
    }

    internal void ConfigureBar(
        Slot root,
        Slot barSlot,
        Slot barBackSlot,
        Canvas barCanvas,
        Canvas barBackCanvas,
        ValueField<float> widthField,
        SmoothValue<float> widthSmooth,
        float canvasScale,
        float worldHalfWidth,
        float barYPosition,
        float barHeight,
        float collapsedWidth,
        float expandedWidth,
        Slot expandPanel = null,
        bool expanded = false)
    {
        _root = root;
        _barSlot = barSlot;
        _barBackSlot = barBackSlot;
        _barCanvas = barCanvas;
        _barBackCanvas = barBackCanvas;
        _widthField = widthField;
        _widthSmooth = widthSmooth;
        _canvasScale = canvasScale;
        _worldHalfWidth = worldHalfWidth;
        _barYPosition = barYPosition;
        _barHeight = barHeight;
        _collapsedWidth = collapsedWidth;
        _expandedWidth = expandedWidth;
        _expandPanel = expandPanel;
        _lastBarWidth = expanded ? expandedWidth : collapsedWidth;
        _barExpanded = expanded;
        if (_expandPanel != null && !_expandPanel.IsDestroyed)
            _expandPanel.ActiveSelf = expanded;
    }

    internal void ConfigurePreview(Slot displaySlot, Image previewIndicator = null)
    {
        _displaySlot = displaySlot;
        _previewIndicator = previewIndicator;
    }

    internal void ConfigureStream(ValueUserOverride<bool> streamVisibility, VideoTextureProvider videoTexture)
    {
        _streamVisibility = streamVisibility;
        _videoTexture = videoTexture;
    }

    internal void UpdateLayout(float worldHalfWidth, float barYPosition)
    {
        _worldHalfWidth = worldHalfWidth;
        _barYPosition = barYPosition;
        ApplyBarLayout(_lastBarWidth);
    }

    internal float CurrentBarWidth => _lastBarWidth;

    public void Pressed(IButton button, ButtonEventData data)
    {
        switch (_action)
        {
            case DesktopBuddyButtonAction.ToggleBar:
                ToggleBar(button, data);
                break;
            case DesktopBuddyButtonAction.TogglePreview:
                TogglePreview(button, data);
                break;
            case DesktopBuddyButtonAction.ResyncStream:
                ResyncStream(button, data);
                break;
        }
    }

    public void Pressing(IButton button, ButtonEventData data)
    {
    }

    public void Released(IButton button, ButtonEventData data)
    {
    }

    public void ToggleBar(IButton button, ButtonEventData data)
    {
        if (_root == null || _root.IsDestroyed || _widthSmooth == null || _widthSmooth.IsDestroyed) return;

        _barExpanded = !_barExpanded;
        if (_expandPanel != null && !_expandPanel.IsDestroyed)
            _expandPanel.ActiveSelf = _barExpanded;
        _widthSmooth.TargetValue.Value = _barExpanded ? _expandedWidth : _collapsedWidth;
        _root.World.RunInUpdates(1, BarUpdateLoop);
    }

    internal void BarUpdateLoop()
    {
        if (_root == null || _root.IsDestroyed ||
            _barSlot == null || _barSlot.IsDestroyed ||
            _barCanvas == null || _barCanvas.IsDestroyed ||
            _widthField == null || _widthField.IsDestroyed ||
            _widthSmooth == null || _widthSmooth.IsDestroyed)
            return;

        float width = _widthField.Value.Value;
        if (width != _lastBarWidth)
        {
            _lastBarWidth = width;
            ApplyBarLayout(width);
        }

        float target = _widthSmooth.TargetValue.Value;
        if (Math.Abs(width - target) > 0.5f)
            _root.World.RunInUpdates(1, BarUpdateLoop);
    }

    private void ApplyBarLayout(float width)
    {
        if (_barCanvas != null && !_barCanvas.IsDestroyed)
            _barCanvas.Size.Value = new float2(width, _barHeight);

        var position = new float3(
            -_worldHalfWidth + width / 2f * _canvasScale,
            _barYPosition,
            0f);

        if (_barSlot != null && !_barSlot.IsDestroyed)
            _barSlot.LocalPosition = position;

        if (_barBackCanvas != null && !_barBackCanvas.IsDestroyed)
            _barBackCanvas.Size.Value = new float2(width, _barHeight);

        if (_barBackSlot != null && !_barBackSlot.IsDestroyed)
            _barBackSlot.LocalPosition = position + new float3(0f, 0f, 0.001f);
    }

    public void TogglePreview(IButton button, ButtonEventData data)
    {
        if (_displaySlot == null || _displaySlot.IsDestroyed ||
            _streamVisibility == null || _streamVisibility.IsDestroyed)
        {
            DesktopBuddyMod.Msg("[Preview] No stream available");
            return;
        }

        var user = data.source?.Slot?.ActiveUser ?? World?.LocalUser;
        if (user == null)
        {
            DesktopBuddyMod.Msg("[Preview] No pressing user");
            return;
        }

        bool streamPreview = !_previewUsers.Contains(user);
        if (streamPreview)
            _previewUsers.Add(user);
        else
            _previewUsers.Remove(user);

        _streamVisibility.SetOverride(user, streamPreview);
        if (user == World?.LocalUser)
        {
            _displaySlot.ActiveSelf = !streamPreview;
            if (_previewIndicator != null && !_previewIndicator.IsDestroyed)
                _previewIndicator.Tint.Value = streamPreview
                    ? new colorX(1f, 0.05f, 0.03f, 1f)
                    : colorX.Clear;
        }

        DesktopBuddyMod.Msg($"[Preview] {user.UserName}: stream={streamPreview}, direct={!streamPreview}");
    }

    public void ResyncStream(IButton button, ButtonEventData data)
    {
        DesktopBuddyMod.Msg("[Resync] Button pressed");
        if (_videoTexture != null && !_videoTexture.IsDestroyed)
        {
            var savedUrl = _videoTexture.URL.Value;
            DesktopBuddyMod.Msg($"[Resync] Forcing full reload: {savedUrl}");
            _videoTexture.URL.Value = null;
            World.RunInUpdates(10, () =>
            {
                if (_videoTexture != null && !_videoTexture.IsDestroyed)
                {
                    _videoTexture.URL.Value = savedUrl;
                    DesktopBuddyMod.Msg($"[Resync] URL restored: {savedUrl}");
                }
            });
        }
        else
        {
            DesktopBuddyMod.Msg("[Resync] No stream available");
        }
    }
}

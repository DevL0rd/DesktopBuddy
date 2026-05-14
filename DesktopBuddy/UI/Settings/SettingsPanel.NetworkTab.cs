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

    private static void BuildNetworkTab(UIBuilder ui, SettingsPanelState state)
    {
        AddSectionHeader(ui, "Stream");
        AddStatusRow(ui, state, "Stream Server", StreamServer == null ? "Stopped" : "Running", StreamServer == null ? SettingsStatusBad : SettingsStatusGood);
        bool cloudflareMode = NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "cloudflare";
        AddStatusRow(ui, state, "Cloudflare", cloudflareMode ? (TunnelUrl == null ? "Waiting" : "Connected") : "Off",
            cloudflareMode ? (TunnelUrl == null ? SettingsStatusWarn : SettingsStatusGood) : SettingsStatusNeutral);
        AddStatusRow(ui, state, "Port", STREAM_PORT.ToString(CultureInfo.InvariantCulture), SettingsStatusNeutral);
        AddOptionRow(ui, state, "Access", NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)),
            new[] { ("cloudflare", "Cloudflare"), ("port_forward", "Port forward") },
            value =>
            {
                SaveConfigValue(StreamNetworkMode, NormalizeStreamNetworkMode(value));
                ApplyStreamNetworkMode();
                RequestStreamEncoderRestart(state.Session, "network mode");
            });

        if (NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "port_forward")
        {
            AddCheckbox(ui, state, "Auto NAT / UPnP", Config?.GetValue(PortForwardUseNat) ?? false, value =>
            {
                SaveConfigValue(PortForwardUseNat, value);
                ApplyStreamNetworkMode();
            });
            AddOptionRow(ui, state, "Host", NormalizePortForwardHostMode(Config?.GetValue(PortForwardHostMode)),
                new[] { ("auto", "Auto public IP"), ("manual", "Manual") },
                value =>
                {
                    SaveConfigValue(PortForwardHostMode, NormalizePortForwardHostMode(value));
                    ApplyStreamNetworkMode();
                });
            if (NormalizePortForwardHostMode(Config?.GetValue(PortForwardHostMode)) == "manual")
            {
                AddStringField(ui, state, "Manual IP / host", Config?.GetValue(PortForwardHost) ?? "", value =>
                {
                    SaveConfigValue(PortForwardHost, value.Trim());
                    ApplyStreamNetworkMode();
                });
            }
            else
            {
                AddInfoRow(ui, state, "Auto public IP", ResolvePortForwardHost() ?? "");
            }
        }
        AddSectionHeader(ui, "MediaMTX");
        AddCheckbox(ui, state, "Use MediaMTX", Config?.GetValue(UseMediaMtx) ?? false, value =>
        {
            SaveConfigValue(UseMediaMtx, value);
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX mode");
        });
        AddStringField(ui, state, "Host", Config?.GetValue(MediaMtxHost) ?? "", value =>
        {
            SaveConfigValue(MediaMtxHost, value.Trim());
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX host");
        });
        AddIntField(ui, state, "Port", Config?.GetValue(MediaMtxPort) ?? 8554, 1, 65535, value =>
        {
            SaveConfigValue(MediaMtxPort, value);
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX port");
        });
        AddStringField(ui, state, "Stream name", Config?.GetValue(MediaMtxStreamName) ?? "", value =>
        {
            SaveConfigValue(MediaMtxStreamName, value.Trim());
            ApplyStreamNetworkMode();
            RequestStreamEncoderRestart(state.Session, "MediaMTX stream name");
        });
    }

}

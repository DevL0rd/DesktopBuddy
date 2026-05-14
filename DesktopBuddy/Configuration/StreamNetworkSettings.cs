using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    internal static string NormalizeStreamNetworkMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value == "port_forward" ? "port_forward" : "cloudflare";
    }

    internal static string NormalizePortForwardHostMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value == "manual" ? "manual" : "auto";
    }

    internal static string NormalizePortForwardAutoIpMode(string value)
    {
        return "external";
    }

    internal static bool UseCloudflareTunnel =>
        Config == null || NormalizeStreamNetworkMode(Config.GetValue(StreamNetworkMode)) == "cloudflare";

    internal static Uri GetBuiltInStreamUrl(int streamId)
    {
        string baseUrl = GetBuiltInStreamBaseUrl();
        return string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri($"{baseUrl}/stream/{streamId}");
    }

    internal static string GetBuiltInStreamBaseUrl()
    {
        if (NormalizeStreamNetworkMode(Config?.GetValue(StreamNetworkMode)) == "cloudflare")
            return TunnelUrl;

        string host = ResolvePortForwardHost();
        return string.IsNullOrWhiteSpace(host) ? null : $"http://{host}:{STREAM_PORT}";
    }

    internal static string ResolvePortForwardHost()
    {
        if (Config != null && NormalizePortForwardHostMode(Config.GetValue(PortForwardHostMode)) == "manual")
            return Config.GetValue(PortForwardHost)?.Trim();

        return GetAutoExternalIPv4Address();
    }

    internal static string GetBestLocalIPv4Address()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        return address.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Msg($"[Network] Failed to auto-detect local IP: {ex.Message}");
        }

        return "";
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static readonly object ExternalIpGate = new();
    private static readonly object NatMappingGate = new();
    private static readonly HttpClient ExternalIpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static string _cachedExternalIPv4;
    private static DateTime _cachedExternalIPv4At;
    private static bool _natMappingActive;

    internal static void ClearExternalIpCache()
    {
        lock (ExternalIpGate)
        {
            _cachedExternalIPv4 = null;
            _cachedExternalIPv4At = DateTime.MinValue;
        }
    }

    internal static string GetAutoExternalIPv4Address()
    {
        lock (ExternalIpGate)
        {
            if (!string.IsNullOrWhiteSpace(_cachedExternalIPv4) &&
                DateTime.UtcNow - _cachedExternalIPv4At < TimeSpan.FromMinutes(10))
            {
                return _cachedExternalIPv4;
            }
        }

        string[] endpoints =
        {
            "https://api.ipify.org",
            "https://checkip.amazonaws.com",
        };

        foreach (string endpoint in endpoints)
        {
            try
            {
                string text = ExternalIpClient.GetStringAsync(endpoint).GetAwaiter().GetResult()?.Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    string value = ip.ToString();
                    lock (ExternalIpGate)
                    {
                        _cachedExternalIPv4 = value;
                        _cachedExternalIPv4At = DateTime.UtcNow;
                    }
                    return value;
                }
            }
            catch (Exception ex)
            {
                Msg($"[PortForward] External IP lookup failed at {endpoint}: {ex.Message}");
            }
        }

        return "";
    }

    internal static void ApplyStreamNetworkMode()
    {
        if (IsMediaMtxEnabled)
        {
            RemovePortForwardNatMapping();
            KillTunnel();
            TunnelUrl = null;
            RefreshBuiltInStreamUrls();
            return;
        }

        if (StreamServer == null)
        {
            RemovePortForwardNatMapping();
            RefreshBuiltInStreamUrls();
            return;
        }

        if (UseCloudflareTunnel)
        {
            RemovePortForwardNatMapping();
            bool tunnelRunning = false;
            try { tunnelRunning = _tunnelProcess != null && !_tunnelProcess.HasExited; }
            catch { }
            if (!tunnelRunning)
                System.Threading.Tasks.Task.Run(() => StartTunnel());
        }
        else
        {
            KillTunnel();
            TunnelUrl = null;
            ApplyPortForwardNatMapping();
        }

        RefreshBuiltInStreamUrls();
    }

    internal static bool ApplyPortForwardNatMapping()
    {
        if (Config?.GetValue(PortForwardUseNat) != true || UseCloudflareTunnel || IsMediaMtxEnabled)
        {
            RemovePortForwardNatMapping();
            return false;
        }

        string localIp = GetBestLocalIPv4Address();
        if (string.IsNullOrWhiteSpace(localIp))
        {
            Msg("[PortForward] UPnP/NAT mapping skipped: no local IPv4 address found");
            return false;
        }

        lock (NatMappingGate)
        {
            try
            {
                object mappings = GetUpnpMappings();
                if (mappings == null)
                {
                    Msg("[PortForward] UPnP/NAT is not available on this system or router");
                    return false;
                }

                TryRemoveUpnpMapping(mappings);
                mappings.GetType().InvokeMember(
                    "Add",
                    BindingFlags.InvokeMethod,
                    null,
                    mappings,
                    new object[] { STREAM_PORT, "TCP", STREAM_PORT, localIp, true, "DesktopBuddy" });

                _natMappingActive = true;
                Msg($"[PortForward] UPnP/NAT TCP {STREAM_PORT} -> {localIp}:{STREAM_PORT}");
                return true;
            }
            catch (Exception ex)
            {
                Msg($"[PortForward] UPnP/NAT mapping failed: {ex.GetBaseException().Message}");
                return false;
            }
        }
    }

    internal static void RemovePortForwardNatMapping()
    {
        lock (NatMappingGate)
        {
            bool wasActive = _natMappingActive;
            try
            {
                object mappings = GetUpnpMappings();
                if (mappings != null)
                    TryRemoveUpnpMapping(mappings);
                if (wasActive)
                    Msg($"[PortForward] Removed UPnP/NAT TCP {STREAM_PORT}");
            }
            catch (Exception ex)
            {
                if (wasActive)
                    Msg($"[PortForward] UPnP/NAT removal failed: {ex.GetBaseException().Message}");
            }
            finally
            {
                _natMappingActive = false;
            }
        }
    }

    internal static void RefreshBuiltInStreamUrls()
    {
        Uri baseUrl = null;
        try
        {
            string rawBase = GetBuiltInStreamBaseUrl();
            if (!string.IsNullOrWhiteSpace(rawBase))
                baseUrl = new Uri(rawBase);
        }
        catch (Exception ex)
        {
            Msg($"[PortForward] Stream base URL refresh failed: {ex.Message}");
        }

        foreach (var session in ActiveSessions)
        {
            if (session?.VideoTexture == null || session.VideoTexture.IsDestroyed || session.StreamId <= 0 || baseUrl == null)
                continue;

            var vtp = session.VideoTexture;
            var capturedSession = session;
            var newUrl = new Uri(baseUrl, $"/stream/{session.StreamId}");
            vtp.World.RunInUpdates(0, () =>
            {
                if (vtp == null || vtp.IsDestroyed)
                    return;
                SetRemoteStreamUrl(capturedSession, newUrl, $"network base refresh streamId={capturedSession.StreamId}");
            });
        }

        lock (_sharedStreams)
        {
            foreach (var shared in _sharedStreams.Values)
            {
                if (shared == null || shared.StreamId <= 0 || baseUrl == null)
                    continue;
                shared.StreamUrl = new Uri(baseUrl, $"/stream/{shared.StreamId}");
            }
        }
    }

    private static object GetUpnpMappings()
    {
        Type natType = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
        if (natType == null)
            return null;

        object nat = Activator.CreateInstance(natType);
        return natType.InvokeMember("StaticPortMappingCollection", BindingFlags.GetProperty, null, nat, null);
    }

    private static void TryRemoveUpnpMapping(object mappings)
    {
        try
        {
            mappings.GetType().InvokeMember(
                "Remove",
                BindingFlags.InvokeMethod,
                null,
                mappings,
                new object[] { STREAM_PORT, "TCP" });
        }
        catch
        {
        }
    }
}

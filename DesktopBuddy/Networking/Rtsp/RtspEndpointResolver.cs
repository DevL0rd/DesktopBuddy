using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public static class RtspEndpointResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static NatPortMapping _mapping;

    public static string ResolvePublicHost(string configured)
    {
        configured = configured?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Log.Msg($"[RTSP] Using configured public host: {configured}");
            return configured;
        }

        try
        {
            string ip = Http.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
            if (IPAddress.TryParse(ip, out _))
            {
                Log.Msg($"[RTSP] Auto-detected public IP via HTTPS: {ip}");
                return ip;
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] Public IP HTTPS detection failed: {ex.Message}");
        }

        string local = GetLocalAddress();
        Log.Msg($"[RTSP] Falling back to local address for RTSP URL: {local}");
        return local;
    }

    public static void TryAutoPortForward(int port)
    {
        Task.Run(() =>
        {
            try
            {
                _mapping = NatPortMapper.TryMapTcpPort(port, "DesktopBuddy RTSP");
                if (_mapping != null)
                    Log.Msg($"[RTSP] Auto port mapping active: TCP {port} -> {NatPortMapper.GetLocalAddress()}:{port}");
                else
                    Log.Msg($"[RTSP] Auto port mapping unavailable. Manually forward TCP {port} to this machine for internet clients.");
            }
            catch (Exception ex)
            {
                Log.Msg($"[RTSP] Auto port mapping failed: {ex.Message}. Manually forward TCP {port} if needed.");
            }
        });
    }

    public static void ReleaseAutoPortForward()
    {
        try
        {
            _mapping?.Dispose();
            _mapping = null;
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] Port mapping release failed: {ex.Message}");
        }
    }

    private static string GetLocalAddress()
    {
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Connect("8.8.8.8", 53);
            if (udp.LocalEndPoint is IPEndPoint endpoint)
                return endpoint.Address.ToString();
        }
        catch { }

        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}

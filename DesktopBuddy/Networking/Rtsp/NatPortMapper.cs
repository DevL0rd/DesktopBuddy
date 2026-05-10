using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using DesktopBuddy;

namespace DesktopBuddy.Networking.Rtsp;

public sealed class NatPortMapping : IDisposable
{
    private readonly Uri _controlUri;
    private readonly string _serviceType;
    private readonly int _externalPort;

    internal NatPortMapping(Uri controlUri, string serviceType, int externalPort)
    {
        _controlUri = controlUri;
        _serviceType = serviceType;
        _externalPort = externalPort;
    }

    public void Dispose()
    {
        NatPortMapper.DeletePortMapping(_controlUri, _serviceType, _externalPort);
    }
}

public static class NatPortMapper
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static NatPortMapping TryMapTcpPort(int port, string description)
    {
        if (!TryDiscoverService(out var controlUri, out string serviceType))
            return null;

        string localAddress = GetLocalAddress();
        AddPortMapping(controlUri, serviceType, port, localAddress, description);
        return new NatPortMapping(controlUri, serviceType, port);
    }

    public static string GetLocalAddress()
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Connect("8.8.8.8", 53);
        return ((IPEndPoint)udp.LocalEndPoint).Address.ToString();
    }

    internal static void DeletePortMapping(Uri controlUri, string serviceType, int port)
    {
        string body =
            "<u:DeletePortMapping xmlns:u=\"" + serviceType + "\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            "<NewExternalPort>" + port + "</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            "</u:DeletePortMapping>";

        try
        {
            SendSoap(controlUri, serviceType, "DeletePortMapping", body);
            Log.Msg($"[RTSP] Released UPnP TCP port mapping {port}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[RTSP] UPnP DeletePortMapping failed: {ex.Message}");
        }
    }

    private static void AddPortMapping(Uri controlUri, string serviceType, int port, string localAddress, string description)
    {
        string body =
            "<u:AddPortMapping xmlns:u=\"" + serviceType + "\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            "<NewExternalPort>" + port + "</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            "<NewInternalPort>" + port + "</NewInternalPort>" +
            "<NewInternalClient>" + WebUtility.HtmlEncode(localAddress) + "</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            "<NewPortMappingDescription>" + WebUtility.HtmlEncode(description) + "</NewPortMappingDescription>" +
            "<NewLeaseDuration>0</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        SendSoap(controlUri, serviceType, "AddPortMapping", body);
        Log.Msg($"[RTSP] UPnP AddPortMapping succeeded: TCP {port} -> {localAddress}:{port}");
    }

    private static void SendSoap(Uri controlUri, string serviceType, string action, string innerBody)
    {
        string envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" + innerBody + "</s:Body>" +
            "</s:Envelope>";

        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "\"" + serviceType + "#" + action + "\"");
        var response = Http.PostAsync(controlUri, content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    private static bool TryDiscoverService(out Uri controlUri, out string serviceType)
    {
        controlUri = null;
        serviceType = null;

        foreach (string location in DiscoverLocations())
        {
            try
            {
                string xml = Http.GetStringAsync(location).GetAwaiter().GetResult();
                if (TryParseControlUri(location, xml, out controlUri, out serviceType))
                {
                    Log.Msg($"[RTSP] UPnP gateway found: {controlUri} service={serviceType}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Msg($"[RTSP] UPnP location probe failed {location}: {ex.Message}");
            }
        }

        return false;
    }

    private static string[] DiscoverLocations()
    {
        const string probe =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2500;
        byte[] bytes = Encoding.ASCII.GetBytes(probe);
        udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

        var locations = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            try
            {
                IPEndPoint remote = null;
                string response = Encoding.ASCII.GetString(udp.Receive(ref remote));
                using var reader = new StringReader(response);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                        locations.Add(line[(colon + 1)..].Trim());
                }
            }
            catch (SocketException)
            {
                break;
            }
        }

        return locations.ToArray();
    }

    private static bool TryParseControlUri(string location, string xml, out Uri controlUri, out string serviceType)
    {
        controlUri = null;
        serviceType = null;

        XDocument doc = XDocument.Parse(xml);
        var service = doc.Descendants()
            .Where(e => e.Name.LocalName == "service")
            .Select(e => new
            {
                Type = e.Elements().FirstOrDefault(x => x.Name.LocalName == "serviceType")?.Value,
                Control = e.Elements().FirstOrDefault(x => x.Name.LocalName == "controlURL")?.Value
            })
            .FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.Type) &&
                !string.IsNullOrWhiteSpace(s.Control) &&
                (s.Type.Contains("WANIPConnection") || s.Type.Contains("WANPPPConnection")));

        if (service == null) return false;

        serviceType = service.Type;
        controlUri = new Uri(new Uri(location), service.Control);
        return true;
    }
}

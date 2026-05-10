using System.Collections.Generic;

namespace DesktopBuddy.Networking.Rtsp;

internal sealed class RtspRequest
{
    public string Method { get; }
    public string Uri { get; }
    public Dictionary<string, string> Headers { get; }

    public RtspRequest(string method, string uri, Dictionary<string, string> headers)
    {
        Method = method;
        Uri = uri;
        Headers = headers;
    }
}

using Serein.Rtsp.Client;

namespace Serein.Hosting;

/// <summary>Configured RTSP sources. A source keeps retrying until the host stops.</summary>
public sealed class SereinOptions
{
    public List<RtspSourceOptions> Sources { get; set; } = [];
    public RtspOutputOptions RtspOutput { get; set; } = new();
}

public sealed class RtspOutputOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8554;
}

public sealed class RtspSourceOptions
{
    public string StreamId { get; set; } = "camera";
    public string Channel { get; set; } = "main";
    public string Url { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public RtspTransportMode Transport { get; set; } = RtspTransportMode.Tcp;
    public bool UsePsDemux { get; set; }
    public int ConnectTimeoutMilliseconds { get; set; } = 5000;
    public int ReconnectDelayMilliseconds { get; set; } = 3000;
    public bool RecordToFile { get; set; }
}

/// <summary>A source snapshot. Credentials and source URLs are intentionally excluded.</summary>
public sealed record SourceStatus(string StreamId, string Channel, bool Connected, int Subscribers,
    string State, DateTimeOffset? ConnectedAt);

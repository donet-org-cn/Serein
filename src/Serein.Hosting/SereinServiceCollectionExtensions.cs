using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using Serein.Media.Relay;
using Serein.Rtsp.Server;

namespace Serein.Hosting;

public static class SereinServiceCollectionExtensions
{
    /// <summary>Adds the shared media hub, configured RTSP sources and background reconnection.</summary>
    public static IServiceCollection AddSerein(this IServiceCollection services, Action<SereinOptions>? configure = null)
    {
        services.AddOptions<SereinOptions>().Configure(options => configure?.Invoke(options))
            .Validate(Valid, "RTSP source identifiers, URLs, timeouts and unique stream/channel pairs are required.")
            .ValidateOnStart();
        services.TryAddSingleton<MediaStreamHub>();
        services.TryAddSingleton<SereinMediaService>();
        services.TryAddSingleton(provider =>
        {
            var output = provider.GetRequiredService<IOptions<SereinOptions>>().Value.RtspOutput;
            return new RtspServer(provider.GetRequiredService<ILogger<RtspServer>>())
            {
                ListenEndPoint = new(IPAddress.Parse(output.ListenAddress), output.Port)
            };
        });
        services.AddHostedService<SereinRtspOutputService>();
        services.AddHostedService(provider => provider.GetRequiredService<SereinMediaService>());
        return services;
    }

    private static bool Valid(SereinOptions options)
    {
        var keys = new HashSet<(string, string)>();
        return options.RtspOutput is { Port: > 0 and <= 65535 } &&
            IPAddress.TryParse(options.RtspOutput.ListenAddress, out _) &&
            options.Sources is not null && options.Sources.All(source => source is not null &&
            ValidSegment(source.StreamId) && ValidSegment(source.Channel) &&
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == "rtsp" &&
            source.ConnectTimeoutMilliseconds > 0 && source.ReconnectDelayMilliseconds > 0 &&
            keys.Add((source.StreamId, source.Channel)));
    }

    private static bool ValidSegment(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

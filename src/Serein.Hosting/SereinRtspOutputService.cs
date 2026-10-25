using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serein.Media.Relay;
using Serein.Rtsp.Server;

namespace Serein.Hosting;

internal sealed class SereinRtspOutputService(MediaStreamHub hub, RtspServer server, IOptions<SereinOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.RtspOutput.Enabled) return;
        server.ResolveVideoInfo = (streamId, channel) =>
            hub.TryGetVideoInfo(streamId, channel, out var codec, out var config) ? (codec, config) : null;
        hub.FramePublishedCallback += server.PushFrame;
        try { await server.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch { hub.FramePublishedCallback -= server.PushFrame; throw; }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hub.FramePublishedCallback -= server.PushFrame;
        await server.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

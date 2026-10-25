using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serein.Media.Relay;
using Serein.Rtsp.Client;
using Serein.Rtsp.Relay;

namespace Serein.Hosting;

/// <summary>Bridges configured sources into the shared hub and owns their complete lifecycle.</summary>
public sealed class SereinMediaService : BackgroundService
{
    private readonly MediaStreamHub _hub;
    private readonly ILoggerFactory _loggers;
    private readonly SourceState[] _sources;

    public SereinMediaService(MediaStreamHub hub, IOptions<SereinOptions> options, ILoggerFactory loggers)
    {
        _hub = hub;
        _loggers = loggers;
        _sources = options.Value.Sources.Select(source => new SourceState(source)).ToArray();
        _hub.RecorderFactory ??= (options, logger) => new StreamRecorder(options, logger);
    }

    public IReadOnlyList<SourceStatus> GetSources()
    {
        var counts = _hub.GetSubscriptionCounts();
        return _sources.Select(source =>
        {
            var status = Volatile.Read(ref source.Status);
            counts.TryGetValue(new(source.Options.StreamId, source.Options.Channel), out int viewers);
            return status with { Subscribers = viewers };
        }).ToArray();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_sources.Select(source => RunSourceAsync(source, stoppingToken)));

    private async Task RunSourceAsync(SourceState source, CancellationToken stoppingToken)
    {
        var options = source.Options;
        _hub.RegisterStream(new StreamOptions
        {
            StreamId = options.StreamId, Channel = options.Channel,
            Source = StreamSource.Rtsp, RecordToFile = options.RecordToFile
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var client = new RtspPullClient(_loggers.CreateLogger<RtspPullClient>())
                {
                    Transport = options.Transport, UsePsDemux = options.UsePsDemux,
                    ConnectTimeout = options.ConnectTimeoutMilliseconds,
                    Username = options.Username, Password = options.Password
                };
                client.FrameReceivedAsync += (_, frame) =>
                {
                    _hub.PublishAndRelease(frame, options.StreamId, options.Channel);
                    return ValueTask.CompletedTask;
                };
                client.StreamDisconnectedAsync += (_, _) =>
                {
                    disconnected.TrySetResult();
                    return ValueTask.CompletedTask;
                };
                try
                {
                    SetState(source, false, "connecting");
                    await client.ConnectAsync(options.Url, stoppingToken).ConfigureAwait(false);
                    _hub.MarkStreamConnected(options.StreamId, options.Channel);
                    SetState(source, true, "connected");
                    await disconnected.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Source URLs can contain credentials; do not include connection exceptions in this public status.
                    SetState(source, false, "retrying");
                }
                finally
                {
                    await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await _hub.MarkStreamDisconnectedAsync(options.StreamId, options.Channel, "Source disconnected").ConfigureAwait(false);
                }

                SetState(source, false, "retrying");
                await Task.Delay(options.ReconnectDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            SetState(source, false, "stopped");
            await _hub.UnregisterStreamAsync(options.StreamId, options.Channel).ConfigureAwait(false);
        }
    }

    private static void SetState(SourceState source, bool connected, string state) =>
        Volatile.Write(ref source.Status, new(source.Options.StreamId, source.Options.Channel,
            connected, 0, state, connected ? DateTimeOffset.UtcNow : null));

    private sealed class SourceState(RtspSourceOptions options)
    {
        public RtspSourceOptions Options { get; } = options;
        public SourceStatus Status = new(options.StreamId, options.Channel, false, 0, "pending", null);
    }
}

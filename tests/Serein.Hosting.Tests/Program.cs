using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using Serein.AspNetCore;
using Serein.Hosting;
using Serein.Media;
using Serein.Media.Relay;
using Serein.Rtsp.Client;
using Serein.Rtsp.Server;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Invalid and duplicate configured sources fail at startup", InvalidOptions),
    ("HTTP and WebSocket-FLV share the registered media hub", SharedHub),
    ("Offline RTSP source retries and stops without leaking credentials", OfflineSource),
    ("RTSP input reaches HTTP-FLV and RTSP output and drains on stop", EndToEnd),
};
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}"); }
}
Console.WriteLine($"Hosting integration tests: {tests.Length - failed}/{tests.Length}");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Until(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
    while (!condition()) await Task.Delay(20, deadline.Token);
}

static async Task InvalidOptions()
{
    foreach (bool duplicate in new[] { false, true })
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSerein(options =>
        {
            options.Sources.Add(new() { Url = duplicate ? "rtsp://127.0.0.1/live" : "http://invalid" });
            if (duplicate) options.Sources.Add(new() { Url = "rtsp://127.0.0.1/live" });
        });
        using var host = builder.Build();
        bool rejected = false;
        try { await host.StartAsync(); }
        catch (OptionsValidationException) { rejected = true; }
        Check(rejected, "Invalid options reached the network layer.");
    }
}

static async Task SharedHub()
{
    var builder = Host.CreateApplicationBuilder();
    var shared = new MediaStreamHub();
    builder.Services.AddSingleton(shared);
    builder.Services.AddSerein();
    builder.Services.AddRtpMediaHub();
    using var host = builder.Build();
    Check(ReferenceEquals(shared, host.Services.GetRequiredService<MediaStreamHub>()), "A duplicate hub was registered.");
    Check(host.Services.GetServices<MediaStreamHub>().Count() == 1, "Multiple hubs exist.");
    await shared.DisposeAsync();
}

static async Task OfflineSource()
{
    // Keep a port bound without listening so connect fails deterministically.
    using var blocked = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    blocked.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)blocked.LocalEndPoint!).Port;
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddSerein(options => options.Sources.Add(new()
    {
        StreamId = "offline", Url = $"rtsp://user:secret@127.0.0.1:{port}/live",
        ConnectTimeoutMilliseconds = 200, ReconnectDelayMilliseconds = 50
    }));
    using var host = builder.Build();
    await host.StartAsync();
    var service = host.Services.GetRequiredService<SereinMediaService>();
    await Until(() => service.GetSources()[0].State == "retrying");
    string status = System.Text.Json.JsonSerializer.Serialize(service.GetSources());
    Check(!status.Contains("secret") && !status.Contains("rtsp://"), "Credentials leaked in source status.");
    await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Check(service.GetSources()[0].State == "stopped", "Source worker survived host shutdown.");
}

static int Port(RtspServer server) => ((IPEndPoint)((Socket)typeof(RtspServer)
    .GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!).LocalEndPoint!).Port;

static async Task EndToEnd()
{
    byte[] accessUnit = [0,0,0,1,0x67,0x64,0,0x1f,0xac,0xd9,0,0,0,1,0x68,0xee,0x3c,0x80,0,0,0,1,0x65,0x88,0x84];
    await using var origin = new RtspServer
    {
        ListenEndPoint = new(IPAddress.Loopback, 0),
        ResolveVideoInfo = (_, _) => (MediaCodec.H264, accessUnit)
    };
    await origin.StartAsync(default);

    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    // Supply a server with an ephemeral listener, avoiding a fixed output port in tests.
    var output = new RtspServer { ListenEndPoint = new(IPAddress.Loopback, 0) };
    builder.Services.AddSingleton(output);
    builder.Services.AddSerein(options =>
    {
        options.RtspOutput.Enabled = true;
        options.Sources.Add(new()
        {
            StreamId = "camera", Channel = "main", Url = $"rtsp://127.0.0.1:{Port(origin)}/camera/main",
            Transport = RtspTransportMode.Udp, ReconnectDelayMilliseconds = 50
        });
    });
    await using var app = builder.Build();
    app.UseWebSockets(); app.UseHttpFlv(); app.UseWebSocketFlv(); app.MapSereinApi();
    using var stopFrames = new CancellationTokenSource();
    var publisher = Task.Run(async () =>
    {
        long timestamp = 0;
        try
        {
            while (!stopFrames.IsCancellationRequested)
            {
                var frame = PooledMediaFrame.Create(accessUnit, MediaFrameKind.Video, MediaCodec.H264, true, timestamp += 40);
                try { origin.PushFrame(frame, "camera", "main"); }
                finally { frame.Release(); }
                await Task.Delay(40, stopFrames.Token);
            }
        }
        catch (OperationCanceledException) when (stopFrames.IsCancellationRequested) { }
    });
    try
    {
        await app.StartAsync();
        var media = app.Services.GetRequiredService<SereinMediaService>();
        await Until(() => media.GetSources()[0].Connected &&
            app.Services.GetRequiredService<MediaStreamHub>().TryGetVideoInfo("camera", "main", out _, out _));
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await http.GetAsync("/live/camera/main.flv", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Check(response.Content.Headers.ContentType?.MediaType == "video/x-flv", "Unexpected FLV content type.");
        await using var body = await response.Content.ReadAsStreamAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[13];
        await body.ReadExactlyAsync(header, deadline.Token);
        Check(header.AsSpan(0, 3).SequenceEqual("FLV"u8), "FLV header missing.");
        byte[] tag = new byte[11];
        await body.ReadExactlyAsync(tag, deadline.Token);
        Check(tag[0] == 9, "No FLV video tag arrived from RTSP.");

        using var websocket = new ClientWebSocket();
        await websocket.ConnectAsync(new Uri(address.Replace("http://", "ws://") + "/ws-live/camera/main"), deadline.Token);
        var bytes = new byte[4096];
        var message = await websocket.ReceiveAsync(bytes.AsMemory(), deadline.Token);
        Check(message.Count >= 3 && bytes.AsSpan(0, 3).SequenceEqual("FLV"u8), "WebSocket-FLV header missing.");

        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var playback = new RtspPullClient { Transport = RtspTransportMode.Udp, UsePsDemux = false };
        playback.FrameReceivedAsync += (_, frame) => { frame.Release(); arrived.TrySetResult(); return ValueTask.CompletedTask; };
        await playback.ConnectAsync($"rtsp://127.0.0.1:{Port(output)}/camera/main", deadline.Token);
        await arrived.Task.WaitAsync(deadline.Token);
        await playback.StopAsync();
        await websocket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        string sourceStatus = await http.GetStringAsync("/api/streams");
        Check(sourceStatus.Contains("camera") && sourceStatus.Contains("connected"), "Status endpoint did not report the source.");
        response.Dispose();
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Check(media.GetSources()[0].State == "stopped", "Source did not stop.");
        Check(app.Services.GetRequiredService<MediaStreamHub>().GetSubscriptionCounts().Count == 0, "Shutdown retained subscriptions.");
    }
    finally
    {
        stopFrames.Cancel();
        await publisher;
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        await output.DisposeAsync();
        await origin.StopAsync(default);
    }
}

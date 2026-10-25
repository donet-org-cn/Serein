using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serein.Media;
using Serein.Media.Relay;
using Serein.Rtsp.Relay;
using Serein.Rtsp.Server;

/// <summary>覆盖服务器回调停止、闲置连接和会话归属的回归测试。</summary>
internal static class Round5ServerTests
{
    /// <summary>通过反射读取测试目标的私有字段。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <param name="name">名称。</param>
    /// <returns>反射读取到的目标成员值。</returns>
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    /// <summary>获取 数量。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <returns>获取 数量计算得到的数值。</returns>
    private static int Count(object value) => (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    /// <summary>验证回归条件，不满足时抛出带错误说明的异常。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    /// <summary>创建延续异步执行的测试完成信号。</summary>
    /// <returns>延续异步执行的完成信号，可由测试控制完成时机。</returns>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>在截止时间前轮询等待测试条件成立。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    { while (!condition()) await Task.Delay(5, cancellationToken); }
    /// <summary>连接指定回环 RTSP 或 FLV 测试服务器。</summary>
    /// <param name="server">服务端实例。</param>
    /// <returns>已经建立连接的 TCP 客户端。</returns>
    private static async Task<TcpClient> ConnectAsync(object server)
    {
        var peer = new TcpClient();
        await peer.ConnectAsync((IPEndPoint)((Socket)Field(server, "_listener")).LocalEndPoint!);
        return peer;
    }

    /// <summary>验证服务器媒体回调内请求停止不会发生自等待。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task CallbackStopAsync()
    {
        foreach (bool rtsp in new[] { true, false })
        foreach (bool dispose in new[] { false, true })
        {
            using var release = new CancellationTokenSource();
            var entered = Signal(); var returned = Signal();
            BackgroundService server = rtsp
                ? new RtspServer(NullLogger.Instance) { ListenEndPoint = new(IPAddress.Loopback, 0) }
                : new TcpFlvServer(NullLogger.Instance) { ListenEndPoint = new(IPAddress.Loopback, 0) };
            // <summary>在媒体回调内部触发并观察测试中的停止操作。</summary>
            // <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
            async Task Callback()
            {
                entered.TrySetResult();
                Task stopping = dispose ? ((IAsyncDisposable)server).DisposeAsync().AsTask() : server.StopAsync(CancellationToken.None);
                await stopping.WaitAsync(release.Token);
                returned.TrySetResult();
            }
            if (server is RtspServer rtspServer) rtspServer.OnStreamRequested = (_, _, _) => Callback();
            if (server is TcpFlvServer flvServer) flvServer.StreamFeed = async (_, _, _, _) => await Callback();
            try
            {
                await server.StartAsync(default);
                using var peer = await ConnectAsync(server);
                string request = rtsp ? "DESCRIBE rtsp://localhost/live/0 RTSP/1.0\r\nCSeq: 1\r\n\r\n" : "live/0\n";
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await server.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
                Check(Count(Field(server, "_clientTasks")) == 0 && ((Socket)Field(server, "_listener")).SafeHandle.IsClosed,
                    "Callback shutdown returned without later completing the tracked connection cleanup.");
            }
            finally
            {
                release.Cancel();
                await server.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
                await ((IAsyncDisposable)server).DisposeAsync();
            }
        }
        await CompletedCallbackScopeAsync();
    }

    /// <summary>验证复制到后台任务的回调作用域在回调结束后失效。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task CompletedCallbackScopeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var server = new RtspServer(NullLogger.Instance)
        { ListenEndPoint = new(IPAddress.Loopback, 0), ResolveVideoInfo = (_, _) => (MediaCodec.H264, null) };
        var startLateStop = Signal(); var lateStopStarted = Signal(); var lateStopCompleted = Signal();
        var blockedEntered = Signal(); var releaseBlocked = Signal();
        Task? descendant = null;
        server.OnStreamRequested = async (streamId, _, _) =>
        {
            if (streamId == "parent")
            {
                descendant = Task.Run(async () =>
                {
                    await startLateStop.Task.WaitAsync(deadline.Token);
                    var stopping = server.StopAsync(default);
                    lateStopStarted.TrySetResult();
                    await stopping;
                    lateStopCompleted.TrySetResult();
                });
            }
            else
            {
                blockedEntered.TrySetResult();
                await releaseBlocked.Task;
            }
        };
        await server.StartAsync(default);
        using var first = await ConnectAsync(server);
        using var second = await ConnectAsync(server);
        try
        {
            await RequestAsync(first, "DESCRIBE", "parent", deadline.Token);
            await second.GetStream().WriteAsync(Encoding.ASCII.GetBytes("DESCRIBE rtsp://localhost/blocked/0 RTSP/1.0\r\nCSeq: 1\r\n\r\n"), deadline.Token);
            await blockedEntered.Task.WaitAsync(deadline.Token);
            startLateStop.TrySetResult();
            await lateStopStarted.Task.WaitAsync(deadline.Token);
            await Task.Delay(50, deadline.Token);
            Check(!lateStopCompleted.Task.IsCompleted, "A child task kept callback-only Stop privileges after its callback returned.");
            releaseBlocked.TrySetResult();
            await lateStopCompleted.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            startLateStop.TrySetResult(); releaseBlocked.TrySetResult();
            if (descendant != null) await descendant.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>验证没有媒体数据时 FLV 对端关闭仍可被及时检测。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task IdleFlvDisconnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hub = new MediaStreamHub(NullLogger.Instance);
        await using var server = new TcpFlvServer(NullLogger.Instance) { ListenEndPoint = new(IPAddress.Loopback, 0) };
        await using var bridge = new FlvStreamBridge(hub, server, NullLogger.Instance);
        bridge.Start();
        await server.StartAsync();
        for (int connection = 0; connection < 12; connection++)
        {
            using (var peer = await ConnectAsync(server))
            {
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("idle/0\n"), timeout.Token);
                await peer.GetStream().ReadExactlyAsync(new byte[13], timeout.Token);
                await UntilAsync(() => hub.GetSubscriptionCounts().Values.Sum() == 1, timeout.Token);
                if ((connection & 1) != 0) peer.Client.LingerState = new LingerOption(true, 0);
            }
            await UntilAsync(() => hub.GetSubscriptionCounts().Count == 0 && Count(Field(server, "_clientTasks")) == 0, timeout.Token);
        }
        Check(!server.ExecuteTask!.IsCompleted, "One peer's EOF canceled the whole FLV server.");
    }

    /// <summary>验证同一 RTSP 控制连接的多次会话操作保持资源归属。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspConnectionOwnershipAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RtspServer(NullLogger.Instance)
        { ListenEndPoint = new(IPAddress.Loopback, 0), ResolveVideoInfo = (_, _) => (MediaCodec.H264, null) };
        await server.StartAsync(default);
        using var first = await ConnectAsync(server);
        using var survivor = await ConnectAsync(server);
        var retiredSockets = new List<Socket>();
        for (int generation = 0; generation < 3; generation++)
        {
            string key = "stream" + generation;
            foreach (string method in new[] { "DESCRIBE", "SETUP", "PLAY" })
                await RequestAsync(first, method, key, timeout.Token);
            var sessions = (IDictionary)Field(server, "_sessions");
            var group = (IDictionary)sessions[key + "/0"]!;
            retiredSockets.Add((Socket)Field(group.Values.Cast<object>().Single(), "_rtpSocket"));
        }
        // SETUP 可以在没有 DESCRIBE 的情况下建立会话；它仍须获得唯一的连接归属，不能删除同一媒体流上其他客户端的会话。
        string directSetup = await RequestAsync(survivor, "SETUP", "stream0", timeout.Token);
        Check(directSetup.Contains("Session: ") && !directSetup.Contains("Session: \n"), "Direct SETUP has no session identity.");
        await RequestAsync(survivor, "PLAY", "stream0", timeout.Token);
        first.Dispose();
        await UntilAsync(() => Count(Field(server, "_clientTasks")) == 1, timeout.Token);
        Check(retiredSockets.All(socket => socket.SafeHandle.IsClosed), "A prior DESCRIBE/PLAY session kept its UDP socket after the TCP owner closed.");
        var remaining = (IDictionary)Field(server, "_sessions");
        Check(remaining.Count == 1 && Count(remaining["stream0/0"]!) == 1, "Disconnect retained old streams or removed the surviving client.");
        survivor.Dispose();
        await UntilAsync(() => Count(Field(server, "_clientTasks")) == 0, timeout.Token);
        Check(remaining.Count == 0, "Direct SETUP/PLAY leaked its session on disconnect.");
    }

    /// <summary>验证缓慢读取请求的客户端不会无限阻塞服务器停止。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task SlowRtspReaderStopAsync()
    {
        await using var server = new RtspServer(NullLogger.Instance) { ListenEndPoint = new(IPAddress.Loopback, 0) };
        await server.StartAsync(default);
        using var peer = await ConnectAsync(server);
        peer.ReceiveBufferSize = 1024;
        using var cancellation = new CancellationTokenSource();
        byte[] batch = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("OPTIONS rtsp://localhost/live/0 RTSP/1.0\r\nCSeq: 1\r\n\r\n", 2048)));
        var writer = Task.Run(async () =>
        {
            try { for (int i = 0; i < 64; i++) await peer.GetStream().WriteAsync(batch, cancellation.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        try
        {
            await Task.Delay(500);
            Check(peer.Available > 0 && !writer.IsCompleted, "Slow-reader fixture failed to establish TCP backpressure.");
            using var healthy = await ConnectAsync(server);
            using var healthyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Check((await RequestAsync(healthy, "OPTIONS", "healthy", healthyDeadline.Token)).Contains("200 OK"),
                "One blocked response prevented another client from receiving a response.");
            await server.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
            Check(Count(Field(server, "_clientTasks")) == 0, "Stop retained a response blocked on a slow reader.");
        }
        finally
        {
            peer.Dispose(); cancellation.Cancel(); await writer;
            await server.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>向本地 RTSP 测试服务器发送请求并读取响应。</summary>
    /// <param name="peer">对端连接。</param>
    /// <param name="method">协议方法名称。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>异步操作的结果：向本地 RTSP 测试服务器发送请求并读取响应得到的文本。</returns>
    private static async Task<string> RequestAsync(TcpClient peer, string method, string streamId, CancellationToken token)
    {
        string extra = method == "SETUP" ? "Transport: RTP/AVP;unicast;client_port=35000-35001\r\n" : "";
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"{method} rtsp://localhost/{streamId}/0/track0 RTSP/1.0\r\nCSeq: 1\r\n{extra}\r\n"), token);
        using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII, false, 4096, true);
        string response = ""; int length = 0;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            response += line + "\n";
            if (line.StartsWith("Content-Length:")) length = int.Parse(line[15..]);
        }
        if (length > 0 && await reader.ReadBlockAsync(new char[length], token) != length) throw new EndOfStreamException();
        return response;
    }
}

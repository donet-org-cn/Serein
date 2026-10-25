using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Serein.Media;
using Serein.Media.Server;
using Serein.Rtsp.Client;

/// <summary>覆盖 RTSP 增量读取、网络重连与 RTP 取消重启的回归测试。</summary>
internal static class Round4NetworkTests
{
    /// <summary>验证回归条件，不满足时抛出带错误说明的异常。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    /// <summary>通过反射读取测试目标的私有字段。</summary>
    /// <param name="target">操作目标。</param>
    /// <param name="name">名称。</param>
    /// <returns>反射读取到的目标成员值。</returns>
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    /// <summary>创建延续异步执行的测试完成信号。</summary>
    /// <returns>延续异步执行的完成信号，可由测试控制完成时机。</returns>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>在截止时间前轮询等待测试条件成立。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    { while (!condition()) await Task.Delay(5, token); }

    /// <summary>验证截断的 RTSP 握手响应不会留下活动连接。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspTruncatedHandshakeAsync()
    {
        foreach (string method in new[] { "OPTIONS", "DESCRIBE", "SETUP", "PLAY" })
        foreach (string malformed in new[]
        {
            "RTSP/1.0 200 OK\r\nCSeq: 1\r\n",
            "RTSP/1.0 200 OK\r\nContent-Length: 10\r\n\r\nX",
            "RTSP/1.0 200 OK\r\nContent-Length: -1\r\n\r\n",
            "RTSP/1.0 200 OK\r\nContent-Length: 0\r\nContent-Length: 1\r\n\r\nX",
            "RTSP/1.0 200 OK\r\nContent-Length: 4194305\r\n\r\n",
            "RTSP/1.0 200 OK\r\nX-Pad: " + new string('x', 65536)
        })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var fixture = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await ServeHandshakeAsync(peer, timeout.Token, async (stream, requestMethod, reply) =>
                {
                    if (requestMethod == method)
                    {
                        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(malformed), timeout.Token); }
                        catch (IOException) { }
                        return false;
                    }
                    await stream.WriteAsync(reply, timeout.Token);
                    return true;
                });
            });
            try
            {
                await using var client = new RtspPullClient(NullLogger.Instance);
                bool rejected = false;
                try { await client.ConnectAsync(Url(listener), timeout.Token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
                Check(rejected && !client.IsConnected && client.LocalRtpPort == 0,
                    $"Incomplete/malformed {method} response was accepted or leaked RTP ports.");
                Check(Field(client, "_tcpSocket") == null && Field(client, "_responseReader") == null,
                    "Failed RTSP handshake retained a transport or pooled reader.");
                await fixture;
            }
            finally { timeout.Cancel(); listener.Stop(); }
        }
    }

    /// <summary>验证拆分到多个 TCP 读操作的交错 RTP 包可完整重组。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspIncrementalMediaAsync()
    {
        foreach (int split in new[] { 1, 2, 3, 6 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var fixture = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                peer.NoDelay = true;
                var stream = peer.GetStream();
                await ServeHandshakeAsync(peer, timeout.Token, async (output, method, reply) =>
                {
                    if (method == "PLAY")
                    {
                        byte[] rtcp = [36, 1, 0, 8, 1, 2, 3, 4, 5, 6, 7, 8];
                        await output.WriteAsync(rtcp.AsMemory(0, split), timeout.Token);
                        await Task.Delay(20, timeout.Token);
                        await output.WriteAsync(rtcp.AsMemory(split), timeout.Token);
                        // 完整响应与第一帧媒体放在同一次 TCP 写入中。
                        await output.WriteAsync(reply.Concat(InterleavedPacket(1)).ToArray(), timeout.Token);
                        return false;
                    }
                    await output.WriteAsync(reply.AsMemory(0, 1), timeout.Token);
                    await Task.Delay(5, timeout.Token);
                    await output.WriteAsync(reply.AsMemory(1), timeout.Token);
                    return true;
                });
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                await ReadRequestAsync(reader, timeout.Token); // 媒体帧断言完成后发送 TEARDOWN。
            });
            try
            {
                await using var client = new RtspPullClient(NullLogger.Instance) { Transport = RtspTransportMode.Tcp, UsePsDemux = false };
                var frameReceived = Signal();
                int frames = 0;
                client.FrameReceivedAsync += (_, frame) =>
                {
                    try { Interlocked.Increment(ref frames); frameReceived.TrySetResult(); }
                    finally { frame.Release(); }
                    return ValueTask.CompletedTask;
                };
                await client.ConnectAsync(Url(listener), timeout.Token);
                await frameReceived.Task.WaitAsync(timeout.Token);
                Check(frames == 1 && client.Stats?.PacketsReceived == 1,
                    "Segmented RTCP/header bytes consumed the first complete RTP frame.");
                await client.StopAsync(timeout.Token);
                await fixture;
            }
            finally { timeout.Cancel(); listener.Stop(); }
        }
    }

    /// <summary>验证真实回环连接断开后的 RTSP 重连。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspNetworkReconnectAsync()
    {
        foreach (RtspTransportMode transport in Enum.GetValues<RtspTransportMode>())
        foreach (bool reset in new[] { false, true })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var disconnect = Signal();
            var fixture = Task.Run(async () =>
            {
                for (int generation = 0; generation < 2; generation++)
                {
                    using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                    await ServeHandshakeAsync(peer, timeout.Token);
                    if (generation == 0)
                    {
                        await disconnect.Task.WaitAsync(timeout.Token);
                        if (transport == RtspTransportMode.Tcp)
                            await peer.GetStream().WriteAsync(new byte[] { 36, 0, 0, 14, 128 }, timeout.Token);
                        if (reset) peer.Client.LingerState = new LingerOption(true, 0);
                    }
                    else
                    {
                        using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII, false, 4096, true);
                        await ReadRequestAsync(reader, timeout.Token);
                    }
                }
            });
            try
            {
                await using var client = new RtspPullClient(NullLogger.Instance) { Transport = transport };
                var reconnected = Signal();
                int notifications = 0;
                int retiredNotifications = 0;
                client.StreamDisconnectedAsync += async (_, _) =>
                {
                    Interlocked.Increment(ref notifications);
                    Check(!client.IsConnected && client.LocalRtpPort == 0, "RTSP disconnect was published before transport cleanup.");
                    await client.ConnectAsync(Url(listener), timeout.Token);
                    reconnected.TrySetResult();
                };
                client.StreamDisconnectedAsync += (_, _) =>
                {
                    Interlocked.Increment(ref retiredNotifications);
                    return ValueTask.CompletedTask;
                };
                await client.ConnectAsync(Url(listener), timeout.Token);
                disconnect.TrySetResult();
                await reconnected.Task.WaitAsync(timeout.Token);
                await Task.Delay(30, timeout.Token);
                Check(client.IsConnected && notifications == 1 && retiredNotifications == 0,
                    "Old RTSP receive completion or a later subscriber affected the replacement connection.");
                await client.StopAsync(timeout.Token);
                Check(!client.IsConnected && client.LocalRtpPort == 0, "Replacement RTSP connection failed to stop.");
                await fixture;
            }
            finally { timeout.Cancel(); listener.Stop(); }
        }
    }

    /// <summary>验证 RTP 输入取消后可在安全时机重新启动。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtpCancellationRestartAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = new RtpClient(NullLogger.Instance)
        { UsePsDemux = false, RawFrameKind = MediaFrameKind.Audio, RawCodec = MediaCodec.G711A, RawClockRate = 8000 };
        using var sender = new UdpClient();
        int frames = 0, disconnects = 0;
        client.FrameReceivedAsync += (_, frame) => { frame.Release(); Interlocked.Increment(ref frames); return ValueTask.CompletedTask; };
        client.StreamDisconnectedAsync += (_, _) => { Interlocked.Increment(ref disconnects); return ValueTask.CompletedTask; };
        for (int generation = 1; generation <= 8; generation++)
        {
            using var cancel = new CancellationTokenSource();
            await client.StartAsync(0, cancel.Token);
            var socket = (Socket)Field(client, "_udpSocket")!;
            var receive = (Task)Field(client, "_receiveTask")!;
            await sender.SendAsync(RtpPacket(1), new IPEndPoint(IPAddress.Loopback, client.LocalPort), timeout.Token);
            await UntilAsync(() => frames == generation, timeout.Token);
            cancel.Cancel(); await receive.WaitAsync(timeout.Token);
            await UntilAsync(() => disconnects == generation, timeout.Token);
            if (Field(client, "_disconnectTask") is Task notification) await notification.WaitAsync(timeout.Token);
            Check(socket.SafeHandle.IsClosed && client.LocalPort == 0, "Canceled RTP receiver retained its UDP socket.");
            Check(client.Stats.PacketsLost == 0 && client.Stats.PacketsOutOfOrder == 0,
                "Restart reused the old RTP sequence window and dropped the new stream's first packet.");
        }
        using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
        bool canceled = false;
        try { await client.StartAsync(0, alreadyCanceled.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && client.LocalPort == 0, "Already-canceled RTP Start bound a fresh socket.");

        await using var slow = new RtpClient(NullLogger.Instance)
        { UsePsDemux = false, RawFrameKind = MediaFrameKind.Audio, RawCodec = MediaCodec.G711A, RawClockRate = 8000 };
        using var slowCancel = new CancellationTokenSource();
        var borrowed = Signal();
        var release = Signal();
        int deliveries = 0;
        bool ownedUntilReturn = false;
        slow.FrameReceivedAsync += async (_, frame) =>
        {
            try
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    var memory = frame.GetMemory();
                    borrowed.TrySetResult();
                    await release.Task;
                    ownedUntilReturn = memory.Span[0] == 101 && frame.GetMemory().Length == 2;
                }
            }
            finally { frame.Release(); }
        };
        await slow.StartAsync(0, slowCancel.Token);
        var slowReceive = (Task)Field(slow, "_receiveTask")!;
        var slowSocket = (Socket)Field(slow, "_udpSocket")!;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, slow.LocalPort);
            await sender.SendAsync(RtpPacket(1), endpoint, timeout.Token);
            await borrowed.Task.WaitAsync(timeout.Token);
            for (ushort sequence = 2; sequence < 12; sequence++)
                await sender.SendAsync(RtpPacket(sequence), endpoint, timeout.Token);
            await UntilAsync(() => slow.Stats.PacketsReceived == 11, timeout.Token);
            slowCancel.Cancel(); await slowReceive.WaitAsync(timeout.Token);
            bool rejected = false;
            try { await slow.StartAsync(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && slowSocket.SafeHandle.IsClosed, "Canceled generation with borrowed frames was replaced or retained its socket.");
            release.TrySetResult();
            await UntilAsync(() => !(bool)Field(slow, "_dispatching")!, timeout.Token);
            Check(ownedUntilReturn && deliveries == 1, "Cancellation released a borrowed frame or delivered retired queued frames.");
            await slow.StartAsync();
            await sender.SendAsync(RtpPacket(1), new IPEndPoint(IPAddress.Loopback, slow.LocalPort), timeout.Token);
            await UntilAsync(() => deliveries == 2, timeout.Token);
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>根据回环监听端口生成测试 RTSP 拉流地址。</summary>
    /// <param name="listener">接受连接的监听器。</param>
    /// <returns>指向本地监听器的 RTSP 地址。</returns>
    private static string Url(TcpListener listener) => $"rtsp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live";
    /// <summary>在本地模拟端点回应 RTSP 建连握手。</summary>
    /// <param name="peer">对端连接。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <param name="replyWriter">向测试连接写入响应的委托。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task ServeHandshakeAsync(TcpClient peer, CancellationToken token,
        Func<NetworkStream, string, byte[], Task<bool>>? replyWriter = null)
    {
        var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
        while (true)
        {
            var (method, cseq) = await ReadRequestAsync(reader, token);
            string body = method == "DESCRIBE" ? "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=body$must-stay-text\r\nt=0 0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:track0\r\n" : "";
            string extra = method == "SETUP" ? "Session: test\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n" : "";
            byte[] reply = Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n{extra}Content-Length: {body.Length}\r\n\r\n{body}");
            if (replyWriter != null) { if (!await replyWriter(stream, method, reply)) return; }
            else await stream.WriteAsync(reply, token);
            if (method == "PLAY") return;
        }
    }
    /// <summary>读取本地 RTSP 测试请求的方法和序号。</summary>
    /// <param name="reader">读取数据的对象。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>异步操作的结果：读取本地 RTSP 测试请求的方法和序号得到的(string Method, string Cseq)对象。</returns>
    private static async Task<(string Method, string Cseq)> ReadRequestAsync(StreamReader reader, CancellationToken token)
    {
        string first = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
        string cseq = "0";
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
            if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase)) cseq = line[5..].Trim();
        return (first.Split(' ')[0], cseq);
    }
    /// <summary>构造带指定序列号的测试 RTP 数据包。</summary>
    /// <param name="sequence">RTP 包序列号。</param>
    /// <returns>构造带指定序列号的测试 RTP 数据包后的字节数据。</returns>
    private static byte[] RtpPacket(ushort sequence)
    {
        byte[] packet = [128, 224, 0, 0, 0, 1, 95, 144, 0, 0, 0, 1, 101, 136];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        return packet;
    }
    /// <summary>为测试 RTP 包添加 RTSP 交错传输头。</summary>
    /// <param name="sequence">RTP 包序列号。</param>
    /// <returns>为测试 RTP 包添加 RTSP 交错传输头后的字节数据。</returns>
    private static byte[] InterleavedPacket(ushort sequence) => new byte[] { 36, 0, 0, 14 }.Concat(RtpPacket(sequence)).ToArray();
}

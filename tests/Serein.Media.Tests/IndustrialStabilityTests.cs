using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Serein.Media;
using Serein.Media.Protocol;
using Serein.Media.Relay;
using Serein.Media.Server;
using Serein.Rtsp.Client;
using Serein.Rtsp.Server;

/// <summary>覆盖媒体队列、端口与请求边界的稳定性回归。</summary>
internal static class IndustrialStabilityTests
{
    /// <summary>定位非公开实例成员使用的反射标志。</summary>
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    /// <summary>验证回归条件，不满足时抛出带错误说明的异常。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    /// <summary>通过反射读取测试目标的私有字段。</summary>
    /// <param name="target">操作目标。</param>
    /// <param name="name">名称。</param>
    /// <returns>反射读取到的目标成员值。</returns>
    private static object Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    /// <summary>获取 数量。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <returns>获取 数量计算得到的数值。</returns>
    private static int Count(object value) => (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    /// <summary>创建延续异步执行的测试完成信号。</summary>
    /// <returns>延续异步执行的完成信号，可由测试控制完成时机。</returns>
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>在截止时间前轮询等待测试条件成立。</summary>
    /// <param name="predicate">轮询等待的条件。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    /// <summary>验证 RTSP 各订阅者分别持有并释放帧引用。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspFrameOwnershipAsync()
    {
        await using var client = new RtspPullClient(NullLogger.Instance);
        var gate = Signal();
        int observed = 0;
        client.FrameReceivedAsync += (_, frame) => { frame.Release(); return ValueTask.CompletedTask; };
        client.FrameReceivedAsync += async (_, frame) =>
        {
            try
            {
                var memory = frame.GetMemory();
                await gate.Task;
                Check(memory.Length == 160 && memory.Span[0] == 0xA5 && frame.GetMemory().Length == 160,
                    "An earlier subscriber returned this consumer's frame to the pool.");
                observed++;
            }
            finally { frame.Release(); }
        };
        client.FrameReceivedAsync += (_, frame) =>
        {
            try { throw new InvalidOperationException("Expected subscriber failure."); }
            finally { frame.Release(); }
        };
        var frame = PooledMediaFrame.Create(Enumerable.Repeat((byte)0xA5, 160).ToArray(), MediaFrameKind.Audio, MediaCodec.G711A, false, 0);
        var pending = (ValueTask)typeof(RtspPullClient).GetMethod("OnRtpFrameReceived", Private)!.Invoke(client, [null, frame])!;
        Check(!pending.IsCompleted && frame.GetMemory().Length == 160, "RTSP did not wait for all subscribers or lost ownership.");
        gate.TrySetResult(true);
        await pending;
        Check(observed == 1 && !frame.TryRetain(), "RTSP leaked a frame reference or failed to invoke a subscriber.");
    }

    /// <summary>验证慢回调下 RTP 帧队列保持有界。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtpBackpressureAsync()
    {
        await using var client = new RtpClient(NullLogger.Instance)
        {
            UsePsDemux = false, RawFrameKind = MediaFrameKind.Audio, RawCodec = MediaCodec.G711A,
            RawClockRate = 8000, FrameDispatchQueueCapacity = 16, FrameHandlerStopTimeout = TimeSpan.FromMilliseconds(50)
        };
        var gate = Signal();
        var completed = Signal();
        int outstanding = 0;
        PooledMediaFrame? retained = null;
        client.FrameReceivedAsync += async (_, frame) =>
        {
            Interlocked.Increment(ref outstanding);
            retained = frame;
            try { await gate.Task; Check(frame.GetMemory().Length == 160, "Stop returned in-flight memory prematurely."); }
            finally { frame.Release(); Interlocked.Decrement(ref outstanding); completed.TrySetResult(true); }
        };
        try
        {
            byte[] payload = new byte[160];
            for (int index = 0; index < 4096; index++) Push(client, payload, (ushort)index, 8);
            var queued = (MediaStreamSubscription)Field(client, "_pendingFrames");
            Check(outstanding == 1 && queued.Reader.Count <= 16, "Slow callback work exceeded the configured bound.");
            var watch = Stopwatch.StartNew();
            await client.StopAsync();
            Check(watch.Elapsed < TimeSpan.FromSeconds(1) && queued.Reader.Count == 0, "Stop did not release queued frames within its deadline.");
            Check(outstanding == 1 && retained!.GetMemory().Length == 160, "Stop invalidated a consumer's in-flight frame.");
            Push(client, payload, 4097, 8);
            Check(outstanding == 1, "Stopped RTP client accepted new callback work.");
        }
        finally { gate.TrySetResult(true); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !(bool)Field(client, "_dispatching"));
        Check(outstanding == 0 && !retained!.TryRetain(), "Completed callback did not release its frame.");
    }

    /// <summary>验证帧积压后从关键帧恢复解码边界。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtpQueueRecoveryAsync()
    {
        await using var client = new RtpClient(NullLogger.Instance)
            { UsePsDemux = false, RawCodec = MediaCodec.H264, FrameDispatchQueueCapacity = 16 };
        var gate = Signal();
        var delivered = new List<bool>();
        client.FrameReceivedAsync += async (_, frame) =>
        {
            try
            {
                lock (delivered) delivered.Add(frame.IsKeyFrame);
                await gate.Task;
            }
            finally { frame.Release(); }
        };
        try
        {
            Push(client, [0x65, 1], 0, 96);
            for (ushort index = 1; index <= 40; index++) Push(client, [0x41, 1], index, 96);
            Push(client, [0x65, 2], 41, 96);
        }
        finally { gate.TrySetResult(true); }
        await UntilAsync(() => { lock (delivered) return delivered.Count >= 2; });
        await UntilAsync(() => !(bool)Field(client, "_dispatching"));
        lock (delivered) Check(delivered.SequenceEqual(new[] { true, true }), "Overflow delivered undecodable P frames before the recovery I frame.");
    }

    /// <summary>验证停止期限不会提前释放阻塞回调持有的帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtpBlockedStopAsync()
    {
        await using var client = new RtpClient(NullLogger.Instance)
        {
            UsePsDemux = false, RawFrameKind = MediaFrameKind.Audio, RawCodec = MediaCodec.G711A,
            FrameHandlerStopTimeout = TimeSpan.FromMilliseconds(50)
        };
        using var frameGate = new ManualResetEventSlim(false);
        var entered = Signal();
        var disconnectEntered = Signal();
        var disconnectGate = Signal();
        PooledMediaFrame? held = null;
        client.FrameReceivedAsync += (_, frame) =>
        {
            held = frame; entered.TrySetResult(true);
            try { frameGate.Wait(TimeSpan.FromSeconds(3)); Check(frame.GetMemory().Length == 160, "Blocked consumer memory was released early."); }
            finally { frame.Release(); }
            return ValueTask.CompletedTask;
        };
        client.StreamDisconnectedAsync += async (_, _) => { disconnectEntered.TrySetResult(true); await disconnectGate.Task; };
        var push = Task.Run(() => Push(client, new byte[160], 0, 8));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var watch = Stopwatch.StartNew();
            await client.StopAsync();
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "A blocked consumer bypassed the stop timeout.");
            await disconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(held!.GetMemory().Length == 160, "Stop released a frame still used by a synchronous callback.");
        }
        finally { frameGate.Set(); disconnectGate.TrySetResult(true); }
        await push.WaitAsync(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !(bool)Field(client, "_stopInProgress"));
    }

    /// <summary>验证日志组件抛错不会使媒体分发卡死。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtpThrowingLoggerAsync()
    {
        await using var client = new RtpClient(new ThrowingErrorLogger())
            { UsePsDemux = false, RawFrameKind = MediaFrameKind.Audio, RawCodec = MediaCodec.G711A };
        var gate = Signal();
        int observed = 0;
        client.FrameReceivedAsync += async (_, frame) =>
        {
            try { await gate.Task; throw new InvalidOperationException("Expected consumer failure."); }
            finally { frame.Release(); Interlocked.Increment(ref observed); }
        };
        Push(client, new byte[160], 0, 8);
        Push(client, new byte[160], 1, 8);
        gate.TrySetResult(true);
        await UntilAsync(() => Volatile.Read(ref observed) == 2 && !(bool)Field(client, "_dispatching"));
        Push(client, new byte[160], 2, 8);
        Check(observed == 3 && !(bool)Field(client, "_dispatching"), "A throwing logger stranded the callback dispatcher.");
    }

    /// <summary>在错误日志写入时抛出异常的测试日志器。</summary>
    private sealed class ThrowingErrorLogger : ILogger
    {
        /// <summary>返回测试日志器的作用域；该替身不创建实际作用域。</summary>
        /// <param name="state">当前处理状态。</param>
        /// <typeparam name="TState">日志状态的类型。</typeparam>
        /// <returns>该测试日志器不创建作用域，返回 null。</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>声明测试日志器是否接收指定级别的日志。</summary>
        /// <param name="logLevel">日志级别。</param>
        /// <returns>声明测试日志器是否接收指定级别的日志是否成立。</returns>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>接收日志以触发当前测试需要的异常或同步信号。</summary>
        /// <param name="logLevel">日志级别。</param>
        /// <param name="eventId">日志事件标识。</param>
        /// <param name="state">当前处理状态。</param>
        /// <param name="exception">当前处理的异常。</param>
        /// <param name="formatter">将日志状态与异常格式化为文本的委托。</param>
        /// <typeparam name="TState">日志状态的类型。</typeparam>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) throw new InvalidOperationException("Expected logger failure.");
        }
    }

    /// <summary>构造 RTP 包并送入指定接收器，驱动帧分发测试。</summary>
    /// <param name="client">客户端连接。</param>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="sequence">RTP 包序列号。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    private static void Push(RtpClient client, byte[] payload, ushort sequence, byte pt)
    {
        byte[] packet = new byte[payload.Length + 12];
        RtpPacketBuilder.WritePacket(packet, RtpPacketBuilder.CreateHeader(pt, sequence, (uint)sequence * 160, 1, true), payload);
        client.PushRtpPacket(packet);
    }

    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspRequestLimitsAsync()
    {
        await using var server = new RtspServer(NullLogger.Instance)
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), MaxRequestHeaderBytes = 1024,
            MaxRequestBodyBytes = 32, RequestTimeout = TimeSpan.FromMilliseconds(150)
        };
        await server.StartAsync(CancellationToken.None);
        foreach (string request in new[]
        {
            "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nX-Pad: " + new string('A', 2048),
            "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 33\r\n\r\n",
            "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nContent-Length: -1\r\n\r\n",
            "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\n",
            "OPTIONS ",
            "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 4\r\n\r\na"
        })
        {
            using var client = await ConnectServerAsync(server);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            try
            {
                int read = await client.GetStream().ReadAsync(new byte[256], timeout.Token);
                Check(read == 0, "An oversized or incomplete RTSP request was accepted.");
            }
            catch (IOException) { }
        }
    }

    /// <summary>验证一条 TCP 连接内连续 RTSP 请求的边界解析。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspPipeliningAsync()
    {
        await using var server = new RtspServer(NullLogger.Instance) { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0) };
        await server.StartAsync(CancellationToken.None);
        using var client = await ConnectServerAsync(server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stream = client.GetStream();
        string first = "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 4\r\n\r\ntest";
        string second = "OPTIONS rtsp://localhost/live RTSP/1.0\r\nCSeq: 2\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(first[..7]), timeout.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(first[7..] + second), timeout.Token);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
        for (int index = 1; index <= 2; index++)
        {
            var (status, headers) = await ReadMessageAsync(reader, timeout.Token);
            Check(status.Contains("200 OK") && headers["CSeq"] == index.ToString(), "RTSP dropped a pipelined request or parsed body bytes as a request.");
        }
    }

    /// <summary>连接本地启动的 RTSP 测试服务器。</summary>
    /// <param name="server">服务端实例。</param>
    /// <returns>异步操作的结果：连接本地启动的 RTSP 测试服务器得到的TcpClient对象。</returns>
    private static async Task<TcpClient> ConnectServerAsync(RtspServer server)
    {
        var listener = (Socket)Field(server, "_listener");
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        return client;
    }

    /// <summary>验证 RTSP 断连回调中的重连流程。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspReconnectAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sessions = new List<string>();
        var fixture = Task.Run(async () =>
        {
            for (int round = 1; round <= 2; round++)
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                while (true)
                {
                    var (request, headers) = await ReadMessageAsync(reader, timeout.Token);
                    if (request.StartsWith("TEARDOWN")) break;
                    string method = request.Split(' ')[0];
                    if (method == "PLAY") sessions.Add(headers.GetValueOrDefault("Session", ""));
                    await ReplyAsync(stream, method, headers["CSeq"], "session" + round, timeout.Token);
                }
            }
        });
        try
        {
            await using var client = new RtspPullClient(NullLogger.Instance) { Transport = RtspTransportMode.Tcp };
            string url = $"rtsp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live";
            for (int round = 0; round < 2; round++) { await client.ConnectAsync(url, timeout.Token); await client.StopAsync(timeout.Token); }
            await fixture;
            Check(sessions.SequenceEqual(new[] { "session1", "session2" }), "RTSP reconnect used a stale session id.");
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }

    /// <summary>验证 RTSP 握手失败后端口与连接资源回滚。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task RtspHandshakeRollbackAsync()
    {
        foreach (string stallAt in new[] { "OPTIONS", "SETUP", "PLAY" })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var fixture = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                while (true)
                {
                    var (request, headers) = await ReadMessageAsync(reader, timeout.Token);
                    string method = request.Split(' ')[0];
                    if (method == stallAt) { Check(await reader.ReadLineAsync(timeout.Token) == null, "Timed-out RTSP connection remained open."); return; }
                    await ReplyAsync(stream, method, headers["CSeq"], "partial", timeout.Token);
                }
            });
            try
            {
                await using var client = new RtspPullClient(NullLogger.Instance) { ConnectTimeout = 200 };
                bool timedOut = false;
                try { await client.ConnectAsync($"rtsp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live", timeout.Token); }
                catch (TimeoutException) { timedOut = true; }
                Check(timedOut && !client.IsConnected && client.LocalRtpPort == 0, "Handshake timeout did not roll back RTP receivers.");
                Check(Field(client, "_tcpSocket") == null && Field(client, "_networkStream") == null && Field(client, "_session") == null,
                    "Failed handshake retained transport/session state.");
                await fixture;
            }
            finally { timeout.Cancel(); listener.Stop(); }
        }
    }

    /// <summary>读取本地 RTSP 测试连接的一条消息。</summary>
    /// <param name="reader">读取数据的对象。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>异步操作的结果：读取本地 RTSP 测试连接的一条消息得到的(string FirstLine, Dictionary&lt;string, string&gt; Headers)对象。</returns>
    private static async Task<(string FirstLine, Dictionary<string, string> Headers)> ReadMessageAsync(StreamReader reader, CancellationToken token)
    {
        string first = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = await reader.ReadLineAsync(token);
            if (string.IsNullOrEmpty(line)) return (first, headers);
            int colon = line.IndexOf(':');
            if (colon >= 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
    }

    /// <summary>向本地测试连接写入 RTSP 成功响应。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="method">协议方法名称。</param>
    /// <param name="cseq">SIP 或 RTSP 请求序号。</param>
    /// <param name="session">当前会话。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task ReplyAsync(NetworkStream stream, string method, string cseq, string session, CancellationToken token)
    {
        string body = method == "DESCRIBE" ? "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=probe\r\nt=0 0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:track0\r\n" : "";
        string extra = method == "SETUP" ? $"Session: {session};timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n" : "";
        string response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n{extra}Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).AsTask();
    }
}

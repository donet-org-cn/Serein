using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serein.Media;
using Serein.Media.Server;
using Serein.Media.Relay;
using Serein.Rtsp.Server;

/// <summary>覆盖媒体质量统计、PS/RTP 帧分发及连接回收的回归测试。</summary>
internal static class Round3MediaTests
{
    /// <summary>验证媒体质量监视器的并发统计与问题记录。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static Task QualityMonitorConcurrencyAsync()
    {
        var monitor = new StreamQualityMonitor(NullLogger.Instance);
        monitor.Bind("camera", "main");
        int notifications = 0;
        monitor.StreamQualityIssueAsync += (_, _) => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; };
        Parallel.For(0, 64, _ => monitor.OnPacketLoss(100));
        Check(notifications == 1, "Concurrent identical warnings bypassed atomic rate limiting");
        for (int round = 0; round < 5; round++)
            Parallel.For(0, 8, lane =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    if (lane == 0) monitor.Reset();
                    else if (lane % 2 == 0) monitor.OnPacketLoss(100);
                    else monitor.OnVideoFrame(i % 2 == 0, i % 2 == 0 ? 10000 : 0);
                }
            });
        monitor.Reset();
        bool reentered = false;
        monitor.StreamQualityIssueAsync += (_, _) =>
        {
            // 如果回调持有内部锁，等待另一线程重置就会死锁。
            Task.Run(monitor.Reset).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            reentered = true; return ValueTask.CompletedTask;
        };
        monitor.OnPacketLoss(100);
        Check(reentered, "Quality callback ran while retaining its state lock");
        return Task.CompletedTask;
    }

    /// <summary>验证 PS/RTP 慢回调下帧队列保持有界。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task PsCallbacksBoundedAsync()
    {
        await using var server = new PsRtpReceiver(NullLogger.Instance) { FrameDispatchQueueCapacity = 16 };
        const uint ssrc = 47;
        server.RegisterStream("original", "video", ssrc);
        var entered = NewSignal(); var release = NewSignal(); var completed = NewSignal();
        int active = 0, peak = 0, calls = 0;
        PooledMediaFrame? held = null;
        var seen = new ConcurrentQueue<string>();
        server.FrameReceivedAsync += async (_, device, _, frame) =>
        {
            int concurrency = Interlocked.Increment(ref active);
            peak = Math.Max(peak, concurrency); Interlocked.Increment(ref calls);
            seen.Enqueue(device);
            if (device == "original") { held = frame; entered.TrySetResult(); await release.Task; Check(frame.Length != 0, "In-flight frame returned before callback completed"); }
            Interlocked.Decrement(ref active); completed.TrySetResult();
        };
        Feed(server, ssrc, 1, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (ushort i = 2; i <= 512; i++) Feed(server, ssrc, i, true);
        Check(calls == 1 && peak == 1, "One slow callback allowed unbounded in-flight frames");
        var sessions = (IDictionary)typeof(PsRtpReceiver).GetField("_sessions", Hidden)!.GetValue(server)!;
        var old = (PsRtpStreamSession)sessions[ssrc]!;
        Check(old.FrameQueue.Reader.Count <= 16, "Frame queue exceeded configured capacity");
        server.UnregisterStream(ssrc);
        Check(old.FrameQueue.Reader.Count == 0 && held is { IsReleased: false }, "Unregister failed queue/borrowed ownership contract");
        for (int i = 0; i < 100; i++)
        {
            bool rejected = false;
            try { server.RegisterStream("replacement", "video", ssrc); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("in-flight frame callback")) { rejected = true; }
            Check(rejected, "Re-registration bypassed a still borrowed frame in the retired generation");
        }
        release.TrySetResult(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => held!.IsReleased);
        var retired = (IDictionary)typeof(PsRtpReceiver).GetField("_retiredFrameSessions", Hidden)!.GetValue(server)!;
        await Until(() => { lock (typeof(PsRtpReceiver).GetField("_sessionLifecycleSync", Hidden)!.GetValue(server)!) return retired.Count == 0; });
        server.RegisterStream("replacement", "video", ssrc);
        Feed(server, ssrc, 1, true);
        await Until(() => seen.Contains("replacement"));
        Check(seen.Count(s => s == "original") == 1, "Old queued frames reached replacement generation");
        server.UnregisterStream(ssrc);
    }

    /// <summary>验证 PS/RTP 丢弃积压帧后保留关键帧恢复条件。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task PsQueueKeyFrameRecoveryAsync()
    {
        await using var server = new PsRtpReceiver(NullLogger.Instance) { FrameDispatchQueueCapacity = 16 };
        server.RegisterStream("camera", "main", 48);
        var entered = NewSignal(); var release = NewSignal();
        var seen = new ConcurrentQueue<bool>();
        server.FrameReceivedAsync += async (_, _, _, frame) =>
        {
            seen.Enqueue(frame.IsKeyFrame);
            if (seen.Count == 1) { entered.TrySetResult(); await release.Task; }
        };
        Feed(server, 48, 1, true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (ushort i = 2; i <= 24; i++) Feed(server, 48, i, false);
        Feed(server, 48, 25, true); Feed(server, 48, 26, false);
        release.TrySetResult(); await Until(() => seen.Count >= 3);
        Check(seen.SequenceEqual(new[] { true, true, false }), "Overflow resumed in the middle of a GOP");
    }

    /// <summary>验证注销不会释放尚在回调中使用的媒体帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task PsBlockedCallbackUnregisterAsync()
    {
        await using var server = new PsRtpReceiver(NullLogger.Instance);
        server.RegisterStream("blocked", "main", 49);
        using var release = new ManualResetEventSlim(); var entered = NewSignal();
        PooledMediaFrame? held = null;
        server.FrameReceivedAsync += (_, _, _, frame) =>
        {
            held = frame; entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        };
        var feeding = Task.Run(() => Feed(server, 49, 1, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await feeding.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Run(() => server.UnregisterStream(49)).WaitAsync(TimeSpan.FromSeconds(1));
            Check(held is { IsReleased: false }, "Blocked callback lost its borrowed frame");
        }
        finally { release.Set(); }
        await Until(() => held!.IsReleased);
    }

    /// <summary>验证服务器频繁建立和关闭连接后的停止清理。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static async Task ServerChurnAndStopAsync()
    {
        foreach (int kind in new[] { 0, 1, 2 })
        {
            using var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            occupied.Start();
            var endpoint = (IPEndPoint)occupied.LocalEndpoint;
            BackgroundService server = kind switch
            {
                0 => new RtspServer(NullLogger.Instance) { ListenEndPoint = endpoint },
                1 => new TcpFlvServer(NullLogger.Instance) { ListenEndPoint = endpoint },
                _ => new PsRtpReceiver(NullLogger.Instance) { ListenEndPoint = endpoint, EnableUdp = true, EnableTcp = true }
            };
            if (server is PsRtpReceiver gb) gb.RegisterStream("partial-start", "main", 99);
            try
            {
                bool rejected = false;
                try
                {
                    await server.StartAsync(default);
                    if (server.ExecuteTask != null) await server.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (SocketException) { rejected = true; }
                Check(rejected, "Occupied TCP port did not reject startup");
                foreach (string field in kind == 2 ? new[] { "_udpSocket", "_tcpSocket" } : new[] { "_listener" })
                {
                    var socket = (Socket)server.GetType().GetField(field, Hidden)!.GetValue(server)!;
                    Check(socket.SafeHandle.IsClosed, "Startup failure left a listener open: " + field);
                }
                if (server is PsRtpReceiver partial)
                    Check(partial.GetIngressStatistics(99) == null, "Partial listener failure skipped session cleanup");
            }
            finally { await server.StopAsync(default); server.Dispose(); }
        }
        foreach (bool flv in new[] { false, true })
        for (int round = 0; round < 3; round++)
        {
            using var log = new ListenGateLogger();
            BackgroundService server = flv ? new TcpFlvServer(log) { ListenEndPoint = new(IPAddress.Loopback, 0) }
                : new RtspServer(log) { ListenEndPoint = new(IPAddress.Loopback, 0) };
            Task start = Task.Run(() => server.StartAsync(default));
            await log.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var listener = (Socket)server.GetType().GetField("_listener", Hidden)!.GetValue(server)!;
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;
            try
            {
                for (int i = 0; i < 24; i++)
                {
                    using var client = new TcpClient(); await client.ConnectAsync(endpoint);
                    client.Client.Shutdown(SocketShutdown.Both);
                }
                log.Release.Set(); await start;
                await Task.Delay(100);
                var tasks = (IDictionary)server.GetType().GetField("_clientTasks", Hidden)!.GetValue(server)!;
                await Until(() => tasks.Count == 0);
                // 保持对端存活，直到接收器实际等待网络输入，再让异步完成与下一批连接接入竞争。
                var asynchronousPeers = new List<TcpClient>();
                try
                {
                    for (int i = 0; i < 24; i++)
                    {
                        var client = new TcpClient(); asynchronousPeers.Add(client);
                        await client.ConnectAsync(endpoint);
                    }
                    await Until(() => tasks.Count == 24);
                    Parallel.ForEach(asynchronousPeers, client => client.Dispose());
                    await Until(() => tasks.Count == 0);
                }
                finally { foreach (var client in asynchronousPeers) client.Dispose(); }
                using var pendingPeer = new TcpClient();
                await pendingPeer.ConnectAsync(endpoint);
                await Until(() => tasks.Count == 1);
                await server.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
                Check(tasks.Count == 0, "Stop returned before its tracked client completed cleanup");
                Check(listener.SafeHandle.IsClosed, "Stopped server left listener open");
                using var replacement = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                replacement.Bind(endpoint); replacement.Listen(1);
            }
            finally
            {
                log.Release.Set(); listener.Dispose(); await server.StopAsync(default); server.Dispose();
            }
        }
    }

    /// <summary>构造测试 PS/RTP 数据并直接送入 PS/RTP 接收器。</summary>
    /// <param name="server">服务端实例。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="sequence">RTP 包序列号。</param>
    /// <param name="key">是否构造关键视频帧。</param>
    internal static void Feed(PsRtpReceiver server, uint ssrc, ushort sequence, bool key)
    {
        byte[] ps = VideoRegressionTests.CreatePsPacket([0, 0, 0, 1, (byte)(key ? 0x65 : 0x41), 0x88, 0x84], 0x1B);
        server.ProcessRtpPacket(VideoRegressionTests.CreateRtpPacket(ps, ssrc, sequence, true), new(IPAddress.Loopback, 6010));
    }
    /// <summary>通过反射定位测试需要观察的内部成员。</summary>
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    /// <summary>创建异步运行延续的测试同步信号。</summary>
    /// <returns>延续异步执行的完成信号，可由测试控制完成时机。</returns>
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>验证回归条件，不满足时抛出带错误说明的异常。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>结束时间（HH:mm:ss）。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Expected media lifecycle state was not reached");
    }
    /// <summary>通过监听日志提供启动同步信号的测试日志器。</summary>
    sealed class ListenGateLogger : ILogger, IDisposable
    {
        /// <summary>监听启动日志已到达时触发的同步信号。</summary>
        public readonly TaskCompletionSource Entered = NewSignal();
        /// <summary>释放引用。引用计数归零时归还缓冲区到池。</summary>
        public readonly ManualResetEventSlim Release = new(false);
        /// <summary>返回测试日志器的作用域；该替身不创建实际作用域。</summary>
        /// <param name="state">当前处理状态。</param>
        /// <typeparam name="TState">日志状态的类型。</typeparam>
        /// <returns>该测试日志器不创建作用域，返回 null。</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>声明测试日志器是否接收指定级别的日志。</summary>
        /// <param name="level">日志或媒体级别。</param>
        /// <returns>声明测试日志器是否接收指定级别的日志是否成立。</returns>
        public bool IsEnabled(LogLevel level) => true;
        /// <summary>接收日志以触发当前测试需要的异常或同步信号。</summary>
        /// <param name="level">日志或媒体级别。</param>
        /// <param name="id">资源或会话标识。</param>
        /// <param name="state">当前处理状态。</param>
        /// <param name="ex">日志关联的异常。</param>
        /// <param name="formatter">将日志状态与异常格式化为文本的委托。</param>
        /// <typeparam name="TState">日志状态的类型。</typeparam>
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, ex).Contains("服务监听")) { Entered.TrySetResult(); if (!Release.Wait(5000)) throw new TimeoutException(); }
        }
        /// <summary>释放当前实例拥有的资源。</summary>
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}

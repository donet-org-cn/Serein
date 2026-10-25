using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serein.Media.Protocol;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Serein.Media.Server;

/// <summary>
/// 接收 RTP 承载的 MPEG 节目流并输出统一媒体帧。
/// <para>
/// 通过 UDP 或 TCP 接收设备推送的标准 RTP 媒体流（PS 封装），
/// 自动解包 RTP → PS → ES，抛出事件供上层消费。
/// </para>
/// <para>
/// 典型使用流程：
/// <list type="number">
/// <item>绑定应用的流标识、通道和 SSRC。</item>
/// <item>上游媒体源向监听端口发送 RTP/PS 数据。</item>
/// <item><see cref="FrameReceivedAsync"/> 事件触发，返回解析后的 <see cref="PooledMediaFrame"/>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PsRtpReceiver : BackgroundService, IAsyncDisposable
{
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>未知 SSRC 通知缓存的容量硬上限。</summary>
    private const int MaxUnknownSsrcEntries = 4096;
    /// <summary>同一未知 SSRC 的最小通知间隔，单位为毫秒。</summary>
    private const long UnknownSsrcEventIntervalMs = 5000;
    /// <summary>未知 SSRC 通知记录的保留时间，单位为毫秒。</summary>
    private const long UnknownSsrcExpiryMs = 60_000;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>当前 UDP 媒体套接字。</summary>
    private Socket? _udpSocket;
    /// <summary>当前 TCP 网络套接字。</summary>
    private Socket? _tcpSocket;
    /// <summary>按会话标识保存的活动会话集合。</summary>
    private readonly ConcurrentDictionary<uint, PsRtpStreamSession> _sessions = new();
    /// <summary>保护媒体会话注册与退役的互斥锁。</summary>
    private readonly object _sessionLifecycleSync = new();
    /// <summary>已注销但仍需等待帧回调完成的会话。</summary>
    private readonly Dictionary<uint, PsRtpStreamSession> _retiredFrameSessions = new();
    // 单个唤醒标记即可：仅当存在待补齐的乱序包时启动期限检查。
    /// <summary>唤醒重排缓存到期检查的信号。</summary>
    private readonly System.Threading.Channels.Channel<byte> _reorderWake = System.Threading.Channels.Channel.CreateBounded<byte>(
        new System.Threading.Channels.BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite
        });

    // 每个 SSRC 对应的 PS 分片缓冲
    /// <summary>已经输出诊断信息的媒体帧数量。</summary>
    private int _diagnosticFrameCount;

    // 未知 SSRC 频率限制：每个 SSRC 最多每 5 秒触发一次
    /// <summary>各未知 SSRC 最近通知时刻的索引。</summary>
    private readonly ConcurrentDictionary<uint, long> _unknownSsrcLastTick = new();
    /// <summary>保护未知 SSRC 限频记录的互斥锁。</summary>
    private readonly object _unknownSsrcSync = new();
    /// <summary>最近一次清理未知 SSRC 记录的单调时刻。</summary>
    private long _lastUnknownSsrcCleanupTick;

    /// <summary>监听端口。</summary>
    public IPEndPoint ListenEndPoint { get; set; } = new(IPAddress.Any, 6000);

    /// <summary>接收缓冲区大小。</summary>
    public int ReceiveBufferSize { get; set; } = 65536;

    /// <summary>每个 SSRC 的 PS 重组缓冲区最大大小。</summary>
    public int MaxPsBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>每个 SSRC 的媒体回调队列容量，最小 16 帧，默认 32。溢出时丢弃旧 GOP 并等待关键帧。</summary>
    public int FrameDispatchQueueCapacity { get; set; } = 32;

    /// <summary>是否启用 UDP RTP 监听。默认 true。</summary>
    public bool EnableUdp { get; set; } = true;

    /// <summary>是否启用 TCP RTP 监听。默认 true。</summary>
    public bool EnableTcp { get; set; } = true;

    /// <summary>TCP 监听 backlog。默认 128。</summary>
    public int TcpBacklog { get; set; } = 128;

    /// <summary>
    /// 收到完整媒体帧时触发。
    /// 参数: (sender, StreamId, ChannelId, Frame)
    /// </summary>
    private readonly object _frameHandlerLock = new();
    /// <summary>媒体帧事件的组合委托。</summary>
    private Func<PsRtpReceiver, string, string, PooledMediaFrame, ValueTask>? _frameReceivedAsync;
    /// <summary>用于分发的媒体帧订阅者快照。</summary>
    private Delegate[] _frameHandlers = Array.Empty<Delegate>();

    /// <summary>
    /// 在 FrameReceivedAsync 时发生。
    /// 每个 SSRC 顺序分派；回调期间借用帧，完成后由服务端归还引用。
    /// 需要在回调外使用时须自行 TryRetain/Release。注销清除排队帧，不提前释放在途回调的帧。
    /// </summary>
    public event Func<PsRtpReceiver, string, string, PooledMediaFrame, ValueTask>? FrameReceivedAsync
    {
        add
        {
            lock (_frameHandlerLock)
            {
                _frameReceivedAsync += value;
                Volatile.Write(ref _frameHandlers,
                    _frameReceivedAsync?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
        remove
        {
            lock (_frameHandlerLock)
            {
                _frameReceivedAsync -= value;
                Volatile.Write(ref _frameHandlers,
                    _frameReceivedAsync?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
    }

    /// <summary>流连接成功（首次收到 RTP 数据包时触发）。</summary>
    public event Func<PsRtpReceiver, string, string, ValueTask>? StreamConnectedAsync;

    /// <summary>流断开（超时或会话注销时触发）。</summary>
    public event Func<PsRtpReceiver, string, string, string, ValueTask>? StreamDisconnectedAsync;

    /// <summary>检测到丢包（序列号间隙）时触发。</summary>
    public event Func<PsRtpReceiver, string, string, int, ValueTask>? PacketLossDetectedAsync;

    /// <summary>收到未知 SSRC 的 RTP 包（未注册会话）时触发。</summary>
    public event Func<PsRtpReceiver, uint, IPEndPoint, ValueTask>? UnknownSsrcReceivedAsync;

    /// <summary>UDP 序号空缺等待时间，0 禁用；最大 500ms，仅对乱序包增加等待。</summary>
    public int ReorderDelayMilliseconds { get; set; } = 100;

    /// <summary>读取已登记 SSRC 的实际 RTP 入站统计；未登记返回 null。</summary>
    /// <param name="ssrc">注册的同步源编号。</param>
    /// <returns>不可变统计快照，不向外暴露内部队列。</returns>
    public PsRtpIngressStatistics? GetIngressStatistics(uint ssrc)
    {
        if (!_sessions.TryGetValue(ssrc, out var session)) return null;
        lock (session.ReceiveSync)
            return session.IsDisposed ? null : session.Ingress.Snapshot(session.Stats.PacketsLost);
    }

    /// <summary>
    /// 初始化 PsRtpReceiver 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public PsRtpReceiver(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PsRtpReceiver>.Instance;
    }

    /// <summary>启动接收器并等待配置的监听端口完成绑定。</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        try { await _listening.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch when (_listening.Task.IsFaulted)
        {
            // Report startup failure only after the worker has rolled back every listener and session.
            if (ExecuteTask is { } worker)
            {
                try { await worker.ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    /// <inheritdoc />
    /// <param name="stoppingToken">用于通知后台服务停止的令牌。</param>
    /// <summary>运行PsRtpReceiver的后台处理循环，直到宿主请求停止。</summary>
    /// <returns>运行PsRtpReceiver的后台处理循环，直到宿主请求停止完成时结束的异步任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiveTasks = new List<Task>(2);
        using var receiveLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            if (EnableUdp)
            {
                _udpSocket = new Socket(ListenEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // 增大 UDP 接收缓冲区，降低高码率场景下内核队列溢出的概率。
                try { _udpSocket.ReceiveBufferSize = 1024 * 1024; } catch { }
                _udpSocket.Bind(ListenEndPoint);
                _logger.LogInformation("PS/RTP RTP 服务监听 UDP {EndPoint}", ListenEndPoint);
                receiveTasks.Add(ReceiveUdpLoopAsync(receiveLifetime.Token));
                receiveTasks.Add(FlushReorderLoopAsync(receiveLifetime.Token));
            }
            if (EnableTcp)
            {
                _tcpSocket = new Socket(ListenEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                _tcpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _tcpSocket.Bind(ListenEndPoint);
                _tcpSocket.Listen(TcpBacklog);
                _logger.LogInformation("PS/RTP RTP 服务监听 TCP {EndPoint}", ListenEndPoint);
                receiveTasks.Add(AcceptTcpLoopAsync(receiveLifetime.Token));
            }
            if (receiveTasks.Count == 0)
            {
                _logger.LogWarning("PS/RTP RTP 服务未启用 UDP/TCP 监听");
                _listening.TrySetResult();
                return;
            }
            _listening.TrySetResult();
            await Task.WhenAll(receiveTasks).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _listening.TrySetException(error);
            throw;
        }
        finally
        {
            // 后续监听器可能在 UDP 及重排循环已经启动后失败；退出启动失败清理前，先取消并观察这些兄弟任务。
            try { receiveLifetime.Cancel(); } catch { }
            _udpSocket?.Dispose();
            _tcpSocket?.Dispose();
            try { await Task.WhenAll(receiveTasks).ConfigureAwait(false); } catch { }
            RetireAllSessions();
            _logger.LogInformation("PS/RTP RTP 服务已停止");
        }
    }

    /// <summary>
    /// 将应用的流标识绑定到 RTP 同步源。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道 ID。</param>
    /// <param name="ssrc">上游使用的 RTP 同步源标识。</param>
    /// <exception cref="InvalidOperationException">同一 SSRC 的上一代帧回调尚未完成。</exception>
    public void RegisterStream(string streamId, string channelId, uint ssrc)
    {
        var session = new PsRtpStreamSession(streamId, channelId, ssrc, MaxPsBufferSize, FrameDispatchQueueCapacity);
        PsRtpStreamSession? previous;
        lock (_sessionLifecycleSync)
        {
            if (_retiredFrameSessions.TryGetValue(ssrc, out var retired))
            {
                lock (retired.FrameDispatchSync)
                    if (retired.FrameCallbackActive) throw PendingFrameCallback(ssrc);
                _retiredFrameSessions.Remove(ssrc);
            }
            _sessions.TryGetValue(ssrc, out previous);
            if (previous != null)
            {
                lock (previous.FrameDispatchSync)
                {
                    if (previous.FrameCallbackActive) throw PendingFrameCallback(ssrc);
                    StopFrameDispatch(previous);
                }
            }
            _sessions[ssrc] = session;
        }
        // 解析锁内可能触发同步生命周期事件，而事件可能注册或注销流。因此持有注册表锁时绝不获取解析锁。
        if (previous != null) DisposeSession(previous);
        _unknownSsrcLastTick.TryRemove(ssrc, out _);
        _logger.LogInformation("注册 PS/RTP 会话: {StreamId}/{Channel}, SSRC={Ssrc}", streamId, channelId, ssrc);
    }

    /// <summary>
    /// 注销一个媒体流会话。
    /// </summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    public void UnregisterStream(uint ssrc)
    {
        if (TryRetireSession(ssrc, out var session))
        {
            var wasConnected = DisposeSession(session);
            if (wasConnected)
                DispatchStreamDisconnected(session, "session unregistered");
        }
    }

    /// <summary>构造仍有媒体帧回调在途时的会话复用异常。</summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <returns>构造仍有媒体帧回调在途时的会话复用异常得到的InvalidOperationException对象。</returns>
    private static InvalidOperationException PendingFrameCallback(uint ssrc) =>
        new($"PS/RTP SSRC {ssrc} still has an in-flight frame callback from the previous session; complete that callback before registering again.");

    /// <summary>注销指定同步源的会话，并保留在途回调需要的资源。</summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="session">当前会话。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private bool TryRetireSession(uint ssrc, out PsRtpStreamSession session)
    {
        lock (_sessionLifecycleSync)
        {
            if (!_sessions.TryRemove(ssrc, out session!)) return false;
            lock (session.FrameDispatchSync)
            {
                StopFrameDispatch(session);
                if (session.FrameCallbackActive) _retiredFrameSessions[ssrc] = session;
            }
            return true;
        }
    }

    /// <summary>将全部活动会话移入停止与回收流程。</summary>
    private void RetireAllSessions()
    {
        foreach (uint ssrc in _sessions.Keys)
            if (TryRetireSession(ssrc, out var session)) DisposeSession(session);
    }

    // 调用方持有 FrameDispatchSync；清理队列时不调用用户代码。
    /// <summary>停止会话接收新帧并释放尚未分发的排队帧。</summary>
    /// <param name="session">当前会话。</param>
    private static void StopFrameDispatch(PsRtpStreamSession session)
    {
        session.FrameDispatchStopped = true;
        session.FrameQueue.DisposeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 接收UDPLoop，并异步完成操作。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>接收UDPLoop，并异步完成操作完成时结束的异步任务。</returns>
    private async Task ReceiveUdpLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var remoteEp = (EndPoint)new IPEndPoint(
            ListenEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesReceived;
                IPEndPoint actualRemoteEP;
#if NET7_0_OR_GREATER
                var result = await _udpSocket!.ReceiveFromAsync(
                    buffer.AsMemory(), SocketFlags.None, remoteEp, cancellationToken).ConfigureAwait(false);
                bytesReceived = result.ReceivedBytes;
                actualRemoteEP = (IPEndPoint)result.RemoteEndPoint;
#else
                bytesReceived = await Task.Run(() =>
                    _udpSocket!.ReceiveFrom(buffer, ref remoteEp), cancellationToken).ConfigureAwait(false);
                actualRemoteEP = (IPEndPoint)remoteEp;
#endif

                if (bytesReceived > 0)
                {
                    ProcessRtpPacket(buffer.AsSpan(0, bytesReceived), actualRemoteEP);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PS/RTP RTP UDP 接收异常");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>持续接受 TCP 媒体连接并启动接收处理。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>持续接受 TCP 媒体连接并启动接收处理完成时结束的异步任务。</returns>
    private async Task AcceptTcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _tcpSocket!.AcceptAsync(cancellationToken).ConfigureAwait(false);
                try { client.NoDelay = true; } catch { }
                _ = ReceiveTcpClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PS/RTP RTP TCP 接入异常");
        }
    }

    /// <summary>
    /// 接收TCP客户端，并异步完成操作。
    /// </summary>
    /// <param name="client">客户端连接。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>接收TCP客户端，并异步完成操作完成时结束的异步任务。</returns>
    private async Task ReceiveTcpClientAsync(Socket client, CancellationToken cancellationToken)
    {
        var remoteEP = client.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(ReceiveBufferSize, ushort.MaxValue + 2));
        var buffered = 0;

        try
        {
            _logger.LogDebug("PS/RTP RTP TCP 客户端接入: {RemoteEndPoint}", remoteEP);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (buffered == buffer.Length)
                {
                    _logger.LogWarning("PS/RTP RTP TCP 缓冲区已满，丢弃异常连接: {RemoteEndPoint}", remoteEP);
                    break;
                }

                var bytesReceived = await client.ReceiveAsync(
                    buffer.AsMemory(buffered, buffer.Length - buffered),
                    SocketFlags.None,
                    cancellationToken).ConfigureAwait(false);

                if (bytesReceived == 0)
                    break;

                buffered += bytesReceived;
                var offset = 0;

                while (buffered - offset >= 2)
                {
                    var packetLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                    if (packetLength == 0)
                    {
                        offset += 2;
                        continue;
                    }

                    if (buffered - offset < packetLength + 2)
                        break;

                    ProcessIngressPacket(buffer.AsSpan(offset + 2, packetLength), remoteEP, false);
                    offset += packetLength + 2;
                }

                if (offset > 0)
                {
                    buffered -= offset;
                    if (buffered > 0)
                        Buffer.BlockCopy(buffer, offset, buffer, 0, buffered);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PS/RTP RTP TCP 接收异常: {RemoteEndPoint}", remoteEP);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { client.Dispose(); } catch { }
            _logger.LogDebug("PS/RTP RTP TCP 客户端断开: {RemoteEndPoint}", remoteEP);
        }
    }

    /// <summary>
    /// 处理RTP数据包。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="remoteEP">远端网络端点。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ProcessRtpPacket(ReadOnlySpan<byte> data, IPEndPoint remoteEP)
        => ProcessIngressPacket(data, remoteEP, true);

    /// <summary>根据会话配置接收 RTP 包并执行可选的乱序重排。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="remoteEP">远端网络端点。</param>
    /// <param name="reorder">是否启用 RTP 包重排。</param>
    private void ProcessIngressPacket(ReadOnlySpan<byte> data, IPEndPoint remoteEP, bool reorder)
    {
        if (data.Length < StandardRtpHeader.HeaderLength) return;
        var header = StandardRtpHeader.Parse(data);
        if (header.Version != 2 || !header.TryGetPayload(data, out _)) return;
        if (!_sessions.TryGetValue(header.Ssrc, out var session))
        {
            ProcessRtpPacketCore(data, remoteEP);
            return;
        }
        lock (session.ReceiveSync)
        {
            if (session.IsDisposed) return;
            var ingress = session.Ingress;
            long now = Environment.TickCount64;
            ingress.Received(data.Length, remoteEP, now);
            ushort seq = header.SequenceNumber;
            if (!reorder || ReorderDelayMilliseconds <= 0 || ingress.Next is null)
            {
                ingress.Next = unchecked((ushort)(seq + 1));
                ProcessRtpPacketCore(data, remoteEP);
                return;
            }
            int delta = unchecked((ushort)(seq - ingress.Next.Value));
            if (delta == 0)
            {
                ProcessRtpPacketCore(data, remoteEP);
                ingress.Next = unchecked((ushort)(seq + 1));
                DrainReorder(session, false);
            }
            else if (delta < 3000)
            {
                if (ingress.Pending.ContainsKey(seq))
                {
                    ingress.LatePackets++;
                    return;
                }
                // 复制数据前同时检查包数量与字节容量配额。
                while (ingress.Pending.Count >= 128 || ingress.PendingBytes + data.Length > 1024 * 1024)
                    DrainReorder(session, true);
                // 容量清理可能已推进到当前包之后，不能让队列序列游标倒退。
                if (unchecked((ushort)(seq - ingress.Next.Value)) >= 32768)
                {
                    ingress.LatePackets++;
                    return;
                }
                ingress.Pending.Add(seq, new(data.ToArray(), remoteEP, now));
                ingress.PendingBytes += data.Length;
                ingress.BufferedPackets++;
                DrainReorder(session, false);
                if (ingress.Pending.Count > 0) _reorderWake.Writer.TryWrite(0);
            }
            else if (delta > 65536 - 100)
            {
                ingress.LatePackets++;
            }
            else
            {
                // 保留既有的连续两包编码器重启确认规则。
                ingress.Clear();
                ingress.Next = unchecked((ushort)(seq + 1));
                ProcessRtpPacketCore(data, remoteEP);
            }
        }
    }

    /// <summary>按序排出重排缓存中的 RTP 包。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="force">是否强制排出尚未连续的缓存包。</param>
    private void DrainReorder(PsRtpStreamSession session, bool force)
    {
        var ingress = session.Ingress;
        while (ingress.Pending.Count > 0 && ingress.Next is { } next)
        {
            if (ingress.Pending.Remove(next, out var packet))
            {
                ingress.PendingBytes -= packet.Data.Length;
                ingress.Next = unchecked((ushort)(next + 1));
                ingress.RecoveredPackets++;
                ProcessRtpPacketCore(packet.Data, packet.Remote);
                continue;
            }
            long now = Environment.TickCount64;
            if (!force && ingress.Pending.Values.All(p => now - p.Tick < Math.Clamp(ReorderDelayMilliseconds, 1, 500))) return;
            ushort closest = ingress.Pending.Keys.MinBy(seq => unchecked((ushort)(seq - next)));
            ingress.Next = closest;
            force = false;
        }
    }

    /// <summary>有乱序积压时检查超时；没有积压时等待信号，不周期唤醒线程池。</summary>
    /// <param name="cancellationToken">停止服务时取消等待。</param>
    /// <returns>有乱序积压时检查超时；没有积压时等待信号，不周期唤醒线程池完成时结束的异步任务。</returns>
    private async Task FlushReorderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _reorderWake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_reorderWake.Reader.TryRead(out _)) { }
                bool pending;
                do
                {
                    pending = false;
                    foreach (var session in _sessions.Values)
                    {
                        lock (session.ReceiveSync)
                        {
                            if (session.IsDisposed || session.Ingress.Pending.Count == 0) continue;
                            DrainReorder(session, false);
                            pending |= session.Ingress.Pending.Count > 0;
                        }
                    }
                    if (pending)
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                } while (pending);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>解析 RTP 包并将负载交给对应媒体解包器。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="remoteEP">远端网络端点。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessRtpPacketCore(ReadOnlySpan<byte> data, IPEndPoint remoteEP)
    {
        if (data.Length < StandardRtpHeader.HeaderLength) return;

        var header = StandardRtpHeader.Parse(data);

        // 版本号必须是 2
        if (header.Version != 2) return;

        if (!header.TryGetPayload(data, out var payload)) return;

        // 根据 SSRC 查找会话
        if (!_sessions.TryGetValue(header.Ssrc, out var session))
        {
            // 频率限制：同一个 SSRC 最多每 5 秒触发一次未知 SSRC 事件
            if (ShouldDispatchUnknownSsrc(header.Ssrc, Environment.TickCount64))
                DispatchUnknownSsrc(header.Ssrc, remoteEP);
            return;
        }

        int lostToDispatch = 0;
        bool connectedToDispatch = false;
        PooledMediaFrame[]? framesToDispatch = null;
        int frameCountToDispatch = 0;

        lock (session.SyncRoot)
        {
            if (session.IsDisposed)
                return;

            var reassembler = session.Reassembler;

            // 序列号跟踪 & 丢包检测
            int lost = session.TrackSequenceNumber(header.SequenceNumber);
            if (lost < 0)
                return; // 重复包与迟到包不得追加到 PS 重组缓存。

            if (lost > 0)
            {
                reassembler.DiscardUntilNextPackStart();
                _ = reassembler.ConsumeDiscontinuity();
                session.WaitingForKeyFrame = true;
                lostToDispatch = lost;

                long now = Environment.TickCount64;
                if (session.LastLossWarningTick == 0 || now - session.LastLossWarningTick >= 5000)
                {
                    session.LastLossWarningTick = now;
                    _logger.LogWarning("PS/RTP 丢包: {Device}/{Channel}, 丢失 {Lost} 包, Seq={Seq}",
                        session.StreamId, session.ChannelId, lost, header.SequenceNumber);
                }
            }

            session.Stats.BytesReceived += payload.Length;

            // 首次收到数据 → 连接事件。只在锁内更新状态，外部处理器在锁外调用。
            if (!session.IsConnected)
            {
                session.IsConnected = true;
                session.ConnectedTime = DateTimeOffset.UtcNow;
                connectedToDispatch = true;
                _logger.LogInformation("PS/RTP 流连接: {Device}/{Channel}, SSRC={Ssrc}",
                    session.StreamId, session.ChannelId, header.Ssrc);
                var firstBytes = payload.Length > 64 ? payload[..64] : payload;
                _logger.LogDebug("[诊断] 首包 Payload({Len}B): {Hex}",
                    payload.Length, Convert.ToHexString(firstBytes));
            }

            // PS/RTP: Marker=1 表示 PS 包结束（一个完整的 PS 包可能跨多个 RTP）
            bool isComplete = header.Marker;
            var psFrames = reassembler.Feed(payload, isComplete);
            bool feedDiscontinuity = reassembler.ConsumeDiscontinuity();
            if (feedDiscontinuity)
                session.WaitingForKeyFrame = true;

            if (psFrames == null)
            {
                if (session.Stats.BytesReceived < payload.Length * 20)
                {
                    _logger.LogDebug("[诊断] 重组未完成: Marker={Marker}, Seq={Seq}, 累计={Bytes}B",
                        isComplete, header.SequenceNumber, session.Stats.BytesReceived);
                }
            }
            else if (psFrames.Count == 0)
            {
                _logger.LogWarning("[诊断] PS 包解析结果为空: SSRC={Ssrc}, PS 长度={Len}B",
                    header.Ssrc, reassembler.CurrentBufferLength);
            }
            else
            {
                int videoCount = 0, audioCount = 0;
                foreach (var pesFrame in psFrames)
                {
                    if (pesFrame.Kind == MediaFrameKind.Video) videoCount++;
                    else audioCount++;
                }
                if (videoCount > 0 || _diagnosticFrameCount < 5)
                {
                    _diagnosticFrameCount++;
                    if (_diagnosticFrameCount <= 10)
                        _logger.LogInformation("[诊断RTP] PS解析出 {Video}个视频帧 + {Audio}个音频帧, PS数据={PSLen}B",
                            videoCount, audioCount, psFrames[0].Length);
                }

                foreach (var pesFrame in psFrames)
                {
                    try
                    {
                        long timestampMs = pesFrame.Timestamp90kHz > 0
                            ? pesFrame.Timestamp90kHz / 90
                            : Environment.TickCount64;

                        bool isKeyFrame = pesFrame.Kind == MediaFrameKind.Video
                            && IsKeyFrame(pesFrame.GetSpan(), pesFrame.Codec);

                        // 本次输入后段发生溢出或重置时，不得继续对外发布此前解析出的旧视频帧。
                        if (feedDiscontinuity && pesFrame.Kind == MediaFrameKind.Video)
                            continue;

                        if (pesFrame.Kind == MediaFrameKind.Video && session.WaitingForKeyFrame)
                        {
                            if (!isKeyFrame)
                            {
                                if (!VideoNalUnitHelper.IsParameterSetOnly(pesFrame.GetSpan(), pesFrame.Codec))
                                    continue;
                            }
                            else
                            {
                                session.WaitingForKeyFrame = false;
                            }
                        }

                        if (!pesFrame.TryTakeOwnership(out var frameBuffer, out var frameLength))
                            continue;

                        framesToDispatch ??= ArrayPool<PooledMediaFrame>.Shared.Rent(psFrames.Count);
                        framesToDispatch[frameCountToDispatch++] = PooledMediaFrame.TakeOwnership(
                            frameBuffer,
                            frameLength,
                            pesFrame.Kind,
                            pesFrame.Codec,
                            isKeyFrame,
                            timestampMs);
                    }
                    finally
                    {
                        pesFrame.Dispose();
                    }
                }
            }
        }

        // 持有会话解析锁时不得调用用户代码；订阅者可能同步注销该会话。
        if (IsSessionDisposed(session))
        {
            ReleaseAndReturnFrames(framesToDispatch, frameCountToDispatch);
            return;
        }

        if (lostToDispatch > 0)
        {
            DispatchPacketLoss(session, lostToDispatch);
            if (IsSessionDisposed(session))
            {
                ReleaseAndReturnFrames(framesToDispatch, frameCountToDispatch);
                return;
            }
        }

        if (connectedToDispatch)
        {
            DispatchStreamConnected(session);
            if (IsSessionDisposed(session))
            {
                ReleaseAndReturnFrames(framesToDispatch, frameCountToDispatch);
                return;
            }
        }

        if (framesToDispatch == null)
            return;

        try
        {
            for (int i = 0; i < frameCountToDispatch; i++)
            {
                if (IsSessionDisposed(session))
                {
                    ReleaseFrameRange(framesToDispatch, i, frameCountToDispatch);
                    break;
                }

                DispatchFrame(session, framesToDispatch[i]);
            }
        }
        finally
        {
            ArrayPool<PooledMediaFrame>.Shared.Return(framesToDispatch, clearArray: true);
        }
    }

    /// <summary>
    /// 判断是否会话释放状态。
    /// </summary>
    /// <param name="session">当前会话。</param>
    /// <returns>是否会话释放状态的判定结果。</returns>
    private static bool IsSessionDisposed(PsRtpStreamSession session)
    {
        lock (session.SyncRoot)
            return session.IsDisposed;
    }

    /// <summary>归还一批媒体帧的引用并清空帧集合。</summary>
    /// <param name="frames">媒体帧集合。</param>
    /// <param name="count">数据长度或数量。</param>
    private static void ReleaseAndReturnFrames(PooledMediaFrame[]? frames, int count)
    {
        if (frames == null)
            return;

        ReleaseFrameRange(frames, 0, count);
        ArrayPool<PooledMediaFrame>.Shared.Return(frames, clearArray: true);
    }

    /// <summary>归还指定范围中媒体帧的引用。</summary>
    /// <param name="frames">媒体帧集合。</param>
    /// <param name="startIndex">起始索引。</param>
    /// <param name="count">数据长度或数量。</param>
    private static void ReleaseFrameRange(PooledMediaFrame[] frames, int startIndex, int count)
    {
        for (int i = startIndex; i < count; i++)
            frames[i].Release();
    }

    /// <summary>将完整媒体帧交给会话的串行分发队列。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="frame">媒体帧。</param>
    private void DispatchFrame(PsRtpStreamSession session, PooledMediaFrame frame)
    {
        // 此处解析器仍持有 ReceiveSync。每条流启动一个异步消费者，使外部回调的同步部分也不会占用该锁。
        session.FrameDispatchTask ??= DispatchQueuedFramesAsync(session);
        if (!session.FrameQueue.TryWrite(frame)) frame.Release();
    }

    /// <summary>串行分发会话中排队的媒体帧。</summary>
    /// <param name="session">当前会话。</param>
    /// <returns>串行分发会话中排队的媒体帧完成时结束的异步任务。</returns>
    private async Task DispatchQueuedFramesAsync(PsRtpStreamSession session)
    {
        await foreach (var frame in session.FrameQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (session.FrameDispatchSync)
            {
                if (session.FrameDispatchStopped) { frame.Release(); continue; }
                session.FrameCallbackActive = true;
            }
            try { await InvokeFrameHandlersAsync(session, frame).ConfigureAwait(false); }
            finally
            {
                bool retired;
                lock (session.FrameDispatchSync)
                {
                    session.FrameCallbackActive = false;
                    retired = session.FrameDispatchStopped;
                }
                // 活动流分发每一帧时无需竞争服务器级注册表锁。
                if (retired)
                    lock (_sessionLifecycleSync)
                        if (_retiredFrameSessions.TryGetValue(session.Ssrc, out var previous) && ReferenceEquals(previous, session))
                            _retiredFrameSessions.Remove(session.Ssrc);
            }
        }
    }

    /// <summary>依次通知媒体帧订阅者并维持回调期间的帧所有权。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="frame">媒体帧。</param>
    /// <returns>依次通知媒体帧订阅者并维持回调期间的帧所有权完成时结束的异步任务。</returns>
    private async ValueTask InvokeFrameHandlersAsync(PsRtpStreamSession session, PooledMediaFrame frame)
    {
        try
        {
            List<Task>? pendingHandlers = null;
            var handlers = Volatile.Read(ref _frameHandlers);
            foreach (var item in handlers)
            {
                if (!frame.TryRetain())
                    break;

                try
                {
                    var handler = (Func<PsRtpReceiver, string, string, PooledMediaFrame, ValueTask>)item;
                    var pending = handler(this, session.StreamId, session.ChannelId, frame);
                    if (pending.IsCompletedSuccessfully)
                    {
                        pending.GetAwaiter().GetResult();
                        frame.Release();
                    }
                    else
                    {
                        (pendingHandlers ??= new List<Task>()).Add(CompleteFrameHandlerAsync(pending, frame, session));
                    }
                }
                catch (Exception ex)
                {
                    frame.Release();
                    LogFrameHandlerError(ex, session);
                }
            }
            if (pendingHandlers != null) await Task.WhenAll(pendingHandlers).ConfigureAwait(false);
        }
        finally
        {
            // 归还发布者持有的引用；各异步处理器拥有独立保留的引用，直到对应 ValueTask 完成。
            frame.Release();
        }
    }

    /// <summary>等待异步帧回调完成并归还借用引用。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <param name="frame">媒体帧。</param>
    /// <param name="session">当前会话。</param>
    /// <returns>等待异步帧回调完成并归还借用引用完成时结束的异步任务。</returns>
    private async Task CompleteFrameHandlerAsync(
        ValueTask pending,
        PooledMediaFrame frame,
        PsRtpStreamSession session)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFrameHandlerError(ex, session);
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>记录帧回调异常，同时隔离日志组件自身的错误。</summary>
    /// <param name="exception">当前处理的异常。</param>
    /// <param name="session">当前会话。</param>
    private void LogFrameHandlerError(Exception exception, PsRtpStreamSession session)
    {
        // 日志提供程序也是外部代码，不能让它终止唯一的消费者。
        try { _logger.LogError(exception, "PS/RTP frame handler failed for {Device}/{Channel}", session.StreamId, session.ChannelId); }
        catch { }
    }

    /// <summary>调度媒体流连接通知。</summary>
    /// <param name="session">当前会话。</param>
    private void DispatchStreamConnected(PsRtpStreamSession session)
    {
        var handlers = StreamConnectedAsync;
        if (handlers == null) return;
        foreach (Func<PsRtpReceiver, string, string, ValueTask> handler in handlers.GetInvocationList())
        {
            try { Observe(handler(this, session.StreamId, session.ChannelId), "stream connected"); }
            catch (Exception ex) { _logger.LogError(ex, "PS/RTP stream connected handler failed"); }
        }
    }

    /// <summary>调度媒体流断开通知。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="reason">状态变化或失败原因。</param>
    private void DispatchStreamDisconnected(PsRtpStreamSession session, string reason)
    {
        var handlers = StreamDisconnectedAsync;
        if (handlers == null) return;
        foreach (Func<PsRtpReceiver, string, string, string, ValueTask> handler in handlers.GetInvocationList())
        {
            try { Observe(handler(this, session.StreamId, session.ChannelId, reason), "stream disconnected"); }
            catch (Exception ex) { _logger.LogError(ex, "PS/RTP stream disconnected handler failed"); }
        }
    }

    /// <summary>调度媒体流丢包通知。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="lost">累计丢包数量。</param>
    private void DispatchPacketLoss(PsRtpStreamSession session, int lost)
    {
        var handlers = PacketLossDetectedAsync;
        if (handlers == null) return;
        foreach (Func<PsRtpReceiver, string, string, int, ValueTask> handler in handlers.GetInvocationList())
        {
            try { Observe(handler(this, session.StreamId, session.ChannelId, lost), "packet loss"); }
            catch (Exception ex) { _logger.LogError(ex, "PS/RTP packet loss handler failed"); }
        }
    }

    /// <summary>在容量和频率限制内决定是否通知未知同步源。</summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="now">当前时间或单调时钟计数。</param>
    /// <returns>在容量和频率限制内决定是否通知未知同步源是否成立。</returns>
    private bool ShouldDispatchUnknownSsrc(uint ssrc, long now)
    {
        lock (_unknownSsrcSync)
        {
            if (_unknownSsrcLastTick.TryGetValue(ssrc, out long lastTick))
            {
                if (now - lastTick < UnknownSsrcEventIntervalMs)
                    return false;

                _unknownSsrcLastTick[ssrc] = now;
                return true;
            }

            // 清理频率受到限制，单次扫描不超过容量硬上限，避免每个伪造 UDP 包都触发 O(N) 扫描。
            if (_unknownSsrcLastTick.Count >= MaxUnknownSsrcEntries &&
                now - _lastUnknownSsrcCleanupTick >= UnknownSsrcEventIntervalMs)
            {
                _lastUnknownSsrcCleanupTick = now;
                foreach (var item in _unknownSsrcLastTick)
                {
                    if (now - item.Value >= UnknownSsrcExpiryMs)
                        _unknownSsrcLastTick.TryRemove(item.Key, out _);
                }
            }

            if (_unknownSsrcLastTick.Count >= MaxUnknownSsrcEntries)
                return false;

            _unknownSsrcLastTick.TryAdd(ssrc, now);
            return true;
        }
    }

    /// <summary>调度未知同步源通知。</summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="remoteEndPoint">远端网络端点。</param>
    private void DispatchUnknownSsrc(uint ssrc, IPEndPoint remoteEndPoint)
    {
        var handlers = UnknownSsrcReceivedAsync;
        if (handlers == null) return;
        foreach (Func<PsRtpReceiver, uint, IPEndPoint, ValueTask> handler in handlers.GetInvocationList())
        {
            try { Observe(handler(this, ssrc, remoteEndPoint), "unknown SSRC"); }
            catch (Exception ex) { _logger.LogError(ex, "PS/RTP unknown SSRC handler failed"); }
        }
    }

    /// <summary>观察异步操作并记录异常，避免错误无人接收。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <param name="eventName">事件名称。</param>
    private void Observe(ValueTask pending, string eventName)
    {
        try
        {
            if (pending.IsCompletedSuccessfully)
                pending.GetAwaiter().GetResult();
            else
                _ = ObserveAsync(pending, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PS/RTP {EventName} handler failed", eventName);
        }
    }

    /// <summary>等待异步操作完成并隔离业务通知异常。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <param name="eventName">事件名称。</param>
    /// <returns>等待异步操作完成并隔离业务通知异常完成时结束的异步任务。</returns>
    private async Task ObserveAsync(ValueTask pending, string eventName)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PS/RTP asynchronous {EventName} handler failed", eventName);
        }
    }

    /// <summary>释放已注销会话的解析与缓存资源。</summary>
    /// <param name="session">当前会话。</param>
    /// <returns>释放已注销会话的解析与缓存资源是否成立。</returns>
    private static bool DisposeSession(PsRtpStreamSession session)
    {
        lock (session.ReceiveSync)
        lock (session.SyncRoot)
        {
            if (session.IsDisposed)
                return false;

            var wasConnected = session.IsConnected;
            session.IsDisposed = true;
            lock (session.FrameDispatchSync) StopFrameDispatch(session);
            session.Ingress.Clear();
            session.Reassembler.Reset();
            return wasConnected;
        }
    }

    /// <summary>
    /// 检查是否关键帧（I帧）。
    /// </summary>
    /// <param name="span">待处理的连续数据区域。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否关键帧（I帧）的判定结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsKeyFrame(ReadOnlySpan<byte> span, MediaCodec codec) =>
        VideoNalUnitHelper.IsRandomAccessPoint(span, codec);

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        // 关闭 socket 会使接收循环退出，ExecuteAsync 随之完成
        try { _udpSocket?.Close(); } catch { }
        try { _tcpSocket?.Close(); } catch { }
        // 等待基类 StopAsync 完成
        await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        // 同时覆盖未启动宿主但通过直接输入或注册建立的会话。
        RetireAllSessions();
    }
}

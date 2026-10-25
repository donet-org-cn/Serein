using Microsoft.Extensions.Logging;
using Serein.Media.Protocol;
using Serein.Media.Relay;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Serein.Media.Server;

/// <summary>
/// 标准 RTP 客户端。
/// <para>
/// 用于 RTSP 拉流场景：从远端接收标准 RTP 媒体流（PS 封装或裸 ES），
/// 自动解包 RTP → PS → ES，抛出事件供上层消费。
/// </para>
/// <para>
/// 典型使用流程（配合 RTSP 拉流）：
/// <list type="number">
/// <item>RTSP SETUP 获取服务端 RTP 端口。</item>
/// <item>创建此类绑定本地 UDP 端口。</item>
/// <item>远端开始发送 RTP 数据到此端口。</item>
/// <item><see cref="FrameReceivedAsync"/> 事件触发，返回解析后的 <see cref="PooledMediaFrame"/>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RtpClient : IAsyncDisposable
{
    /// <summary>识别正常序列推进的最大跳跃距离。</summary>
    private const int MaxSequenceDropout = 3000;
    /// <summary>识别迟到 RTP 包的反向序列窗口。</summary>
    private const int MaxSequenceMisorder = 100;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>当前 UDP 媒体套接字。</summary>
    private Socket? _udpSocket;
    /// <summary>当前网络接收任务。</summary>
    private Task? _receiveTask;
    /// <summary>保护帧分发状态的互斥锁。</summary>
    private readonly object _dispatchSync = new();
    /// <summary>等待分发的有界媒体帧队列。</summary>
    private MediaStreamSubscription? _pendingFrames;
    /// <summary>是否已有帧分发过程正在运行。</summary>
    private bool _dispatching;
    /// <summary>帧分发是否已经停止。</summary>
    private bool _dispatchStopped;
    /// <summary>当前帧分发过程完成时发送的信号。</summary>
    private TaskCompletionSource<bool>? _dispatchCompletion;
    /// <summary>尚未退出的输入处理数量。</summary>
    private int _activeInputs;
    /// <summary>全部输入处理结束时发送的信号。</summary>
    private TaskCompletionSource<bool>? _inputCompletion;
    /// <summary>是否已有停止操作正在执行。</summary>
    private bool _stopInProgress;
    /// <summary>用于区分启动与停止世代的版本号。</summary>
    private long _lifecycleVersion;
    /// <summary>当前断连通知任务。</summary>
    private Task? _disconnectTask;

    /// <summary>慢帧回调的队列容量，最小 16；溢出时丢弃旧 GOP 并等待关键帧。</summary>
    public int FrameDispatchQueueCapacity { get; set; } = 32;

    /// <summary>
    /// 停止时等待在途帧回调的最长时间，默认 2 秒。旧事件没有取消参数；到期后
    /// 回调仍拥有其帧引用，必须自行结束并在 finally 中 Release，库不会提前归还该内存。
    /// </summary>
    public TimeSpan FrameHandlerStopTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>本地绑定端口。</summary>
    public int LocalPort { get; private set; }

    /// <summary>接收缓冲区大小。</summary>
    public int ReceiveBufferSize { get; set; } = 65536;

    /// <summary>PS 重组缓冲区最大大小。</summary>
    public int MaxPsBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>裸 H.264/H.265 FU 分片重组后的最大帧大小。</summary>
    public int MaxRawFragmentSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>裸 H.264/H.265 按 RTP 时间戳聚合后的最大访问单元大小。</summary>
    public int MaxRawAccessUnitSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>是否使用 PS 解封装（PS/RTP / RTSP 源 标准 RTP/PS）。</summary>
    public bool UsePsDemux { get; set; } = true;

    /// <summary>裸 RTP 模式下的媒体类型。</summary>
    public MediaFrameKind RawFrameKind { get; set; } = MediaFrameKind.Video;

    /// <summary>裸 RTP 模式下的媒体编码。</summary>
    public MediaCodec RawCodec { get; set; } = MediaCodec.H264;

    /// <summary>裸 RTP 模式下的 RTP 时钟频率。</summary>
    public int RawClockRate { get; set; } = 90000;

    /// <summary>收到完整媒体帧时触发。参数: (sender, frame)</summary>
    private readonly object _frameHandlerLock = new();
    /// <summary>媒体帧事件的组合委托。</summary>
    private Func<RtpClient, PooledMediaFrame, ValueTask>? _frameReceivedAsync;
    /// <summary>用于分发的媒体帧订阅者快照。</summary>
    private Delegate[] _frameHandlers = Array.Empty<Delegate>();

    /// <summary>
    /// 每个订阅者拥有一个帧引用，必须在 finally 中 Release。每流只允许一批回调在途，后续帧有界排队。
    /// </summary>
    public event Func<RtpClient, PooledMediaFrame, ValueTask>? FrameReceivedAsync
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
    public event Func<RtpClient, IPEndPoint, ValueTask>? StreamConnectedAsync;

    /// <summary>流断开。</summary>
    public event Func<RtpClient, string, ValueTask>? StreamDisconnectedAsync;

    /// <summary>检测到丢包。</summary>
    public event Func<RtpClient, int, ValueTask>? PacketLossDetectedAsync;

    /// <summary>RTP 统计。</summary>
    public RtpStreamStats Stats { get; } = new();

    // 序列号跟踪
    /// <summary>最近确认的 RTP 序列号。</summary>
    private ushort _lastSeqNum;
    /// <summary>RTP 序列号跟踪是否已初始化。</summary>
    private bool _seqInitialized;
    /// <summary>编码器重启候选后期望的下一序列号。</summary>
    private ushort _restartCandidateNextSeq;
    /// <summary>是否已有等待后续包确认的重启候选。</summary>
    private bool _hasRestartCandidate;

    // PS 重组器（当 UsePsDemux=true 时使用）
    /// <summary>把 RTP 负载合并为 PS 媒体帧的重组器。</summary>
    private PsReassembler? _psReassembler;

    // 裸 H.264/H.265 FU 分片重组缓冲。RTP 接收热路径复用池化数组，避免长时间运行产生 GC 压力。
    /// <summary>裸 RTP 分片重组缓冲区。</summary>
    private byte[]? _rawFragmentBuffer;
    /// <summary>裸 RTP 分片缓冲区的有效字节数。</summary>
    private int _rawFragmentLength;
    /// <summary>当前裸 RTP 分片组的时间戳。</summary>
    private uint _rawFragmentTimestamp;
    /// <summary>是否正在重组一个裸 RTP 分片组。</summary>
    private bool _rawFragmentActive;
    /// <summary>裸 RTP 视频访问单元的拼接缓冲区。</summary>
    private byte[]? _rawAccessUnitBuffer;
    /// <summary>裸 RTP 访问单元的有效字节数。</summary>
    private int _rawAccessUnitLength;
    /// <summary>当前裸 RTP 访问单元的时间戳。</summary>
    private uint _rawAccessUnitTimestamp;
    /// <summary>是否正在积累裸 RTP 访问单元。</summary>
    private bool _rawAccessUnitActive;
    /// <summary>按已验证的 NAL 边界累计随机接入标记，避免发布时再次扫描整帧。</summary>
    private bool _rawAccessUnitIsKeyFrame;
    /// <summary>是否等待丢包后的恢复关键帧。</summary>
    private bool _waitingForKeyFrame = true;

    // 连接状态
    /// <summary>当前媒体接收器是否已建立连接。</summary>
    private bool _isConnected;
    /// <summary>当前媒体连接的建立时间。</summary>
    private DateTimeOffset _connectedTime;
    /// <summary>远端网络端点。</summary>
    private IPEndPoint? _remoteEndPoint;

    /// <summary>
    /// 初始化 RtpClient 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public RtpClient(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RtpClient>.Instance;
    }

    /// <summary>
    /// 启动 RTP 接收，自动分配本地 UDP 端口。
    /// </summary>
    /// <param name="localPort">本地 RTP 接收端口。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动 RTP 接收，自动分配本地 UDP 端口完成时结束的异步任务。</returns>
    public Task StartAsync(int localPort = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchSync)
        {
            if (_dispatching || _activeInputs != 0 || _stopInProgress || _receiveTask is { IsCompleted: false } || _disconnectTask is { IsCompleted: false })
                throw new InvalidOperationException("The previous RTP receiver or frame callback has not completed.");
            _dispatchStopped = false;
            _pendingFrames = null;
            _lifecycleVersion++;
            _seqInitialized = false;
            _hasRestartCandidate = false;
            _waitingForKeyFrame = true;
            _isConnected = false;
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try { _udpSocket.ReceiveBufferSize = 1024 * 1024; } catch { }

        try { _udpSocket.Bind(new IPEndPoint(IPAddress.Any, localPort)); }
        catch { _udpSocket.Dispose(); _udpSocket = null; throw; }
        LocalPort = ((IPEndPoint)_udpSocket.LocalEndPoint!).Port;

        if (UsePsDemux)
            _psReassembler = new PsReassembler(MaxPsBufferSize);

        _receiveTask = ReceiveLoopAsync(_udpSocket, cancellationToken);
        }
        _logger.LogInformation("RTP 客户端监听 UDP 端口 {Port}", LocalPort);
        return Task.CompletedTask;
    }

    /// <summary>停止 RTP 接收。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>停止 RTP 接收完成时结束的异步任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? dispatchCompletion;
        Task? inputCompletion;
        Task? receiveTask;
        Socket? socket;
        long stopVersion;
        Task? disconnectTask;
        lock (_dispatchSync)
        {
            stopVersion = ++_lifecycleVersion;
            socket = _udpSocket;
            receiveTask = _receiveTask;
            _dispatchStopped = true;
            _stopInProgress = true;
            _pendingFrames?.DisposeAsync().GetAwaiter().GetResult();
            dispatchCompletion = _dispatching
                ? (_dispatchCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : null;
            inputCompletion = _activeInputs > 0
                ? (_inputCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : null;
            if (_isConnected)
            {
                _isConnected = false;
                var handlers = StreamDisconnectedAsync?.GetInvocationList();
                // 用户委托可能在返回 ValueTask 前就阻塞。每个连接只隔离调度一次生命周期回调，并将其纳入停止期限。
                if (handlers != null) _disconnectTask = Task.Run(() => NotifyDisconnectedAsync(handlers, "主动停止"));
            }
            disconnectTask = _disconnectTask;
        }

        try { socket?.Close(); } catch { }
        try
        {
            var pending = Task.WhenAll(receiveTask ?? Task.CompletedTask,
                inputCompletion ?? Task.CompletedTask, dispatchCompletion ?? Task.CompletedTask,
                disconnectTask ?? Task.CompletedTask);
            await pending.WaitAsync(FrameHandlerStopTimeout > TimeSpan.Zero
                ? FrameHandlerStopTimeout : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("RTP frame callback did not finish within {Timeout}; its consumer retains ownership until it releases the frame.", FrameHandlerStopTimeout);
        }
        finally
        {
            // 同步消费者可能仍在包读取线程上执行。即使停止已超时，也必须等输入返回后才能释放其解析缓存。
            if (inputCompletion is { IsCompleted: false }) _ = ResetAfterInputAsync(inputCompletion, stopVersion);
            else ResetStoppedBuffers(stopVersion);
        }
        _logger.LogInformation("RTP 客户端已停止");
    }

    /// <summary>通知本次连接结束时捕获的断连订阅者。</summary>
    /// <param name="handlers">本次通知的事件处理委托快照。</param>
    /// <param name="reason">状态变化或失败原因。</param>
    /// <returns>通知本次连接结束时捕获的断连订阅者完成时结束的异步任务。</returns>
    private async Task NotifyDisconnectedAsync(Delegate[] handlers, string reason)
    {
        foreach (var entry in handlers)
        {
            try { await ((Func<RtpClient, string, ValueTask>)entry)(this, reason).ConfigureAwait(false); }
            catch (Exception ex) { LogFrameError(ex, "RTP stream disconnected handler failed"); }
        }
    }

    /// <summary>记录媒体处理错误并隔离日志写入异常。</summary>
    /// <param name="exception">当前处理的异常。</param>
    /// <param name="message">待处理的消息。</param>
    private void LogFrameError(Exception exception, string message)
    {
        try { _logger.LogError(exception, message); }
        catch { /* 日志写入失败不能让分发器或池化帧无法回收。 */ }
    }

    /// <summary>等待在途输入结束后清理对应生命周期的缓冲区。</summary>
    /// <param name="completion">表示当前处理完成的异步信号。</param>
    /// <param name="stopVersion">本次停止操作所属的生命周期版本。</param>
    /// <returns>等待在途输入结束后清理对应生命周期的缓冲区完成时结束的异步任务。</returns>
    private async Task ResetAfterInputAsync(Task completion, long stopVersion)
    {
        await completion.ConfigureAwait(false);
        ResetStoppedBuffers(stopVersion);
    }

    /// <summary>仅清理指定停止版本仍拥有的媒体缓存。</summary>
    /// <param name="stopVersion">本次停止操作所属的生命周期版本。</param>
    private void ResetStoppedBuffers(long stopVersion)
    {
        lock (_dispatchSync)
        {
            if (stopVersion != _lifecycleVersion) return;
            _psReassembler?.Reset();
            ResetRawFragmentBuffer();
            ResetRawAccessUnitBuffer();
            _stopInProgress = false;
        }
    }

    /// <summary>
    /// 接收Loop，并异步完成操作。
    /// </summary>
    /// <param name="socket">当前接收代次独占的套接字。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>接收Loop，并异步完成操作完成时结束的异步任务。</returns>
    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var remoteEp = (EndPoint)new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesReceived;
#if NET7_0_OR_GREATER
                var result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(), SocketFlags.None, remoteEp, cancellationToken).ConfigureAwait(false);
                bytesReceived = result.ReceivedBytes;
                remoteEp = result.RemoteEndPoint;
#else
                bytesReceived = await Task.Run(() =>
                    socket.ReceiveFrom(buffer, ref remoteEp), cancellationToken).ConfigureAwait(false);
#endif

                if (bytesReceived > 0)
                {
                    ProcessRtpPacket(buffer.AsSpan(0, bytesReceived), remoteEp as IPEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTP 客户端接收异常");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            socket.Dispose();
            Task? inputs = null;
            long version = 0;
            lock (_dispatchSync)
            {
                if (ReferenceEquals(socket, _udpSocket))
                {
                    _udpSocket = null;
                    LocalPort = 0;
                    _dispatchStopped = true;
                    _pendingFrames?.DisposeAsync().GetAwaiter().GetResult();
                    if (_isConnected)
                    {
                        _isConnected = false;
                        var handlers = StreamDisconnectedAsync?.GetInvocationList();
                        if (handlers != null) _disconnectTask = Task.Run(() => NotifyDisconnectedAsync(handlers,
                            cancellationToken.IsCancellationRequested ? "接收取消" : "接收终止"));
                    }
                    if (!_stopInProgress)
                    {
                        version = _lifecycleVersion;
                        _stopInProgress = true;
                        if (_activeInputs > 0)
                            inputs = (_inputCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                    }
                }
            }
            if (version != 0)
            {
                if (inputs != null) _ = ResetAfterInputAsync(inputs, version);
                else ResetStoppedBuffers(version);
            }
        }
    }

    /// <summary>
    /// 从外部喂入 RTP 数据包（用于 RTP-over-TCP interleaved 模式）。
    /// <para>
    /// 当 RTSP 通过 TCP interleaved 拉流时，远端 RTP 包通过 RTSP TCP 连接内嵌传输
    /// （$ + channel + length 前缀）。上层从 TCP 流解析出完整的 RTP 包后调用本方法，
    /// 即可复用本类的 RTP 解封装 + 帧重组 + 事件分发逻辑，无需绑定本地 UDP 端口。
    /// </para>
    /// <para>
    /// 与 <see cref="StartAsync"/> 互斥：调用本方法前不要调用 StartAsync（否则会有两路输入）。
    /// </para>
    /// </summary>
    /// <param name="data">完整的 RTP 包（含 RTP 头）。</param>
    /// <param name="remoteEp">远端地址（仅用于日志和事件，可为 null）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushRtpPacket(ReadOnlySpan<byte> data, IPEndPoint? remoteEp = null)
        => ProcessRtpPacket(data, remoteEp);

    /// <summary>
    /// 处理RTP数据包。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="remoteEp">远端网络端点。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessRtpPacket(ReadOnlySpan<byte> data, IPEndPoint? remoteEp)
    {
        lock (_dispatchSync)
        {
            if (_dispatchStopped) return;
            _activeInputs++;
        }
        try { ProcessRtpPacketCore(data, remoteEp); }
        finally
        {
            lock (_dispatchSync)
            {
                if (--_activeInputs == 0)
                {
                    _inputCompletion?.TrySetResult(true);
                    _inputCompletion = null;
                }
            }
        }
    }

    /// <summary>解析 RTP 包并将负载交给对应媒体解包器。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="remoteEp">远端网络端点。</param>
    private void ProcessRtpPacketCore(ReadOnlySpan<byte> data, IPEndPoint? remoteEp)
    {
        if (data.Length < StandardRtpHeader.HeaderLength) return;

        var header = StandardRtpHeader.Parse(data);
        if (header.Version != 2) return;

        if (!header.TryGetPayload(data, out var payload)) return;

        // 首次收到数据 → 连接事件
        if (!_isConnected)
        {
            _isConnected = true;
            _connectedTime = DateTimeOffset.UtcNow;
            _remoteEndPoint = remoteEp;
            Stats.LastPacketTime = Environment.TickCount64;

            _logger.LogInformation("RTP 客户端收到远端数据: {Remote}", remoteEp);
            var connHandler = StreamConnectedAsync;
            if (connHandler != null && remoteEp != null)
            {
                try { ObserveEvent(connHandler(this, remoteEp), "stream connected"); }
                catch (Exception ex) { _logger.LogError(ex, "RTP stream connected handler failed"); }
            }
        }

        // 序列号跟踪 & 丢包检测
        Stats.PacketsReceived++;
        Stats.BytesReceived += payload.Length;
        Stats.LastPacketTime = Environment.TickCount64;

        int lost = TrackSequenceNumber(header.SequenceNumber);
        if (lost < 0)
            return;

        if (lost > 0)
        {
            _logger.LogWarning("RTP 丢包: 丢失 {Lost} 包, Seq={Seq}", lost, header.SequenceNumber);
            _waitingForKeyFrame = RawFrameKind == MediaFrameKind.Video || UsePsDemux;
            if (UsePsDemux)
            {
                _psReassembler ??= new PsReassembler(MaxPsBufferSize);
                _psReassembler.DiscardUntilNextPackStart();
                _ = _psReassembler.ConsumeDiscontinuity();
            }
            else
                MarkRawVideoDiscontinuity();

            var lossHandler = PacketLossDetectedAsync;
            if (lossHandler != null)
            {
                try { ObserveEvent(lossHandler(this, lost), "packet loss"); }
                catch (Exception ex) { _logger.LogError(ex, "RTP packet loss handler failed"); }
            }
        }

        // 解封装
        if (UsePsDemux)
        {
            // PS 封装：重组后解析。
            // _psReassembler 在 StartAsync 里预先初始化；TCP interleaved 模式不调用
            // StartAsync（不绑 UDP 端口），此处懒初始化保证 PushRtpPacket 路径也能正常重组。
            _psReassembler ??= new PsReassembler(MaxPsBufferSize);
            var psFrames = _psReassembler.Feed(payload, header.Marker);
            bool feedDiscontinuity = _psReassembler.ConsumeDiscontinuity();
            if (feedDiscontinuity)
                _waitingForKeyFrame = true;
            if (psFrames == null || psFrames.Count == 0) return;

            foreach (var pesFrame in psFrames)
            {
                try
                {
                    long timestampMs = pesFrame.Timestamp90kHz > 0
                        ? pesFrame.Timestamp90kHz / 90
                        : Environment.TickCount64;

                    bool isKeyFrame = pesFrame.Kind == MediaFrameKind.Video
                        && IsKeyFrame(pesFrame.GetSpan(), pesFrame.Codec);

                    if (feedDiscontinuity && pesFrame.Kind == MediaFrameKind.Video)
                        continue;

                    if (!pesFrame.TryTakeOwnership(out var frameBuffer, out var frameLength))
                        continue;

                    var frame = PooledMediaFrame.TakeOwnership(
                        frameBuffer, frameLength, pesFrame.Kind, pesFrame.Codec, isKeyFrame, timestampMs);

                    DispatchFrame(frame);
                }
                finally
                {
                    pesFrame.Dispose();
                }
            }
        }
        else
        {
            ProcessRawPayload(payload, header.Timestamp, header.Marker);
        }
    }

    /// <summary>
    /// 处理Raw负载。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessRawPayload(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        if (payload.Length == 0)
        {
            if (RawFrameKind == MediaFrameKind.Video)
                MarkRawVideoDiscontinuity();
            return;
        }

        if (RawFrameKind == MediaFrameKind.Audio)
        {
            DispatchFrame(PooledMediaFrame.Create(payload, RawFrameKind, RawCodec, false, ToTimestampMs(rtpTimestamp)));
            return;
        }

        if (RawCodec == MediaCodec.H264)
        {
            ProcessH264Payload(payload, rtpTimestamp, marker);
            return;
        }

        if (RawCodec == MediaCodec.H265)
        {
            ProcessH265Payload(payload, rtpTimestamp, marker);
            return;
        }

        DispatchFrame(PooledMediaFrame.Create(payload, RawFrameKind, RawCodec, false, ToTimestampMs(rtpTimestamp)));
    }

    /// <summary>
    /// 处理H264负载。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH264Payload(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        var nalType = payload[0] & 0x1F;
        if (nalType is >= 1 and <= 23)
        {
            AppendRawNal(payload, MediaCodec.H264, rtpTimestamp, marker);
            return;
        }

        if (nalType == 24)
        {
            ProcessH264StapA(payload, rtpTimestamp, marker);
            return;
        }

        if (nalType == 28 && payload.Length > 2)
        {
            ProcessH264FuA(payload, rtpTimestamp, marker);
            return;
        }

        MarkRawVideoDiscontinuity();
    }

    /// <summary>
    /// 处理H264StapA。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH264StapA(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        var offset = 1;
        var totalLength = 0;
        while (offset + 2 <= payload.Length)
        {
            var naluLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            if (naluLength == 0 || offset + naluLength > payload.Length)
            {
                MarkRawVideoDiscontinuity();
                return;
            }
            totalLength += 4 + naluLength;
            offset += naluLength;
        }

        if (offset != payload.Length || totalLength == 0 ||
            !PrepareRawAccessUnit(rtpTimestamp) || !EnsureRawAccessUnitCapacity(totalLength))
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        offset = 1;
        while (offset + 2 <= payload.Length)
        {
            var naluLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            WriteStartCode(_rawAccessUnitBuffer!.AsSpan(_rawAccessUnitLength, 4));
            _rawAccessUnitLength += 4;
            _rawAccessUnitIsKeyFrame |= IsKeyFrame(payload.Slice(offset, 1), MediaCodec.H264);
            payload.Slice(offset, naluLength).CopyTo(_rawAccessUnitBuffer.AsSpan(_rawAccessUnitLength));
            _rawAccessUnitLength += naluLength;
            offset += naluLength;
        }

        if (marker)
            DispatchCompletedRawAccessUnit(MediaCodec.H264);
    }

    /// <summary>
    /// 处理H264FuA。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH264FuA(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        var fuIndicator = payload[0];
        var fuHeader = payload[1];
        var isStart = (fuHeader & 0x80) != 0;
        var isEnd = (fuHeader & 0x40) != 0;
        int originalType = fuHeader & 0x1F;
        var nalHeader = (byte)((fuIndicator & 0xE0) | originalType);

        if ((isStart && isEnd) || originalType is < 1 or > 23)
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        if (isStart)
        {
            if (_rawFragmentActive)
                MarkRawVideoDiscontinuity();
            if (!PrepareRawAccessUnit(rtpTimestamp))
                return;
            if (!EnsureRawFragmentCapacity(payload.Length + 4))
                return;
            var fragmentBuffer = _rawFragmentBuffer!;
            WriteStartCode(fragmentBuffer.AsSpan(0, 4));
            fragmentBuffer[4] = nalHeader;
            payload[2..].CopyTo(fragmentBuffer.AsSpan(5));
            _rawFragmentLength = 5 + payload.Length - 2;
            _rawFragmentTimestamp = rtpTimestamp;
            _rawFragmentActive = true;
        }
        else if (_rawFragmentActive && _rawFragmentTimestamp == rtpTimestamp)
        {
            if (!AppendRawFragment(payload[2..]))
                return;
        }
        else
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        if (!isEnd)
        {
            if (marker)
                MarkRawVideoDiscontinuity();
            return;
        }

        AppendCompletedRawFragment(MediaCodec.H264, marker);
    }

    /// <summary>
    /// 处理H265负载。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH265Payload(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        if (payload.Length < 2)
        {
            MarkRawVideoDiscontinuity();
            return;
        }
        var nalType = (payload[0] >> 1) & 0x3F;

        if (nalType is >= 0 and <= 47)
        {
            AppendRawNal(payload, MediaCodec.H265, rtpTimestamp, marker);
            return;
        }

        if (nalType == 48)
        {
            ProcessH265Ap(payload, rtpTimestamp, marker);
            return;
        }

        if (nalType == 49 && payload.Length > 3)
        {
            ProcessH265Fu(payload, rtpTimestamp, marker);
            return;
        }

        MarkRawVideoDiscontinuity();
    }

    /// <summary>
    /// 处理H265Ap。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH265Ap(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        var offset = 2;
        var totalLength = 0;
        while (offset + 2 <= payload.Length)
        {
            var naluLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            if (naluLength == 0 || offset + naluLength > payload.Length)
            {
                MarkRawVideoDiscontinuity();
                return;
            }
            totalLength += 4 + naluLength;
            offset += naluLength;
        }

        if (offset != payload.Length || totalLength == 0 ||
            !PrepareRawAccessUnit(rtpTimestamp) || !EnsureRawAccessUnitCapacity(totalLength))
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        offset = 2;
        while (offset + 2 <= payload.Length)
        {
            var naluLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            WriteStartCode(_rawAccessUnitBuffer!.AsSpan(_rawAccessUnitLength, 4));
            _rawAccessUnitLength += 4;
            _rawAccessUnitIsKeyFrame |= IsKeyFrame(payload.Slice(offset, 1), MediaCodec.H265);
            payload.Slice(offset, naluLength).CopyTo(_rawAccessUnitBuffer.AsSpan(_rawAccessUnitLength));
            _rawAccessUnitLength += naluLength;
            offset += naluLength;
        }

        if (marker)
            DispatchCompletedRawAccessUnit(MediaCodec.H265);
    }

    /// <summary>
    /// 处理H265Fu。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void ProcessH265Fu(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool marker)
    {
        var fuHeader = payload[2];
        var isStart = (fuHeader & 0x80) != 0;
        var isEnd = (fuHeader & 0x40) != 0;
        var originalType = fuHeader & 0x3F;
        var nal0 = (byte)((payload[0] & 0x81) | (originalType << 1));
        var nal1 = payload[1];

        if ((isStart && isEnd) || originalType > 47)
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        if (isStart)
        {
            if (_rawFragmentActive)
                MarkRawVideoDiscontinuity();
            if (!PrepareRawAccessUnit(rtpTimestamp))
                return;
            if (!EnsureRawFragmentCapacity(payload.Length + 4))
                return;
            var fragmentBuffer = _rawFragmentBuffer!;
            WriteStartCode(fragmentBuffer.AsSpan(0, 4));
            fragmentBuffer[4] = nal0;
            fragmentBuffer[5] = nal1;
            payload[3..].CopyTo(fragmentBuffer.AsSpan(6));
            _rawFragmentLength = 6 + payload.Length - 3;
            _rawFragmentTimestamp = rtpTimestamp;
            _rawFragmentActive = true;
        }
        else if (_rawFragmentActive && _rawFragmentTimestamp == rtpTimestamp)
        {
            if (!AppendRawFragment(payload[3..]))
                return;
        }
        else
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        if (!isEnd)
        {
            if (marker)
                MarkRawVideoDiscontinuity();
            return;
        }

        AppendCompletedRawFragment(MediaCodec.H265, marker);
    }

    /// <summary>把完整裸流 NAL 追加到当前访问单元。</summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void AppendRawNal(
        ReadOnlySpan<byte> payload,
        MediaCodec codec,
        uint rtpTimestamp,
        bool marker)
    {
        if (!PrepareRawAccessUnit(rtpTimestamp) ||
            !EnsureRawAccessUnitCapacity(payload.Length + 4))
            return;

        WriteStartCode(_rawAccessUnitBuffer!.AsSpan(_rawAccessUnitLength, 4));
        _rawAccessUnitLength += 4;
        payload.CopyTo(_rawAccessUnitBuffer.AsSpan(_rawAccessUnitLength));
        _rawAccessUnitLength += payload.Length;
        _rawAccessUnitIsKeyFrame |= IsKeyFrame(payload.Slice(0, 1), codec);

        if (marker)
            DispatchCompletedRawAccessUnit(codec);
    }

    /// <summary>把已完整重组的分片 NAL 追加到访问单元。</summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    private void AppendCompletedRawFragment(MediaCodec codec, bool marker)
    {
        if (!_rawFragmentActive || _rawFragmentBuffer == null || _rawFragmentLength <= 4)
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        uint timestamp = _rawFragmentTimestamp;
        int length = _rawFragmentLength;
        if (!PrepareRawAccessUnit(timestamp))
            return;

        // 单个分片 NAL 可直接成为访问单元；先检查独立的 AU 上限，再移交所有权。
        if (_rawAccessUnitLength == 0)
        {
            if (length > MaxRawAccessUnitSize)
            {
                MarkRawVideoDiscontinuity();
                return;
            }
            if (_rawAccessUnitBuffer != null)
                ArrayPool<byte>.Shared.Return(_rawAccessUnitBuffer);
            _rawAccessUnitBuffer = _rawFragmentBuffer;
            _rawFragmentBuffer = null;
            _rawAccessUnitLength = length;
            _rawAccessUnitIsKeyFrame |= IsKeyFrame(_rawAccessUnitBuffer.AsSpan(4, 1), codec);
        }
        else
        {
            if (!EnsureRawAccessUnitCapacity(length))
                return;
            _rawAccessUnitIsKeyFrame |= IsKeyFrame(_rawFragmentBuffer.AsSpan(4, 1), codec);
            _rawFragmentBuffer.AsSpan(0, length)
                .CopyTo(_rawAccessUnitBuffer!.AsSpan(_rawAccessUnitLength));
            _rawAccessUnitLength += length;
        }
        ResetRawFragmentBuffer();

        if (marker)
            DispatchCompletedRawAccessUnit(codec);
    }

    /// <summary>根据时间戳为裸流访问单元建立或切换状态。</summary>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <returns>根据时间戳为裸流访问单元建立或切换状态是否成立。</returns>
    private bool PrepareRawAccessUnit(uint rtpTimestamp)
    {
        if (_rawAccessUnitActive)
        {
            if (_rawAccessUnitTimestamp == rtpTimestamp)
                return true;

            // Marker 到达前时间戳改变，表示前一访问单元被截断或结束标记丢失。
            MarkRawVideoDiscontinuity();
        }

        _rawAccessUnitTimestamp = rtpTimestamp;
        _rawAccessUnitActive = true;
        return true;
    }

    /// <summary>按容量上限为裸流访问单元预留缓冲区。</summary>
    /// <param name="appendLength">本次追加数据的字节数。</param>
    /// <returns>按容量上限为裸流访问单元预留缓冲区是否成立。</returns>
    private bool EnsureRawAccessUnitCapacity(int appendLength)
    {
        long requiredLong = (long)_rawAccessUnitLength + appendLength;
        if (appendLength < 0 || requiredLong <= 0 || requiredLong > MaxRawAccessUnitSize ||
            requiredLong > int.MaxValue)
        {
            MarkRawVideoDiscontinuity();
            return false;
        }

        int required = (int)requiredLong;
        if (_rawAccessUnitBuffer != null && _rawAccessUnitBuffer.Length >= required)
            return true;

        int currentLength = _rawAccessUnitBuffer?.Length ?? 0;
        int newSize = Math.Max(required, Math.Max(4096, Math.Min(MaxRawAccessUnitSize, currentLength * 2)));
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        if (_rawAccessUnitBuffer != null)
        {
            _rawAccessUnitBuffer.AsSpan(0, _rawAccessUnitLength).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_rawAccessUnitBuffer);
        }

        _rawAccessUnitBuffer = newBuffer;
        return true;
    }

    /// <summary>在边界完整且满足恢复条件时发布裸流访问单元。</summary>
    /// <param name="codec">媒体编码格式。</param>
    private void DispatchCompletedRawAccessUnit(MediaCodec codec)
    {
        if (_rawFragmentActive || !_rawAccessUnitActive ||
            _rawAccessUnitBuffer == null || _rawAccessUnitLength <= 4)
        {
            MarkRawVideoDiscontinuity();
            return;
        }

        var buffer = _rawAccessUnitBuffer;
        int length = _rawAccessUnitLength;
        uint timestamp = _rawAccessUnitTimestamp;
        bool isKeyFrame = _rawAccessUnitIsKeyFrame;
        _rawAccessUnitIsKeyFrame = false;
        _rawAccessUnitBuffer = null;
        _rawAccessUnitLength = 0;
        _rawAccessUnitTimestamp = 0;
        _rawAccessUnitActive = false;

        DispatchFrame(PooledMediaFrame.TakeOwnership(
            buffer,
            length,
            MediaFrameKind.Video,
            codec,
            isKeyFrame,
            ToTimestampMs(timestamp)));
    }

    /// <summary>按媒体时钟频率把 RTP 时间戳换算为毫秒。</summary>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    /// <returns>按媒体时钟频率把 RTP 时间戳换算为毫秒计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ToTimestampMs(uint rtpTimestamp)
        => RawClockRate > 0 ? (long)(rtpTimestamp * 1000UL / (uint)RawClockRate) : Environment.TickCount64;

    /// <summary>
    /// 写入起始Code。
    /// </summary>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteStartCode(Span<byte> destination)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 1;
    }

    /// <summary>按容量上限扩展裸 RTP 分片缓冲区。</summary>
    /// <param name="appendLength">本次追加数据的字节数。</param>
    /// <returns>按容量上限扩展裸 RTP 分片缓冲区是否成立。</returns>
    private bool EnsureRawFragmentCapacity(int appendLength)
    {
        long requiredLong = (long)_rawFragmentLength + appendLength;
        if (appendLength < 0 || requiredLong <= 0 || requiredLong > MaxRawFragmentSize ||
            requiredLong > int.MaxValue)
        {
            MarkRawVideoDiscontinuity();
            return false;
        }

        int required = (int)requiredLong;

        if (_rawFragmentBuffer != null && _rawFragmentBuffer.Length >= required) return true;

        var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(required, 4096));
        if (_rawFragmentBuffer != null && _rawFragmentLength > 0)
        {
            _rawFragmentBuffer.AsSpan(0, _rawFragmentLength).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_rawFragmentBuffer);
        }
        _rawFragmentBuffer = newBuffer;
        return true;
    }

    /// <summary>把当前 RTP 分片追加到 NAL 重组缓冲区。</summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <returns>把当前 RTP 分片追加到 NAL 重组缓冲区是否成立。</returns>
    private bool AppendRawFragment(ReadOnlySpan<byte> payload)
    {
        if (!EnsureRawFragmentCapacity(payload.Length))
            return false;
        payload.CopyTo(_rawFragmentBuffer!.AsSpan(_rawFragmentLength));
        _rawFragmentLength += payload.Length;
        return true;
    }

    /// <summary>丢弃未完成的裸 RTP 分片并重置重组状态。</summary>
    private void ResetRawFragmentBuffer()
    {
        if (_rawFragmentBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_rawFragmentBuffer);
            _rawFragmentBuffer = null;
        }
        _rawFragmentLength = 0;
        _rawFragmentActive = false;
        _rawFragmentTimestamp = 0;
    }

    /// <summary>清空裸流访问单元的累计数据与时间戳状态。</summary>
    private void ResetRawAccessUnitBuffer()
    {
        _rawAccessUnitIsKeyFrame = false;
        if (_rawAccessUnitBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_rawAccessUnitBuffer);
            _rawAccessUnitBuffer = null;
        }
        _rawAccessUnitLength = 0;
        _rawAccessUnitTimestamp = 0;
        _rawAccessUnitActive = false;
    }

    /// <summary>丢弃不完整视频并重新等待可解码的关键帧。</summary>
    private void MarkRawVideoDiscontinuity()
    {
        ResetRawFragmentBuffer();
        ResetRawAccessUnitBuffer();
        _waitingForKeyFrame = true;
    }

    /// <summary>将完整媒体帧交给会话的串行分发队列。</summary>
    /// <param name="frame">媒体帧。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DispatchFrame(PooledMediaFrame frame)
    {
        if (frame.Kind == MediaFrameKind.Video && _waitingForKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                if (!VideoNalUnitHelper.IsParameterSetOnly(frame.GetSpan(), frame.Codec))
                {
                    frame.Release();
                    return;
                }
            }
            else
            {
                _waitingForKeyFrame = false;
            }
        }

        lock (_dispatchSync)
        {
            if (_dispatchStopped)
            {
                frame.Release();
                return;
            }
            if (_dispatching)
            {
                _pendingFrames ??= new MediaStreamSubscription(_ => { }, FrameDispatchQueueCapacity, dropStaleFrames: true);
                if (!_pendingFrames.TryWrite(frame)) frame.Release();
                return;
            }
            _dispatching = true;
        }

        while (true)
        {
            var pending = InvokeFrameHandlers(frame);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = DrainFramesAsync(pending);
                return;
            }
            pending.GetAwaiter().GetResult();
            if (!TryReadNextFrame(out frame)) return;
        }
    }

    /// <summary>从待分发队列中取出下一帧。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private bool TryReadNextFrame(out PooledMediaFrame frame)
    {
        lock (_dispatchSync)
        {
            if (!_dispatchStopped && _pendingFrames != null && _pendingFrames.Reader.TryRead(out frame!))
                return true;
            frame = null!;
            _dispatching = false;
            _dispatchCompletion?.TrySetResult(true);
            _dispatchCompletion = null;
            return false;
        }
    }

    /// <summary>等待当前异步回调结束并继续串行分发排队帧。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <returns>等待当前异步回调结束并继续串行分发排队帧完成时结束的异步任务。</returns>
    private async Task DrainFramesAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
            while (TryReadNextFrame(out var frame))
                await InvokeFrameHandlers(frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFrameError(ex, "RTP frame dispatcher failed");
            lock (_dispatchSync)
            {
                _pendingFrames?.DisposeAsync().GetAwaiter().GetResult();
                _pendingFrames = null;
                _dispatching = false;
                _dispatchCompletion?.TrySetResult(true);
                _dispatchCompletion = null;
            }
        }
    }

    /// <summary>调用当前媒体帧订阅者并跟踪异步完成。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>调用当前媒体帧订阅者并跟踪异步完成完成时结束的异步任务。</returns>
    private ValueTask InvokeFrameHandlers(PooledMediaFrame frame)
    {
        var handlers = Volatile.Read(ref _frameHandlers);
        if (handlers.Length == 0)
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        // RtpClient 的既有约定是向事件消费者转移所有权。任何订阅者可能同步释放前，先为每个多播订阅者各保留一个独立引用。
        int retainedHandlers = 1;
        while (retainedHandlers < handlers.Length && frame.TryRetain())
            retainedHandlers++;

        List<Task>? pendingHandlers = null;
        for (int i = 0; i < retainedHandlers; i++)
        {
            try
            {
                var handler = (Func<RtpClient, PooledMediaFrame, ValueTask>)handlers[i];
                var pending = handler(this, frame);
                if (pending.IsCompletedSuccessfully) pending.GetAwaiter().GetResult();
                else (pendingHandlers ??= new List<Task>()).Add(ObserveFrameHandlerAsync(pending));
            }
            catch (Exception ex)
            {
                // 即使消费者处理失败，引用所有权也已经转移给它。
                LogFrameError(ex, "RTP frame handler failed");
            }
        }
        return pendingHandlers == null ? ValueTask.CompletedTask : new ValueTask(Task.WhenAll(pendingHandlers));
    }

    /// <summary>观察异步帧回调完成并处理回调异常。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <returns>观察异步帧回调完成并处理回调异常完成时结束的异步任务。</returns>
    private async Task ObserveFrameHandlerAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFrameError(ex, "RTP asynchronous frame handler failed");
        }
    }

    /// <summary>观察媒体事件异步处理的完成状态。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <param name="eventName">事件名称。</param>
    private void ObserveEvent(ValueTask pending, string eventName)
    {
        try
        {
            if (pending.IsCompletedSuccessfully)
                pending.GetAwaiter().GetResult();
            else
                _ = ObserveEventAsync(pending, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTP {EventName} handler failed", eventName);
        }
    }

    /// <summary>等待媒体事件完成并隔离通知异常。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <param name="eventName">事件名称。</param>
    /// <returns>等待媒体事件完成并隔离通知异常完成时结束的异步任务。</returns>
    private async Task ObserveEventAsync(ValueTask pending, string eventName)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTP asynchronous {EventName} handler failed", eventName);
        }
    }

    /// <summary>跟踪 RTP 序列号并计算本次新增丢包数量。</summary>
    /// <param name="seqNum">RTP 包序列号。</param>
    /// <returns>跟踪 RTP 序列号并计算本次新增丢包数量计算得到的数值。</returns>
    private int TrackSequenceNumber(ushort seqNum)
    {
        if (!_seqInitialized)
        {
            _lastSeqNum = seqNum;
            _seqInitialized = true;
            Stats.LastSequenceNumber = seqNum;
            return 0;
        }

        int forwardDelta = (ushort)(seqNum - _lastSeqNum);
        if (forwardDelta == 0)
        {
            Stats.PacketsOutOfOrder++;
            return -1;
        }

        if (forwardDelta < MaxSequenceDropout)
        {
            _hasRestartCandidate = false;
            _lastSeqNum = seqNum;
            Stats.LastSequenceNumber = seqNum;
            int lost = forwardDelta - 1;
            if (lost > 0)
                Stats.PacketsLost += lost;
            return lost;
        }

        if (forwardDelta <= ushort.MaxValue + 1 - MaxSequenceMisorder)
        {
            if (_hasRestartCandidate && seqNum == _restartCandidateNextSeq)
            {
                _hasRestartCandidate = false;
                _lastSeqNum = seqNum;
                Stats.LastSequenceNumber = seqNum;
                Stats.PacketsLost++;
                return 1;
            }

            _hasRestartCandidate = true;
            _restartCandidateNextSeq = (ushort)(seqNum + 1);
            return -1;
        }

        Stats.PacketsOutOfOrder++;
        return -1;
    }

    /// <summary>
    /// 判断是否键帧。
    /// </summary>
    /// <param name="span">待处理的连续数据区域。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否键帧的判定结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsKeyFrame(ReadOnlySpan<byte> span, MediaCodec codec) =>
        VideoNalUnitHelper.IsRandomAccessPoint(span, codec);

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

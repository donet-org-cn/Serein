using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;

namespace Serein.Media.Relay;

/// <summary>
/// 统一的媒体流中转枢纽。
/// <para>
/// 接收任意来源（PS/RTP / RTSP 源 / 标准RTP）的媒体帧，
/// 通过 pub/sub 模式分发给下游消费者。
/// </para>
/// <para>功能：引用计数零拷贝多播、流连接/断开事件、观看者加入/离开事件、无人观看时自动断流、流录制。</para>
/// </summary>
public sealed class MediaStreamHub : IAsyncDisposable
{
    /// <summary>实时媒体订阅的默认队列容量。</summary>
    private const int DefaultLiveSubscriptionCapacity = 32;
    /// <summary>外部扩展异常日志的最小输出间隔，单位为毫秒。</summary>
    private const long ExtensionErrorLogIntervalMs = 5000;
    /// <summary>按媒体流标识保存的状态索引。</summary>
    private readonly ConcurrentDictionary<MediaStreamKey, StreamContext> _streams = new();
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>保护媒体帧发布回调列表的互斥锁。</summary>
    private readonly object _frameCallbackSync = new();
    /// <summary>媒体帧发布事件的组合委托。</summary>
    private Action<PooledMediaFrame, string, string>? _framePublishedCallback;
    /// <summary>媒体帧发布订阅者的快照。</summary>
    private Delegate[] _framePublishedHandlers = Array.Empty<Delegate>();
    /// <summary>最近一次输出帧回调错误日志的单调时刻。</summary>
    private long _lastFrameCallbackErrorLogTick;

    #region 事件

    /// <summary>流连接成功（收到第一帧数据时触发）。</summary>
    public event Func<MediaStreamHub, StreamConnectedEventArgs, ValueTask>? StreamConnectedAsync;

    /// <summary>流断开。</summary>
    public event Func<MediaStreamHub, StreamDisconnectedEventArgs, ValueTask>? StreamDisconnectedAsync;

    /// <summary>新观看者加入。</summary>
    public event Func<MediaStreamHub, StreamViewerJoinedEventArgs, ValueTask>? ViewerJoinedAsync;

    /// <summary>观看者离开。</summary>
    public event Func<MediaStreamHub, StreamViewerLeftEventArgs, ValueTask>? ViewerLeftAsync;

    /// <summary>每秒更新本次连接的统计；慢处理器会延迟通知，断开时取消后续通知。</summary>
    public event Func<MediaStreamHub, StreamStatsUpdatedEventArgs, ValueTask>? StreamStatsUpdatedAsync;

    /// <summary>
    /// 需要自动拉流（有观看者但流未连接时触发）。
    /// <para>上游应响应此事件开始推流。</para>
    /// </summary>
    public event Func<MediaStreamHub, MediaStreamKey, ValueTask>? StreamAutoStartRequiredAsync;

    /// <summary>
    /// 需要自动断流（无观看者超过延迟时间后触发）。
    /// <para>上游应响应此事件停止推流。</para>
    /// </summary>
    public event Func<MediaStreamHub, MediaStreamKey, ValueTask>? StreamAutoStopRequiredAsync;

    /// <summary>实现 IStreamRecorderLifecycle 的录制器实际创建文件后触发一次。</summary>
    public event Func<MediaStreamHub, StreamRecordStartedEventArgs, ValueTask>? RecordStartedAsync;

    /// <summary>流录制停止。</summary>
    public event Func<MediaStreamHub, StreamRecordStoppedEventArgs, ValueTask>? RecordStoppedAsync;

    /// <summary>流质量异常（冻结/花屏/丢包等）。</summary>
    public event Func<MediaStreamHub, StreamQualityEventArgs, ValueTask>? StreamQualityIssueAsync;

    /// <summary>收到未知 SSRC 的 RTP 数据（流未注册）。</summary>
    public event Func<MediaStreamHub, uint, System.Net.IPEndPoint, ValueTask>? UnknownSsrcReceivedAsync;

    /// <summary>
    /// 帧广播回调（所有流的所有帧都会触发）。
    /// <para>用于外部输出（如 RTSP）直接获取帧数据，无需订阅 Channel。</para>
    /// </summary>
    public Action<PooledMediaFrame, string, string>? FramePublishedCallback
    {
        get => _framePublishedCallback;
        set
        {
            lock (_frameCallbackSync)
            {
                _framePublishedCallback = value;
                Volatile.Write(ref _framePublishedHandlers,
                    value?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
    }

    /// <summary>
    /// 流录制器工厂。用于创建 <see cref="IStreamRecorder"/> 实例。
    /// <para>使用录制功能前设置此工厂，例如创建 RTSP 库中的 StreamRecorder。</para>
    /// </summary>
    public Func<StreamOptions, ILogger, IStreamRecorder>? RecorderFactory { get; set; }

    #endregion

    /// <summary>创建实例。</summary>
    /// <param name="logger">日志记录器。</param>
    public MediaStreamHub(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaStreamHub>.Instance;
    }

    /// <summary>当前所有活跃流的订阅者数量。</summary>
    /// <returns>当前所有活跃流的订阅者数量得到的IReadOnlyDictionary&lt;MediaStreamKey, int&gt;对象。</returns>
    public IReadOnlyDictionary<MediaStreamKey, int> GetSubscriptionCounts()
    {
        var result = new Dictionary<MediaStreamKey, int>();
        foreach (var kvp in _streams)
        {
            if (kvp.Value.Subscribers.Count > 0)
                result[kvp.Key] = kvp.Value.Subscribers.Count;
        }
        return result;
    }

    /// <summary>获取指定流的选项。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>获取指定流的选项得到的StreamOptions?对象。</returns>
    public StreamOptions? GetStreamOptions(string streamId, string channel)
    {
        var key = new MediaStreamKey(streamId, channel);
        return _streams.TryGetValue(key, out var ctx) ? ctx.Options : null;
    }

    /// <summary>指定流当前是否已经收到媒体帧并处于接收状态。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>指定流当前是否已经收到媒体帧并处于接收状态是否成立。</returns>
    public bool IsStreamReceiving(string streamId, string channel)
    {
        var key = new MediaStreamKey(streamId, channel);
        return _streams.TryGetValue(key, out var ctx) && ctx.IsReceiving;
    }

    /// <summary>
    /// 注册一个流（在推流开始前调用，配置流行为）。
    /// </summary>
    /// <param name="options">配置选项。</param>
    public void RegisterStream(StreamOptions options)
    {
        var key = new MediaStreamKey(options.StreamId, options.Channel);
        var ctx = _streams.GetOrAdd(key, _ => new StreamContext(options, _logger, this));
        ctx.Options = options;
        // 重新绑定质量监控器（流重新注册时）
        ctx.QualityMonitor.Bind(options.StreamId, options.Channel);

        // 如果需要录制，创建录制器
        if (options.RecordToFile)
            EnsureRecorder(ctx);
        else
            StopAndDisposeRecorder(DetachRecorder(ctx));

        ctx.ResetAudioProcessor();
        if (options.AudioProcessing.Enabled)
        {
            ctx.AudioProcessor = new RtpServerAudioProcessor(key, options.AudioProcessing, _logger);
        }

        _logger.LogInformation("注册流 [{Key}], 来源={Source}, 自动断流={AutoStop}, 录制={Record}, 音频处理={AudioProcessing}",
            key, options.Source, options.AutoStopWhenNoViewers, options.RecordToFile, options.AudioProcessing.Enabled);
    }

    /// <summary>按流配置创建或取得当前录制器。</summary>
    /// <param name="ctx">当前请求上下文。</param>
    private void EnsureRecorder(StreamContext ctx)
    {
        if (!ctx.Options.RecordToFile)
            return;

        lock (ctx.RecorderSync)
        {
            if (ctx.Recorder == null && RecorderFactory != null)
            {
                var recorder = RecorderFactory(ctx.Options, _logger);
                ctx.Recorder = recorder is IStreamRecorderLifecycle lifecycle
                    ? new NotifyingStreamRecorder(recorder, lifecycle, this, new(ctx.Options.StreamId, ctx.Options.Channel)) : recorder;
            }
        }
    }

    /// <summary>从流上下文解除录制器绑定并返回旧实例。</summary>
    /// <param name="ctx">当前请求上下文。</param>
    /// <returns>从流上下文解除录制器绑定并返回旧实例得到的IStreamRecorder?对象。</returns>
    private static IStreamRecorder? DetachRecorder(StreamContext ctx)
    {
        lock (ctx.RecorderSync)
        {
            var recorder = ctx.Recorder;
            ctx.Recorder = null;
            return recorder;
        }
    }

    /// <summary>
    /// 停止AndDisposeRecorder。
    /// </summary>
    /// <param name="recorder">媒体录制器。</param>
    private static void StopAndDisposeRecorder(IStreamRecorder? recorder)
    {
        if (recorder == null)
            return;

        try { recorder.Stop(); }
        finally { recorder.Dispose(); }
    }

    /// <summary>
    /// 注销一个流。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>注销一个流完成时结束的异步任务。</returns>
    public async ValueTask UnregisterStreamAsync(string streamId, string channel)
    {
        var key = new MediaStreamKey(streamId, channel);
        if (_streams.TryRemove(key, out var ctx))
        {
            ctx.TryStopReceiving(out var streamDuration);
            // 关闭所有订阅者
            foreach (var sub in ctx.Subscribers.Values)
            {
                await sub.DisposeAsync().ConfigureAwait(false);
            }

            // 停止录制
            var recorder = DetachRecorder(ctx);
            if (recorder != null)
            {
                (string path, long size, TimeSpan duration) recordInfo;
                try { recordInfo = recorder.Stop(); }
                finally { recorder.Dispose(); }

                await RaiseEventAsync(RecordStoppedAsync, handler =>
                    handler(this, new StreamRecordStoppedEventArgs
                    {
                        StreamId = streamId,
                        Channel = channel,
                        FilePath = recordInfo.path,
                        FileSize = recordInfo.size,
                        Duration = recordInfo.duration
                    })).ConfigureAwait(false);
            }

            await ctx.DisposeAudioProcessorAsync().ConfigureAwait(false);

            ctx.StopAutoStopTimer();
            ctx.QualityMonitor.StopCheckTimer();
            ctx.QualityMonitor.Reset();

            await RaiseEventAsync(StreamDisconnectedAsync, handler =>
                handler(this, new StreamDisconnectedEventArgs
                {
                    StreamId = streamId,
                    Channel = channel,
                    Source = ctx.Options.Source,
                    Reason = "手动注销",
                    Duration = streamDuration
                })).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 订阅指定流的媒体数据。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="subscriberDescription">用于日志定位的订阅者说明。</param>
    /// <param name="capacity">订阅缓冲区容量。</param>
    /// <returns>异步操作的结果：订阅指定流的媒体数据得到的MediaStreamSubscription对象。</returns>
    public Task<MediaStreamSubscription> SubscribeAsync(string streamId, string channel,
        string? subscriberDescription = null, int capacity = 0)
        => SubscribeCore(streamId, channel, subscriberDescription, capacity, dropStaleFrames: false);

    /// <summary>订阅实时媒体流并接收可解码的起始帧。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="subscriberDescription">用于日志定位的订阅者说明。</param>
    /// <param name="capacity">订阅缓冲区容量。</param>
    /// <returns>异步操作的结果：订阅实时媒体流并接收可解码的起始帧得到的MediaStreamSubscription对象。</returns>
    public Task<MediaStreamSubscription> SubscribeLiveAsync(string streamId, string channel,
        string? subscriberDescription = null, int capacity = 0)
        => SubscribeCore(streamId, channel, subscriberDescription, capacity, dropStaleFrames: true);

    /// <summary>在媒体流中登记订阅并应用队列容量与恢复策略。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="subscriberDescription">用于日志定位的订阅者说明。</param>
    /// <param name="capacity">订阅缓冲区容量。</param>
    /// <param name="dropStaleFrames">是否在积压时丢弃过时帧。</param>
    /// <returns>异步操作的结果：在媒体流中登记订阅并应用队列容量与恢复策略得到的MediaStreamSubscription对象。</returns>
    private Task<MediaStreamSubscription> SubscribeCore(string streamId, string channel,
        string? subscriberDescription, int capacity, bool dropStaleFrames)
    {
        var key = new MediaStreamKey(streamId, channel);
        var ctx = _streams.GetOrAdd(key, _ => new StreamContext(new StreamOptions
        {
            StreamId = streamId,
            Channel = channel
        }, _logger, this));

        var options = ctx.Options;
        // 实时输出不应积压数秒后才触发溢出处理；调用方显式设置的容量仍然有效。
        int cap = capacity > 0
            ? capacity
            : dropStaleFrames
                ? Math.Min(options.SubscriptionCapacity, DefaultLiveSubscriptionCapacity)
                : options.SubscriptionCapacity;

        // 检查最大订阅者数
        if (options.MaxSubscribers > 0 && ctx.Subscribers.Count >= options.MaxSubscribers)
        {
            throw new InvalidOperationException($"流 [{key}] 已达最大订阅者数 {options.MaxSubscribers}");
        }

        var subscription = new MediaStreamSubscription(sub => OnSubscriptionDisposed(sub, ctx), cap,
            dropStaleFrames)
        {
            Key = key,
            Description = subscriberDescription
        };

        ctx.Subscribers[subscription.Id] = subscription;
        int viewerCount = ctx.Subscribers.Count;

        _logger.LogInformation("新订阅 [{Key}]，当前订阅者: {Count}", key, viewerCount);

        // 观看者加入事件
        _ = RaiseEventAsync(ViewerJoinedAsync, handler =>
            handler(this, new StreamViewerJoinedEventArgs
            {
                StreamId = streamId,
                Channel = channel,
                SubscriberId = subscription.Id,
                ViewerCount = viewerCount,
                SubscriberDescription = subscriberDescription
            }));

        // 第一个订阅者
        if (viewerCount == 1)
        {
            ctx.StopAutoStopTimer();

            if (!ctx.IsReceiving)
            {
                _ = RaiseEventAsync(StreamAutoStartRequiredAsync, handler =>
                    handler(this, key));
            }
        }

        return Task.FromResult(subscription);
    }

    /// <summary>
    /// 标记流开始接收数据（收到第一帧时调用）。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="remoteEndPoint">远端网络端点。</param>
    public void MarkStreamConnected(string streamId, string channel, System.Net.IPEndPoint? remoteEndPoint = null)
    {
        var key = new MediaStreamKey(streamId, channel);
        var ctx = _streams.GetOrAdd(key, _ => new StreamContext(new StreamOptions
        {
            StreamId = streamId,
            Channel = channel
        }, _logger, this));

        if (ctx.TryStartReceiving())
        {
            EnsureRecorder(ctx);

            // 重置并启动质量监控
            ctx.QualityMonitor.Reset();
            ctx.QualityMonitor.StartCheckTimer();

            _ = RaiseEventAsync(StreamConnectedAsync, handler =>
                handler(this, new StreamConnectedEventArgs
                {
                    StreamId = streamId,
                    Channel = channel,
                    Source = ctx.Options.Source,
                    RemoteEndPoint = remoteEndPoint
                }));

            _logger.LogInformation("流连接 [{Key}], 来源={Remote}", key, remoteEndPoint);
        }
    }

    /// <summary>
    /// 报告 RTP 丢包（由适配器层调用，转发给对应流的质量监控器）。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="lostCount">丢失的包数量。</param>
    public void ReportPacketLoss(string streamId, string channel, int lostCount)
    {
        var key = new MediaStreamKey(streamId, channel);
        if (_streams.TryGetValue(key, out var ctx))
        {
            ctx.OnPacketLoss(lostCount);
            ctx.QualityMonitor.OnPacketLoss(lostCount);
        }
    }

    /// <summary>
    /// 内部方法：由 StreamContext 调用，将质量监控事件转发到 hub 级事件。
    /// </summary>
    /// <param name="args">事件参数。</param>
    /// <returns>内部方法：由 StreamContext 调用，将质量监控事件转发到 hub 级事件完成时结束的异步任务。</returns>
    internal ValueTask OnStreamQualityIssue(StreamQualityEventArgs args)
    {
        return RaiseEventAsync(StreamQualityIssueAsync, handler => handler(this, args));
    }

    /// <summary>转发录制器开始通知。</summary>
    /// <param name="args">事件参数。</param>
    /// <returns>转发录制器开始通知完成时结束的异步任务。</returns>
    internal ValueTask OnRecordingStarted(StreamRecordStartedEventArgs args) =>
        RaiseEventAsync(RecordStartedAsync, handler => handler(this, args));

    /// <summary>记录录制器生命周期处理失败。</summary>
    /// <param name="exception">发生的异常。</param>
    internal void OnRecorderLifecycleError(Exception exception) =>
        _logger.LogWarning(exception, "录制器生命周期通知失败");

    /// <summary>转发媒体流统计更新通知。</summary>
    /// <param name="context">当前操作的上下文。</param>
    /// <param name="args">事件参数。</param>
    /// <returns>转发媒体流统计更新通知完成时结束的异步任务。</returns>
    internal ValueTask OnStreamStatsUpdated(StreamContext context, StreamStatsUpdatedEventArgs args)
    {
        var key = new MediaStreamKey(args.StreamId, args.Channel);
        if (!_streams.TryGetValue(key, out var current) || !ReferenceEquals(current, context) || !context.IsReceiving)
            return ValueTask.CompletedTask;
        return RaiseEventAsync(StreamStatsUpdatedAsync, handler => handler(this, args));
    }

    /// <summary>
    /// 报告收到未知 SSRC 的 RTP 数据（由适配器层调用）。
    /// </summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="remoteEP">远端网络端点。</param>
    public void ReportUnknownSsrc(uint ssrc, System.Net.IPEndPoint remoteEP)
    {
        _logger.LogWarning("收到未知 SSRC 的 RTP 数据: SSRC={Ssrc}, 来源={RemoteEP}", ssrc, remoteEP);
        var handler = UnknownSsrcReceivedAsync;
        if (handler != null)
        {
            _ = handler(this, ssrc, remoteEP);
        }
    }

    /// <summary>
    /// 向所有订阅者多播一个媒体帧。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    public void Publish(PooledMediaFrame frame, string streamId, string channel)
    {
        var key = new MediaStreamKey(streamId, channel);
        if (!_streams.TryGetValue(key, out var ctx))
        {
            // 未注册的流，先调用帧广播回调（用于 RTSP 等外部输出），然后返回
            InvokeFramePublishedCallback(frame, streamId, channel);
            return;
        }

        // 首帧自动标记连接
        if (!ctx.IsReceiving)
        {
            MarkStreamConnected(streamId, channel);
        }

        // 更新统计
        ctx.OnFrameReceived(frame);

        // 更新编码信息
        if (frame.Kind == MediaFrameKind.Video && ctx.ConnectedArgs.VideoCodec == null)
            ctx.ConnectedArgs.VideoCodec = frame.Codec;
        else if (frame.Kind == MediaFrameKind.Audio && ctx.ConnectedArgs.AudioCodec == null)
            ctx.ConnectedArgs.AudioCodec = frame.Codec;

        if (frame.Kind == MediaFrameKind.Video && frame.Codec != MediaCodec.Unknown)
        {
            lock (ctx.VideoConfigSync)
            {
                if (ctx.VideoCodec != frame.Codec)
                {
                    ctx.VideoCodec = frame.Codec;
                    ctx.VideoConfig = null;
                }

                // 参数集可能分散在不同访问单元中。检查关键帧和仅含参数集的单元，将变化的 VPS/SPS/PPS 合并到此前的不可变配置快照。
                var frameData = frame.GetSpan();
                if (ctx.VideoConfig == null || frame.IsKeyFrame ||
                    VideoNalUnitHelper.HasParameterSetBeforeFirstVcl(frameData, frame.Codec))
                {
                    var update = TryExtractVideoConfig(frameData, frame.Codec);
                    if (update != null)
                        ctx.VideoConfig = MergeVideoConfig(ctx.VideoConfig, update, frame.Codec);
                }
            }
        }

        // 质量监控
        if (frame.Kind == MediaFrameKind.Video)
            ctx.QualityMonitor.OnVideoFrame(frame.IsKeyFrame, frame.TimestampMs);
        else
            ctx.QualityMonitor.OnAudioFrame();

        // 录制
        try
        {
            ctx.Recorder?.OnFrame(frame);
        }
        catch (Exception ex)
        {
            if (ShouldLog(ref ctx.LastRecorderErrorLogTick))
                _logger.LogError(ex, "媒体流录制器处理帧失败: StreamId={StreamId}, Channel={Channel}", streamId, channel);
        }

        // 后台音频处理：转写、降噪、WAV 录音。非阻塞，队列拥塞时丢帧保护主链路。
        ctx.AudioProcessor?.TryEnqueue(frame);

        // 多播到订阅者
        foreach (var kvp in ctx.Subscribers)
        {
            if (frame.TryRetain())
            {
                if (!kvp.Value.TryWrite(frame))
                    frame.Release();
            }
        }

        // 帧广播回调（用于 RTSP 等外部输出）
        // 注意：即使没有 FLV 订阅者，也必须调用此回调，以便 RTSP 等外部输出能收到帧
        InvokeFramePublishedCallback(frame, streamId, channel);
    }

    /// <summary>调用一个媒体发布回调并记录扩展错误。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    private void InvokeFramePublishedCallback(PooledMediaFrame frame, string streamId, string channel)
    {
        foreach (var item in Volatile.Read(ref _framePublishedHandlers))
        {
            try
            {
                ((Action<PooledMediaFrame, string, string>)item)(frame, streamId, channel);
            }
            catch (Exception ex)
            {
                if (ShouldLog(ref _lastFrameCallbackErrorLogTick))
                    _logger.LogError(ex, "媒体帧广播回调失败: StreamId={StreamId}, Channel={Channel}", streamId, channel);
            }
        }
    }

    /// <summary>按最小时间间隔判断是否需要再次输出警告。</summary>
    /// <param name="lastTick">上次处理时的单调时钟计数。</param>
    /// <returns>按最小时间间隔判断是否需要再次输出警告是否成立。</returns>
    private static bool ShouldLog(ref long lastTick)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref lastTick);
        return (previous == 0 || now - previous >= ExtensionErrorLogIntervalMs) &&
            Interlocked.CompareExchange(ref lastTick, now, previous) == previous;
    }

    /// <summary>
    /// 获取共享流上下文中已探测到的视频编码与参数集。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="videoConfig">视频编码参数集。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    public bool TryGetVideoInfo(string streamId, string channel, out MediaCodec codec, out byte[]? videoConfig)
    {
        var key = new MediaStreamKey(streamId, channel);
        if (_streams.TryGetValue(key, out var ctx))
        {
            lock (ctx.VideoConfigSync)
            {
                if (ctx.VideoCodec != MediaCodec.Unknown)
                {
                    codec = ctx.VideoCodec;
                    videoConfig = ctx.VideoConfig;
                    return true;
                }
            }
        }

        codec = MediaCodec.Unknown;
        videoConfig = null;
        return false;
    }

    /// <summary>尝试从视频帧中提取编码参数集。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>尝试从视频帧中提取编码参数集得到的byte[]?对象。</returns>
    private static byte[]? TryExtractVideoConfig(ReadOnlySpan<byte> data, MediaCodec codec)
    {
        if (codec is not (MediaCodec.H264 or MediaCodec.H265))
            return null;

        var selected = new List<(int Start, int Length)>(3);
        foreach (var nalu in SplitAnnexBNalus(data))
        {
            if (IsParameterSet(data.Slice(nalu.Start, nalu.Length), codec))
            {
                selected.Add(nalu);
            }
        }

        if (selected.Count == 0)
        {
            // 生产者可能提交单个原始 NAL；缓存时统一为 Annex-B 格式，让各 FLV 输出使用同一种配置格式。
            if (data.IsEmpty || !IsParameterSet(data, codec))
                return null;

            var normalized = new byte[checked(data.Length + 4)];
            normalized[3] = 1;
            data.CopyTo(normalized.AsSpan(4));
            return normalized;
        }

        var totalLength = 0;
        foreach (var nalu in selected)
        {
            totalLength += 4 + nalu.Length;
        }

        var buffer = new byte[totalLength];
        var offset = 0;
        foreach (var nalu in selected)
        {
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 1;
            data.Slice(nalu.Start, nalu.Length).CopyTo(buffer.AsSpan(offset));
            offset += nalu.Length;
        }

        return buffer;
    }

    /// <summary>把新参数集合并到已有的不可变配置快照。</summary>
    /// <param name="existing">现有的编码配置。</param>
    /// <param name="update">本次收到的编码配置。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>把新参数集合并到已有的不可变配置快照后的字节数据。</returns>
    private static byte[] MergeVideoConfig(byte[]? existing, byte[] update, MediaCodec codec)
    {
        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;

        if (existing != null)
            Collect(existing, overwrite: true);
        Collect(update, overwrite: true);

        int totalLength = (vps?.Length ?? 0) + (sps?.Length ?? 0) + (pps?.Length ?? 0);
        int setCount = (vps != null ? 1 : 0) + (sps != null ? 1 : 0) + (pps != null ? 1 : 0);
        if (setCount == 0)
            return update;

        var merged = new byte[checked(totalLength + setCount * 4)];
        int offset = 0;
        Append(vps);
        Append(sps);
        Append(pps);
        return merged;

        // <summary>从视频数据中收集参数集并按要求更新缓存。</summary>
        // <param name="source">源数据。</param>
        // <param name="overwrite">是否覆盖已缓存的参数集。</param>
        void Collect(ReadOnlySpan<byte> source, bool overwrite)
        {
            foreach (var item in SplitAnnexBNalus(source))
            {
                var nalu = source.Slice(item.Start, item.Length);
                if (nalu.IsEmpty)
                    continue;

                if (codec == MediaCodec.H264)
                {
                    int type = nalu[0] & 0x1F;
                    if (type == 7 && (overwrite || sps == null)) sps = nalu.ToArray();
                    else if (type == 8 && (overwrite || pps == null)) pps = nalu.ToArray();
                }
                else if (codec == MediaCodec.H265 && nalu.Length > 1)
                {
                    int type = (nalu[0] >> 1) & 0x3F;
                    if (type == 32 && (overwrite || vps == null)) vps = nalu.ToArray();
                    else if (type == 33 && (overwrite || sps == null)) sps = nalu.ToArray();
                    else if (type == 34 && (overwrite || pps == null)) pps = nalu.ToArray();
                }
            }
        }

        // <summary>将已存在的参数集追加到组合视频配置中。</summary>
        // <param name="parameterSet">视频参数集 NAL 单元。</param>
        void Append(byte[]? parameterSet)
        {
            if (parameterSet == null) return;
            merged[offset++] = 0;
            merged[offset++] = 0;
            merged[offset++] = 0;
            merged[offset++] = 1;
            parameterSet.CopyTo(merged, offset);
            offset += parameterSet.Length;
        }
    }

    /// <summary>
    /// 判断是否参数Set。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否参数Set的判定结果。</returns>
    private static bool IsParameterSet(ReadOnlySpan<byte> nalu, MediaCodec codec)
    {
        if (codec == MediaCodec.H264)
            return nalu.Length > 0 && ((nalu[0] & 0x1F) is 7 or 8);

        return nalu.Length > 1 && (((nalu[0] >> 1) & 0x3F) is 32 or 33 or 34);
    }

    /// <summary>按起始码把 Annex-B 数据拆分为 NAL 单元。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>按起始码把 Annex-B 数据拆分为 NAL 单元得到的条目集合。</returns>
    private static List<(int Start, int Length)> SplitAnnexBNalus(ReadOnlySpan<byte> data)
    {
        var result = new List<(int Start, int Length)>();
        var i = 0;

        while (i < data.Length)
        {
            if (i + 3 <= data.Length && data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    i += 3;
                }
                else if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    i += 4;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }

            var naluStart = i;
            i = AnnexBStartCode.Find(data, i, out _);
            if (i < 0) i = data.Length;

            if (i > naluStart)
                result.Add((naluStart, i - naluStart));
        }

        return result;
    }

    /// <summary>
    /// 推送媒体帧（自动 Release）。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    public void PublishAndRelease(PooledMediaFrame frame, string streamId, string channel)
    {
        try
        {
            Publish(frame, streamId, channel);
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>
    /// 标记流断开。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="reason">状态变化或失败原因。</param>
    /// <returns>标记流断开完成时结束的异步任务。</returns>
    public async ValueTask MarkStreamDisconnectedAsync(string streamId, string channel, string reason = "")
    {
        var key = new MediaStreamKey(streamId, channel);
        if (!_streams.TryGetValue(key, out var ctx)) return;

        if (!ctx.TryStopReceiving(out var duration)) return;

        // 停止质量监控定时器
        ctx.QualityMonitor.StopCheckTimer();
        ctx.QualityMonitor.Reset();

        // 停止录制
        var recorder = DetachRecorder(ctx);
        if (recorder != null)
        {
            try
            {
                var (path, size, recDuration) = recorder.Stop();
                await RaiseEventAsync(RecordStoppedAsync, handler =>
                    handler(this, new StreamRecordStoppedEventArgs
                    {
                        StreamId = streamId,
                        Channel = channel,
                        FilePath = path,
                        FileSize = size,
                        Duration = recDuration
                    })).ConfigureAwait(false);
            }
            finally
            {
                recorder.Dispose();
            }
        }

        await ctx.DisposeAudioProcessorAsync().ConfigureAwait(false);

        // 完成所有订阅者的写入
        foreach (var sub in ctx.Subscribers.Values)
            sub.Complete();

        await RaiseEventAsync(StreamDisconnectedAsync, handler =>
            handler(this, new StreamDisconnectedEventArgs
            {
                StreamId = streamId,
                Channel = channel,
                Source = ctx.Options.Source,
                Reason = reason,
                Duration = duration
            })).ConfigureAwait(false);

        _logger.LogInformation("流断开 [{Key}], 原因={Reason}, 持续={Duration}", key, reason, duration);
    }

    /// <summary>移除已释放订阅，并按需安排终端停止上传。</summary>
    /// <param name="subscription">媒体流订阅。</param>
    /// <param name="ctx">当前请求上下文。</param>
    private void OnSubscriptionDisposed(MediaStreamSubscription subscription, StreamContext ctx)
    {
        var key = subscription.Key;
        if (ctx.Subscribers.TryRemove(subscription.Id, out _))
        {
            int viewerCount = ctx.Subscribers.Count;
            _logger.LogInformation("取消订阅 [{Key}]，剩余订阅者: {Count}", key, viewerCount);

            _ = RaiseEventAsync(ViewerLeftAsync, handler =>
                handler(this, new StreamViewerLeftEventArgs
                {
                    StreamId = key.StreamId,
                    Channel = key.Channel,
                    SubscriberId = subscription.Id,
                    ViewerCount = viewerCount
                }));

            // 最后一个订阅者离开，启动自动断流计时
            if (viewerCount == 0 && ctx.Options.AutoStopWhenNoViewers)
            {
                _logger.LogInformation("流 [{Key}] 无订阅者，{Delay}s 后触发自动断流", key, ctx.Options.AutoStopDelaySeconds);
                ctx.StartAutoStopTimer(key, this);
            }
        }
    }

    /// <summary>调用媒体流事件订阅者并观察完成。</summary>
    /// <typeparam name="TArgs">类型参数 <c>TArgs</c>。</typeparam>
    /// <param name="evt">当前事件数据。</param>
    /// <param name="raise">事件通知委托。</param>
    /// <returns>调用媒体流事件订阅者并观察完成完成时结束的异步任务。</returns>
    internal async ValueTask RaiseEventAsync<TArgs>(Func<MediaStreamHub, TArgs, ValueTask>? evt,
        Func<Func<MediaStreamHub, TArgs, ValueTask>, ValueTask> raise)
    {
        if (evt == null) return;
        foreach (Func<MediaStreamHub, TArgs, ValueTask> handler in evt.GetInvocationList())
        {
            try
            {
                await raise(handler).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "事件处理异常");
            }
        }
    }

    /// <summary>自动断流定时器到期。</summary>
    /// <param name="key">查找或分组所用的键。</param>
    /// <returns>自动断流定时器到期完成时结束的异步任务。</returns>
    internal ValueTask OnAutoStopElapsed(MediaStreamKey key)
    {
        return RaiseEventAsync(StreamAutoStopRequiredAsync, handler =>
            handler(this, key));
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _streams)
        {
            var ctx = kvp.Value;
            ctx.TryStopReceiving(out _);
            foreach (var sub in ctx.Subscribers.Values)
                await sub.DisposeAsync().ConfigureAwait(false);

            ctx.StopAutoStopTimer();
            ctx.QualityMonitor.StopCheckTimer();

            StopAndDisposeRecorder(DetachRecorder(ctx));

            await ctx.DisposeAudioProcessorAsync().ConfigureAwait(false);
        }
        _streams.Clear();
    }
}

/// <summary>
/// 媒体流唯一标识。
/// </summary>
/// <param name="StreamId">流标识（如 SIM 号 / 设备 ID）。</param>
/// <param name="Channel">通道号。</param>
public readonly record struct MediaStreamKey(string StreamId, string Channel)
{
    /// <inheritdoc />
    /// <summary>将媒体流标识转换为用于路由的文本键。</summary>
    /// <returns>可用于媒体流索引或日志输出的文本表示。</returns>
    public override string ToString() => $"{StreamId}/{Channel}";
}

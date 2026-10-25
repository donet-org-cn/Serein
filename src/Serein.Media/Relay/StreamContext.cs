using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Serein.Media.Relay;

/// <summary>
/// 流上下文（内部使用，维护单条流的所有状态）。
/// </summary>
internal sealed class StreamContext
{
    /// <summary>
    /// 获取或设置 配置选项。
    /// </summary>
    public StreamOptions Options { get; set; }
    /// <summary>
    /// 获取 Subscribers。
    /// </summary>
    public ConcurrentDictionary<string, MediaStreamSubscription> Subscribers { get; } = new();
    /// <summary>
    /// 获取 是否Receiving。
    /// </summary>
    public bool IsReceiving => Volatile.Read(ref _stats) != null;
    /// <summary>
    /// 获取或设置 Connected时间。
    /// </summary>
    public DateTimeOffset ConnectedTime { get; set; }
    /// <summary>
    /// 获取 Connected参数。
    /// </summary>
    public StreamConnectedEventArgs ConnectedArgs { get; } = new();
    /// <summary>
    /// 获取或设置 视频Codec。
    /// </summary>
    public MediaCodec VideoCodec { get; set; } = MediaCodec.Unknown;
    /// <summary>
    /// 获取或设置 视频配置。
    /// </summary>
    public byte[]? VideoConfig { get; set; }
    /// <summary>
    /// 获取 视频配置Sync。
    /// </summary>
    public object VideoConfigSync { get; } = new();
    /// <summary>
    /// 获取或设置 Recorder。
    /// </summary>
    public IStreamRecorder? Recorder { get; set; }
    /// <summary>
    /// 获取 RecorderSync。
    /// </summary>
    public object RecorderSync { get; } = new();
    /// <summary>最近一次录制器错误日志的单调时刻。</summary>
    public long LastRecorderErrorLogTick;
    /// <summary>
    /// 获取或设置 音频Processor。
    /// </summary>
    public RtpServerAudioProcessor? AudioProcessor { get; set; }
    /// <summary>
    /// 获取 QualityMonitor。
    /// </summary>
    public StreamQualityMonitor QualityMonitor { get; }

    // 统计
    /// <summary>保护统计会话索引的互斥锁。</summary>
    private readonly object _statsSync = new();
    /// <summary>当前统计快照。</summary>
    private StatsSession? _stats;

    /// <summary>保存媒体流统计任务的取消与计数状态。</summary>
    private sealed class StatsSession
    {
        /// <summary>结束统计任务的取消源。</summary>
        public readonly CancellationTokenSource Cancellation = new();
        /// <summary>开始时间。</summary>
        public readonly long StartedAt = Stopwatch.GetTimestamp();
        /// <summary>累计总帧数、总字节数、视频帧数与丢包数。</summary>
        public long TotalFrames, TotalBytes, VideoFrames, PacketsLost;
    }

    // 自动断流定时器
    /// <summary>无订阅者时延迟停止媒体流的计时器。</summary>
    private Timer? _autoStopTimer;
    /// <summary>媒体流注册与订阅中心。</summary>
    private readonly MediaStreamHub _hub;

    /// <summary>
    /// 初始化 StreamContext 的新实例。
    /// </summary>
    /// <param name="options">配置选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    public StreamContext(StreamOptions options, ILogger logger, MediaStreamHub hub)
    {
        Options = options;
        _hub = hub;

        // 创建并绑定质量监控器（定时器在 MarkStreamConnected 时启动）
        QualityMonitor = new StreamQualityMonitor(logger);
        QualityMonitor.Bind(options.StreamId, options.Channel);
        QualityMonitor.StreamQualityIssueAsync += (monitor, args) =>
        {
            // 通过 hub 实例触发事件调用（不能直接在类外访问事件）
            return hub.OnStreamQualityIssue(args);
        };
    }

    /// <summary>
    /// 获取持续时间。
    /// </summary>
    /// <returns>获取持续时间得到的TimeSpan对象。</returns>
    public TimeSpan GetDuration() => Volatile.Read(ref _stats) is { } stats
        ? Stopwatch.GetElapsedTime(stats.StartedAt) : TimeSpan.Zero;

    /// <summary>以原子状态切换开始接收媒体数据。</summary>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    public bool TryStartReceiving()
    {
        lock (_statsSync)
        {
            if (_stats != null) return false;
            var stats = new StatsSession();
            ConnectedTime = DateTimeOffset.UtcNow;
            Volatile.Write(ref _stats, stats);
            _ = ReportStatsAsync(stats);
            return true;
        }
    }

    /// <summary>以原子状态切换停止接收媒体数据。</summary>
    /// <param name="duration">持续时间。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    public bool TryStopReceiving(out TimeSpan duration)
    {
        lock (_statsSync)
        {
            var stats = _stats;
            duration = stats == null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(stats.StartedAt);
            if (stats == null) return false;
            Volatile.Write(ref _stats, null);
            stats.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>生成并发送当前媒体流的统计更新。</summary>
    /// <param name="stats">当前统计快照。</param>
    /// <returns>生成并发送当前媒体流的统计更新完成时结束的异步任务。</returns>
    private async Task ReportStatsAsync(StatsSession stats)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long previousTick = stats.StartedAt, previousBytes = 0, previousVideoFrames = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stats.Cancellation.Token).ConfigureAwait(false))
            {
                if (!ReferenceEquals(Volatile.Read(ref _stats), stats)) return;
                long now = Stopwatch.GetTimestamp();
                long bytes = Interlocked.Read(ref stats.TotalBytes), videoFrames = Interlocked.Read(ref stats.VideoFrames);
                double seconds = Stopwatch.GetElapsedTime(previousTick, now).TotalSeconds;
                var args = new StreamStatsUpdatedEventArgs
                {
                    StreamId = Options.StreamId, Channel = Options.Channel,
                    TotalFrames = Interlocked.Read(ref stats.TotalFrames), TotalBytes = bytes,
                    PacketsLost = Interlocked.Read(ref stats.PacketsLost),
                    CurrentFps = (videoFrames - previousVideoFrames) / seconds,
                    CurrentBitrate = (long)((bytes - previousBytes) * 8.0 / seconds),
                    Duration = Stopwatch.GetElapsedTime(stats.StartedAt, now)
                };
                previousTick = now; previousBytes = bytes; previousVideoFrames = videoFrames;
                // 等待订阅者处理完成，避免慢消费者产生无限增长的后台任务。
                await _hub.OnStreamStatsUpdated(this, args).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stats.Cancellation.IsCancellationRequested) { }
        finally { lock (_statsSync) stats.Cancellation.Dispose(); }
    }

    /// <summary>处理媒体丢包通知，并更新关联状态或转发事件。</summary>
    /// <param name="lostCount">丢失的包数量。</param>
    public void OnPacketLoss(int lostCount)
    {
        if (lostCount > 0 && Volatile.Read(ref _stats) is { } stats)
            Interlocked.Add(ref stats.PacketsLost, lostCount);
    }

    /// <summary>处理媒体帧接收通知，并更新关联状态或转发事件。</summary>
    /// <param name="frame">媒体帧。</param>
    public void OnFrameReceived(PooledMediaFrame frame)
    {
        if (Volatile.Read(ref _stats) is not { } stats) return;
        Interlocked.Increment(ref stats.TotalFrames);
        Interlocked.Add(ref stats.TotalBytes, frame.Length);
        if (frame.Kind == MediaFrameKind.Video) Interlocked.Increment(ref stats.VideoFrames);
    }

    /// <summary>
    /// 启动AutoStopTimer。
    /// </summary>
    /// <param name="key">查找或分组所用的键。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    public void StartAutoStopTimer(MediaStreamKey key, MediaStreamHub hub)
    {
        StopAutoStopTimer();
        int delayMs = Math.Max(Options.AutoStopDelaySeconds, 1) * 1000;

        _autoStopTimer = new Timer(_ =>
        {
            if (Subscribers.IsEmpty)
            {
                _ = hub.OnAutoStopElapsed(key);
            }
        }, null, delayMs, Timeout.Infinite);
    }

    /// <summary>
    /// 停止AutoStopTimer。
    /// </summary>
    public void StopAutoStopTimer()
    {
        _autoStopTimer?.Dispose();
        _autoStopTimer = null;
    }

    /// <summary>为新的媒体生命周期重建音频处理器。</summary>
    public void ResetAudioProcessor()
    {
        var processor = AudioProcessor;
        AudioProcessor = null;
        if (processor != null)
            _ = processor.DisposeAsync();
    }

    /// <summary>等待音频处理器完成并释放它。</summary>
    /// <returns>等待音频处理器完成并释放它完成时结束的异步任务。</returns>
    public async ValueTask DisposeAudioProcessorAsync()
    {
        var processor = AudioProcessor;
        AudioProcessor = null;
        if (processor != null)
            await processor.DisposeAsync().ConfigureAwait(false);
    }
}

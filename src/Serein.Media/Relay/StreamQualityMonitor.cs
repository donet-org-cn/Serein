using Microsoft.Extensions.Logging;

namespace Serein.Media.Relay;

/// <summary>
/// 流质量监控器。
/// <para>
/// 挂载到 <see cref="MediaStreamHub"/> 或 <see cref="Serein.Media.Server.PsRtpReceiver"/>，
/// 通过帧间隔、关键帧频率、丢包率、PTS 连续性等指标检测异常。
/// </para>
/// </summary>
public sealed class StreamQualityMonitor
{
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 流质量异常事件。
    /// </summary>
    public event Func<StreamQualityMonitor, StreamQualityEventArgs, ValueTask>? StreamQualityIssueAsync;

    /// <summary>
    /// 初始化 StreamQualityMonitor 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public StreamQualityMonitor(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamQualityMonitor>.Instance;
    }

    #region 可调阈值

    /// <summary>画面冻结判定阈值（秒）。超过此时间无视频帧视为冻结。默认 3 秒。</summary>
    public double FreezeThresholdSeconds { get; set; } = 3.0;

    /// <summary>帧率过低判定阈值（FPS）。低于此帧率触发警告。默认 5 FPS。</summary>
    public double LowFpsThreshold { get; set; } = 5.0;

    /// <summary>帧率统计窗口（秒）。默认 5 秒。</summary>
    public double FpsWindowSeconds { get; set; } = 5.0;

    /// <summary>关键帧超时阈值（秒）。超过此时间无关键帧触发警告。默认 10 秒。</summary>
    public double KeyFrameTimeoutSeconds { get; set; } = 10.0;

    /// <summary>单次丢包数警告阈值。默认 10。</summary>
    public int PacketLossWarningThreshold { get; set; } = 10;

    /// <summary>单次丢包数严重阈值。默认 50。</summary>
    public int PacketLossCriticalThreshold { get; set; } = 50;

    /// <summary>PTS 跳变阈值（毫秒）。超过此值视为时间戳异常。默认 1000ms。</summary>
    public long TimestampJumpThresholdMs { get; set; } = 1000;

    /// <summary>花屏检测灵敏度。每 N 个视频帧检测一次 GOP 完整性。默认 25（约每秒一次@25fps）。</summary>
    public int ArtifactCheckInterval { get; set; } = 25;

    /// <summary>流超时阈值（秒）。超过此时间无任何帧数据触发 StreamTimeout。默认 5 秒。</summary>
    public double StreamTimeoutSeconds { get; set; } = 5.0;

    #endregion

    #region 内部状态

    /// <summary>媒体流标识。</summary>
    private string? _streamId;
    /// <summary>承载订阅数据的异步通道。</summary>
    private string? _channel;

    // 帧率统计
    /// <summary>累计视频帧数量。</summary>
    private long _videoFrameCount;
    /// <summary>上次计算帧率的单调时刻。</summary>
    private long _lastFpsCheckTicks;
    /// <summary>当前帧率统计窗口中的帧数。</summary>
    private long _framesInWindow;
    /// <summary>最近计算得到的每秒帧数。</summary>
    private double _currentFps;

    // 冻结检测
    /// <summary>最近一次收到媒体帧的单调时刻。</summary>
    private long _lastFrameTicks;
    /// <summary>最近一次收到视频帧的单调时刻。</summary>
    private long _lastVideoFrameTicks;
    /// <summary>本轮画面冻结问题是否已经通知。</summary>
    private bool _freezeReported;
    /// <summary>本轮媒体流超时是否已经通知。</summary>
    private bool _streamTimeoutReported;

    // 关键帧检测
    /// <summary>最近一次收到关键帧的单调时刻。</summary>
    private long _lastKeyFrameTicks;
    /// <summary>本轮关键帧超时是否已经通知。</summary>
    private bool _keyFrameTimeoutReported;

    // PTS 检测
    /// <summary>上一视频帧的显示时间戳，单位为毫秒。</summary>
    private long _lastVideoPtsMs;
    /// <summary>是否已经记录上一帧时间戳。</summary>
    private bool _hasLastPts;

    // 花屏检测（基于 GOP 完整性）
    /// <summary>自上一关键帧以来收到的视频帧数。</summary>
    private int _framesSinceLastKeyFrame;
    /// <summary>当前流是否已经收到过关键帧。</summary>
    private bool _hasSeenKeyFrame;

    // 丢包
    /// <summary>累计检测到的 RTP 丢包数量。</summary>
    private long _totalPacketLoss;

    // 定时检查
    /// <summary>周期检查媒体质量的计时器。</summary>
    private Timer? _checkTimer;

    // 质量事件去重（同类型事件最小间隔）
    /// <summary>按问题类型保存的最近通知时刻。</summary>
    private readonly Dictionary<StreamQualityIssue, long> _lastIssueTicks = new();
    /// <summary>保护媒体质量问题记录的互斥锁。</summary>
    private readonly object _issueSync = new();

    #endregion

    /// <summary>
    /// 绑定到指定流。在流开始时调用。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    public void Bind(string streamId, string channel)
    {
        _streamId = streamId;
        _channel = channel;
        Reset();
    }

    /// <summary>
    /// 重置所有统计状态。
    /// </summary>
    public void Reset()
    {
        _videoFrameCount = 0;
        _lastFpsCheckTicks = Environment.TickCount64;
        _framesInWindow = 0;
        _currentFps = 0;
        _lastFrameTicks = 0;
        _lastVideoFrameTicks = 0;
        _freezeReported = false;
        _streamTimeoutReported = false;
        _lastKeyFrameTicks = 0;
        _keyFrameTimeoutReported = false;
        _lastVideoPtsMs = 0;
        _hasLastPts = false;
        _framesSinceLastKeyFrame = 0;
        _hasSeenKeyFrame = false;
        _totalPacketLoss = 0;
        lock (_issueSync) _lastIssueTicks.Clear();
    }

    /// <summary>
    /// 启动定时质量检查。
    /// </summary>
    /// <param name="intervalMs">统计间隔，单位为毫秒。</param>
    public void StartCheckTimer(int intervalMs = 1000)
    {
        StopCheckTimer();
        _checkTimer = new Timer(_ => PerformPeriodicCheck(), null, intervalMs, intervalMs);
    }

    /// <summary>
    /// 停止定时质量检查。
    /// </summary>
    public void StopCheckTimer()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    /// <summary>
    /// 通知收到视频帧。应在每一帧视频数据到达时调用。
    /// </summary>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="timestampMs">时间戳，单位为毫秒。</param>
    public void OnVideoFrame(bool isKeyFrame, long timestampMs)
    {
        var now = Environment.TickCount64;
        _videoFrameCount++;
        _framesInWindow++;
        _lastFrameTicks = now;
        _lastVideoFrameTicks = now;

        if (_streamTimeoutReported)
        {
            _logger.LogDebug("[流质量] 流超时恢复: {Stream}/{Channel}", _streamId, _channel);
            _streamTimeoutReported = false;
        }

        // 冻结恢复
        if (_freezeReported)
        {
            _logger.LogDebug("[流质量] 冻结恢复: {Stream}/{Channel}", _streamId, _channel);
            _freezeReported = false;
        }

        // 关键帧跟踪
        if (isKeyFrame)
        {
            _lastKeyFrameTicks = now;
            _keyFrameTimeoutReported = false;

            // 花屏检测：检查上次 GOP 的完整性
            if (_hasSeenKeyFrame && _framesSinceLastKeyFrame > 0)
            {
                CheckArtifactConsistency(_framesSinceLastKeyFrame);
            }
            _framesSinceLastKeyFrame = 0;
            _hasSeenKeyFrame = true;
        }
        else
        {
            _framesSinceLastKeyFrame++;
        }

        // 帧率计算（Environment.TickCount64 单位为 ms）
        long windowMs = (long)(FpsWindowSeconds * 1000);
        if (now - _lastFpsCheckTicks >= windowMs)
        {
            double elapsedSec = (now - _lastFpsCheckTicks) / 1000.0;
            _currentFps = _framesInWindow / elapsedSec;
            _framesInWindow = 0;
            _lastFpsCheckTicks = now;

            // 帧率过低检测
            if (_currentFps > 0 && _currentFps < LowFpsThreshold)
            {
                RaiseIssue(StreamQualityIssue.LowFrameRate, StreamQualitySeverity.Warning,
                    $"帧率过低: {_currentFps:F1} FPS (阈值 {LowFpsThreshold} FPS)", _currentFps);
            }
        }

        // PTS 跳变检测
        if (_hasLastPts)
        {
            long diff = Math.Abs(timestampMs - _lastVideoPtsMs);
            if (diff > TimestampJumpThresholdMs)
            {
                RaiseIssue(StreamQualityIssue.TimestampJump, StreamQualitySeverity.Warning,
                    $"PTS 跳变: {diff}ms (从 {_lastVideoPtsMs} 跳到 {timestampMs})", diff);
            }
        }
        _lastVideoPtsMs = timestampMs;
        _hasLastPts = true;
    }

    /// <summary>
    /// 通知收到音频帧。
    /// </summary>
    public void OnAudioFrame()
    {
        _lastFrameTicks = Environment.TickCount64;
        _streamTimeoutReported = false;
    }

    /// <summary>
    /// 通知 RTP 丢包。
    /// </summary>
    /// <param name="lostCount">丢失的包数量。</param>
    public void OnPacketLoss(int lostCount)
    {
        _totalPacketLoss += lostCount;

        var severity = lostCount >= PacketLossCriticalThreshold
            ? StreamQualitySeverity.Critical
            : StreamQualitySeverity.Warning;

        if (lostCount >= PacketLossWarningThreshold)
        {
            RaiseIssue(StreamQualityIssue.PacketLoss, severity,
                $"丢包: {lostCount} 包 (累计 {_totalPacketLoss})", lostCount);
        }
    }

    /// <summary>
    /// 定时质量检查（冻结、关键帧超时、流超时）。
    /// </summary>
    private void PerformPeriodicCheck()
    {
        if (_streamId == null) return;

        var now = Environment.TickCount64;

        // === 冻结检测 ===
        if (_lastVideoFrameTicks > 0 && !_freezeReported)
        {
            double frozenSeconds = (now - _lastVideoFrameTicks) / 1000.0;
            if (frozenSeconds >= FreezeThresholdSeconds)
            {
                _freezeReported = true;
                RaiseIssue(StreamQualityIssue.Freeze, StreamQualitySeverity.Warning,
                    $"画面冻结: 已 {frozenSeconds:F1} 秒无视频帧 (阈值 {FreezeThresholdSeconds}s)", frozenSeconds);
            }
        }

        // === 关键帧超时检测 ===
        if (_hasSeenKeyFrame && !_keyFrameTimeoutReported)
        {
            double sinceLastKey = (now - _lastKeyFrameTicks) / 1000.0;
            if (sinceLastKey >= KeyFrameTimeoutSeconds)
            {
                _keyFrameTimeoutReported = true;
                RaiseIssue(StreamQualityIssue.KeyFrameTimeout, StreamQualitySeverity.Warning,
                    $"关键帧超时: 已 {sinceLastKey:F1}s 未收到 IDR (阈值 {KeyFrameTimeoutSeconds}s)", sinceLastKey);
            }
        }

        // === 流超时检测 ===
        if (_lastFrameTicks > 0 && !_streamTimeoutReported)
        {
            double timeout = (now - _lastFrameTicks) / 1000.0;
            if (timeout >= StreamTimeoutSeconds)
            {
                _streamTimeoutReported = true;
                RaiseIssue(StreamQualityIssue.StreamTimeout, StreamQualitySeverity.Critical,
                    $"流超时: 已 {timeout:F1}s 无数据 (阈值 {StreamTimeoutSeconds}s)", timeout);
            }
        }
    }

    /// <summary>
    /// 花屏检测：基于 P 帧数量与关键帧间隔的异常判断。
    /// </summary>
    /// <param name="pFrameCount">非关键视频帧数量。</param>
    private void CheckArtifactConsistency(int pFrameCount)
    {
        if (pFrameCount > 300)
        {
            RaiseIssue(StreamQualityIssue.Artifact, StreamQualitySeverity.Warning,
                $"GOP 过长 ({pFrameCount} P帧)，丢包后极易花屏", pFrameCount);
        }

        if (_totalPacketLoss > 0 && pFrameCount > 50)
        {
            RaiseIssue(StreamQualityIssue.Artifact, StreamQualitySeverity.Info,
                $"GOP 内存在丢包 ({_totalPacketLoss} 累计)，可能有花屏", _totalPacketLoss);
        }
    }

    /// <summary>
    /// 抛出质量事件（带去重）。
    /// </summary>
    /// <param name="issue">检测到的媒体质量问题。</param>
    /// <param name="severity">问题严重程度。</param>
    /// <param name="message">待处理的消息。</param>
    /// <param name="value">当前读取、写入或检查的值。</param>
    private void RaiseIssue(StreamQualityIssue issue, StreamQualitySeverity severity, string message, double? value)
    {
        var now = Environment.TickCount64;

        lock (_issueSync)
        {
            if (_lastIssueTicks.TryGetValue(issue, out var lastTicks))
            {
                double elapsed = (now - lastTicks) / 1000.0;
                if (elapsed < 2.0) return;
            }
            _lastIssueTicks[issue] = now;
        }
        // 帧处理、计时器和重置线程共享限频表；日志及外部回调放在短暂的原子检查更新区间之外。

        var args = new StreamQualityEventArgs
        {
            StreamId = _streamId ?? string.Empty,
            Channel = _channel ?? string.Empty,
            Issue = issue,
            Severity = severity,
            Message = message,
            Value = value,
            DetectedAt = DateTimeOffset.UtcNow,
        };

        _logger.Log(severity == StreamQualitySeverity.Critical ? LogLevel.Error :
                    severity == StreamQualitySeverity.Warning ? LogLevel.Warning :
                    LogLevel.Information,
            "[流质量] {Issue}: {Message} ({Stream}/{Channel})",
            issue, message, _streamId, _channel);

        var handler = StreamQualityIssueAsync;
        if (handler != null)
        {
            foreach (var h in handler.GetInvocationList())
            {
                try
                {
                    _ = ((Func<StreamQualityMonitor, StreamQualityEventArgs, ValueTask>)h)(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "流质量事件处理异常");
                }
            }
        }
    }
}

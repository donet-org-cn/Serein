namespace Serein.Media.Relay;

/// <summary>
/// 流质量问题的严重程度。
/// </summary>
public enum StreamQualitySeverity
{
    /// <summary>提示级别，短暂异常可自动恢复。</summary>
    Info,

    /// <summary>警告级别，可能影响观看体验。</summary>
    Warning,

    /// <summary>严重级别，画面已不可用。</summary>
    Critical,
}

/// <summary>
/// 流质量问题类型。
/// </summary>
public enum StreamQualityIssue
{
    /// <summary>画面冻结 — 视频帧停止更新。</summary>
    Freeze,

    /// <summary>画面花屏/马赛克 — 数据损坏导致解码异常。</summary>
    Artifact,

    /// <summary>严重丢包 — RTP 序列号间隙过大。</summary>
    PacketLoss,

    /// <summary>帧率过低 — 视频帧率远低于预期。</summary>
    LowFrameRate,

    /// <summary>时间戳跳变 — PTS 不连续，可能导致播放卡顿。</summary>
    TimestampJump,

    /// <summary>长时间未收到关键帧 — 无法刷新画面。</summary>
    KeyFrameTimeout,

    /// <summary>流断开 — 超过阈值时间未收到任何数据。</summary>
    StreamTimeout,
}

/// <summary>
/// 流质量异常事件参数。
/// </summary>
public sealed class StreamQualityEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>质量问题类型。</summary>
    public StreamQualityIssue Issue { get; init; }

    /// <summary>严重程度。</summary>
    public StreamQualitySeverity Severity { get; init; }

    /// <summary>问题描述。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>附带数据（如丢包数、冻结时长、帧率等）。</summary>
    public double? Value { get; init; }

    /// <summary>检测时间。</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

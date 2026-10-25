namespace Serein.Media.Relay;

/// <summary>
/// 流统计信息更新事件参数。
/// </summary>
public sealed class StreamStatsUpdatedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>已接收总帧数。</summary>
    public long TotalFrames { get; init; }

    /// <summary>已接收总字节数。</summary>
    public long TotalBytes { get; init; }

    /// <summary>丢包数。</summary>
    public long PacketsLost { get; init; }

    /// <summary>当前帧率（FPS）。</summary>
    public double CurrentFps { get; init; }

    /// <summary>当前码率（bps）。</summary>
    public long CurrentBitrate { get; init; }

    /// <summary>流持续时间。</summary>
    public TimeSpan Duration { get; init; }
}

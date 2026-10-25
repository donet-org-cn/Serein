namespace Serein.Media.Relay;

/// <summary>
/// 流录制停止事件参数。
/// </summary>
public sealed class StreamRecordStoppedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>录制文件路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>录制文件大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>录制持续时间。</summary>
    public TimeSpan Duration { get; init; }
}

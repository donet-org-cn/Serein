namespace Serein.Media.Relay;

/// <summary>
/// 流录制开始事件参数。
/// </summary>
public sealed class StreamRecordStartedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>录制文件路径。</summary>
    public string FilePath { get; init; } = string.Empty;
}

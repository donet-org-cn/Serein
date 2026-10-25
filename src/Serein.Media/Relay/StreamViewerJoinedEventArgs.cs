namespace Serein.Media.Relay;

/// <summary>
/// 新订阅者（观看者）加入事件参数。
/// </summary>
public sealed class StreamViewerJoinedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>订阅者 ID。</summary>
    public string SubscriberId { get; init; } = string.Empty;

    /// <summary>当前总订阅者数。</summary>
    public int ViewerCount { get; init; }

    /// <summary>订阅者来源描述（如 IP 地址、HTTP 路径等）。</summary>
    public string? SubscriberDescription { get; init; }
}

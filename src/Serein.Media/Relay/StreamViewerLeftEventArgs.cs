namespace Serein.Media.Relay;

/// <summary>
/// 订阅者（观看者）离开事件参数。
/// </summary>
public sealed class StreamViewerLeftEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>订阅者 ID。</summary>
    public string SubscriberId { get; init; } = string.Empty;

    /// <summary>当前剩余订阅者数。</summary>
    public int ViewerCount { get; init; }

    /// <summary>是否已无订阅者。</summary>
    public bool IsLastViewer => ViewerCount == 0;
}

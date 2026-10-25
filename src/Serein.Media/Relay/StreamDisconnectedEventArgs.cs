namespace Serein.Media.Relay;

/// <summary>
/// 流断开事件参数。
/// </summary>
public sealed class StreamDisconnectedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>流来源协议。</summary>
    public StreamSource Source { get; init; }

    /// <summary>断开原因。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>流持续时间。</summary>
    public TimeSpan Duration { get; init; }
}

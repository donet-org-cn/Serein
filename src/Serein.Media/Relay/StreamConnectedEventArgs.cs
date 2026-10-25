using System.Net;

namespace Serein.Media.Relay;

/// <summary>
/// 流连接成功事件参数。
/// </summary>
public sealed class StreamConnectedEventArgs : EventArgs
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>流来源协议。</summary>
    public StreamSource Source { get; init; }

    /// <summary>远程终结点。</summary>
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>流的编码信息（首个帧确定后更新）。</summary>
    public MediaCodec? VideoCodec { get; internal set; }

    /// <summary>音频编码。</summary>
    public MediaCodec? AudioCodec { get; internal set; }
}

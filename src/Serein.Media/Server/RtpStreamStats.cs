namespace Serein.Media.Server;

/// <summary>
/// RTP 流统计信息。
/// </summary>
public sealed class RtpStreamStats
{
    /// <summary>已接收 RTP 包数。</summary>
    public long PacketsReceived { get; set; }

    /// <summary>已接收字节数。</summary>
    public long BytesReceived { get; set; }

    /// <summary>丢包数（通过序列号间隙估算）。</summary>
    public long PacketsLost { get; set; }

    /// <summary>乱序包数。</summary>
    public long PacketsOutOfOrder { get; set; }

    /// <summary>最后收到包的时间（TickCount64）。</summary>
    public long LastPacketTime { get; set; }

    /// <summary>最后一个 RTP 序列号。</summary>
    public ushort LastSequenceNumber { get; set; }
}

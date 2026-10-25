namespace Serein.Media.Protocol;

/// <summary>
/// RTP 包构建工具。
/// <para>调用方提供目标 Span，方法只写入 RTP 头和 payload，不分配临时数组。</para>
/// </summary>
public static class RtpPacketBuilder
{
    /// <summary>
    /// 计算 RTP 包总长度。
    /// </summary>
    /// <param name="payloadLength">有效负载的字节数。</param>
    /// <returns>计算 RTP 包总长度计算得到的数值。</returns>
    public static int GetPacketLength(int payloadLength) => StandardRtpHeader.HeaderLength + payloadLength;

    /// <summary>
    /// 写入 RTP 包。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="header">RTP 头。</param>
    /// <param name="payload">负载。</param>
    /// <returns>写入的 RTP 包长度。</returns>
    public static int WritePacket(Span<byte> destination, StandardRtpHeader header, ReadOnlySpan<byte> payload)
    {
        var length = GetPacketLength(payload.Length);
        if (destination.Length < length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));

        header.WriteTo(destination);
        payload.CopyTo(destination[StandardRtpHeader.HeaderLength..]);
        return length;
    }

    /// <summary>
    /// 创建标准 RTP 头。
    /// </summary>
    /// <param name="payloadType">RTP 负载类型。</param>
    /// <param name="sequenceNumber">包序列号。</param>
    /// <param name="timestamp">按媒体时钟计数的 RTP 时间戳。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    /// <returns>创建标准 RTP 头得到的StandardRtpHeader对象。</returns>
    public static StandardRtpHeader CreateHeader(
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        bool marker = false)
    {
        return new StandardRtpHeader
        {
            Version = 2,
            PayloadType = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
            Marker = marker,
        };
    }
}

using Serein.Media.Protocol;

namespace Serein.Media.Audio;

/// <summary>
/// RTP 音频包构建器。
/// </summary>
public static class RtpAudioPacketBuilder
{
    /// <summary>
    /// 构建一个标准 RTP 数据包。
    /// </summary>
    /// <param name="format">音频格式描述或格式编号。</param>
    /// <param name="sequenceNumber">包序列号。</param>
    /// <param name="timestamp">按媒体时钟计数的 RTP 时间戳。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    /// <returns>构建一个标准 RTP 数据包后的字节数据。</returns>
    public static byte[] Build(AudioFormat format, ushort sequenceNumber, uint timestamp, uint ssrc, ReadOnlySpan<byte> payload, bool marker = false)
    {
        var packet = new byte[StandardRtpHeader.HeaderLength + payload.Length];
        Write(format, sequenceNumber, timestamp, ssrc, payload, packet, marker);
        return packet;
    }

    /// <summary>将 RTP 包写入调用方缓冲区，返回实际长度；发送完成前不得复用缓冲区。</summary>
    /// <param name="format">音频格式描述或格式编号。</param>
    /// <param name="sequenceNumber">包序列号。</param>
    /// <param name="timestamp">按媒体时钟计数的 RTP 时间戳。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    /// <returns>实际写入的元素数量。</returns>
    public static int Write(AudioFormat format, ushort sequenceNumber, uint timestamp, uint ssrc,
        ReadOnlySpan<byte> payload, Span<byte> destination, bool marker = false)
    {
        int length = checked(StandardRtpHeader.HeaderLength + payload.Length);
        if (destination.Length < length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));
        var header = new StandardRtpHeader
        {
            Version = 2,
            Padding = false,
            Extension = false,
            CsrcCount = 0,
            Marker = marker,
            PayloadType = (byte)format.PayloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
        };

        header.WriteTo(destination);
        payload.CopyTo(destination[StandardRtpHeader.HeaderLength..]);
        return length;
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Serein.Media.Protocol
{
    /// <summary>
    /// 标准 RTP 头（RFC 3550），固定 12 字节。
    /// 用于 PS/RTP 等标准 RTP 协议。
    /// </summary>
    public readonly struct StandardRtpHeader
    {
        /// <summary>固定头长度。</summary>
        public const int HeaderLength = 12;

        /// <summary>版本号（固定为 2）。</summary>
        public byte Version { get; init; }

        /// <summary>填充标志。</summary>
        public bool Padding { get; init; }

        /// <summary>扩展标志。</summary>
        public bool Extension { get; init; }

        /// <summary>CSRC 计数。</summary>
        public byte CsrcCount { get; init; }

        /// <summary>标记位。</summary>
        public bool Marker { get; init; }

        /// <summary>负载类型。</summary>
        public byte PayloadType { get; init; }

        /// <summary>序列号。</summary>
        public ushort SequenceNumber { get; init; }

        /// <summary>时间戳。</summary>
        public uint Timestamp { get; init; }

        /// <summary>同步源标识。</summary>
        public uint Ssrc { get; init; }

        /// <summary>
        /// 从字节序列解析标准 RTP 头（12 字节）。
        /// </summary>
        /// <param name="data">待处理的数据。</param>
        /// <returns>从字节序列解析标准 RTP 头（12 字节）得到的StandardRtpHeader对象。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StandardRtpHeader Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < HeaderLength)
                throw new ArgumentOutOfRangeException(nameof(data), $"至少需要 {HeaderLength} 字节");

            byte b0 = data[0];
            byte b1 = data[1];

            return new StandardRtpHeader
            {
                Version = (byte)((b0 >> 6) & 0x03),
                Padding = ((b0 >> 5) & 0x01) != 0,
                Extension = ((b0 >> 4) & 0x01) != 0,
                CsrcCount = (byte)(b0 & 0x0F),
                Marker = ((b1 >> 7) & 0x01) != 0,
                PayloadType = (byte)(b1 & 0x7F),
                SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
                Timestamp = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
                Ssrc = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            };
        }

        /// <summary>
        /// 将头写入目标 Span（至少 12 字节）。
        /// </summary>
        /// <param name="destination">接收转换结果的目标缓冲区。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteTo(Span<byte> destination)
        {
            destination[0] = (byte)(((Version & 0x03) << 6)
                | ((Padding ? 1 : 0) << 5)
                | ((Extension ? 1 : 0) << 4)
                | (CsrcCount & 0x0F));
            destination[1] = (byte)(((Marker ? 1 : 0) << 7) | (PayloadType & 0x7F));
            BinaryPrimitives.WriteUInt16BigEndian(destination[2..], SequenceNumber);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(destination[8..], Ssrc);
        }

        /// <summary>
        /// 获取包含 CSRC 列表的总头长度。
        /// </summary>
        public int TotalHeaderLength => HeaderLength + CsrcCount * 4;

        /// <summary>
        /// 获取 RTP 负载区域，自动跳过 CSRC 列表和 RTP 扩展头，并剔除 padding。
        /// </summary>
        /// <param name="packet">协议数据包。</param>
        /// <param name="payload">媒体或协议消息的有效负载。</param>
        /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
        {
            payload = ReadOnlySpan<byte>.Empty;

            var payloadOffset = TotalHeaderLength;
            if (packet.Length < payloadOffset)
            {
                return false;
            }

            if (Extension)
            {
                if (packet.Length < payloadOffset + 4)
                {
                    return false;
                }

                var extensionLengthWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(payloadOffset + 2)..]);
                payloadOffset += 4 + extensionLengthWords * 4;
                if (packet.Length < payloadOffset)
                {
                    return false;
                }
            }

            var payloadLength = packet.Length - payloadOffset;
            if (Padding)
            {
                if (payloadLength == 0)
                {
                    return false;
                }

                var paddingLength = packet[^1];
                if (paddingLength == 0 || paddingLength > payloadLength)
                {
                    return false;
                }

                payloadLength -= paddingLength;
            }

            if (payloadLength <= 0)
            {
                return false;
            }

            payload = packet.Slice(payloadOffset, payloadLength);
            return true;
        }
    }
}

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Serein.Media.Protocol;

/// <summary>
/// PS 解析出的 PES 帧。
/// </summary>
public sealed class PsPesFrame : IDisposable
{
    /// <summary>待处理的数据。</summary>
    private byte[] _data = Array.Empty<byte>();
    /// <summary>本次处理的数据长度。</summary>
    private int _length;

    /// <summary>帧类型。</summary>
    public MediaFrameKind Kind { get; init; }

    /// <summary>编码格式。</summary>
    public MediaCodec Codec { get; init; }

    /// <summary>PTS 时间戳（90kHz 时钟）。</summary>
    public long Timestamp90kHz { get; init; }

    /// <summary>ES 数据。</summary>
    public byte[] Data => _data;

    /// <summary>ES 数据有效长度。</summary>
    public int Length => _length;

    /// <summary>租用缓冲区并复制 ES 数据。</summary>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp90kHz">以 90 kHz 时钟计数的时间戳。</param>
    /// <param name="data">待处理的数据。</param>
    /// <returns>租用缓冲区并复制 ES 数据得到的PsPesFrame对象。</returns>
    public static PsPesFrame Create(MediaFrameKind kind, MediaCodec codec, long timestamp90kHz, ReadOnlySpan<byte> data)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        return TakeOwnership(kind, codec, timestamp90kHz, buffer, data.Length);
    }

    /// <summary>接管已租用的 ES 数据缓冲区。</summary>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp90kHz">以 90 kHz 时钟计数的时间戳。</param>
    /// <param name="rentedBuffer">从缓冲池租用的数据缓冲区。</param>
    /// <param name="length">数据长度或数量。</param>
    /// <returns>接管已租用的 ES 数据缓冲区得到的PsPesFrame对象。</returns>
    public static PsPesFrame TakeOwnership(MediaFrameKind kind, MediaCodec codec, long timestamp90kHz, byte[] rentedBuffer, int length)
    {
        return new PsPesFrame
        {
            Kind = kind,
            Codec = codec,
            Timestamp90kHz = timestamp90kHz,
            _data = rentedBuffer,
            _length = length
        };
    }

    /// <summary>获取 ES 数据。</summary>
    /// <returns>与本次提取或读取范围对应的字节数据。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetSpan() => _data.AsSpan(0, _length);

    /// <summary>转移底层缓冲区所有权，调用成功后本对象不再负责归还。</summary>
    /// <param name="buffer">数据缓冲区。</param>
    /// <param name="length">数据长度或数量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    public bool TryTakeOwnership(out byte[] buffer, out int length)
    {
        buffer = _data;
        length = _length;
        if (length <= 0 || buffer.Length == 0)
            return false;

        _data = Array.Empty<byte>();
        _length = 0;
        return true;
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        var buffer = _data;
        _data = Array.Empty<byte>();
        _length = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}

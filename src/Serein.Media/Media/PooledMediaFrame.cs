using System.Buffers;
using System.Runtime.CompilerServices;

namespace Serein.Media;

/// <summary>
/// 基于 <see cref="ArrayPool{T}"/> 的高性能零拷贝媒体帧。
/// <para>
/// 使用引用计数管理生命周期，底层字节数组来自共享池，避免 GC 压力。
/// <b>使用后必须调用 <see cref="Release"/> 归还缓冲区。</b>
/// </para>
/// </summary>
public sealed class PooledMediaFrame : IMediaFrame, IDisposable
{
    /// <summary>读写字节数据的缓冲区。</summary>
    private byte[] _buffer;
    /// <summary>本次处理的数据长度。</summary>
    private int _length;
    /// <summary>池化帧当前持有的引用数量。</summary>
    private int _refCount;

    /// <summary>帧类型。</summary>
    public MediaFrameKind Kind { get; init; }

    /// <summary>视频/音频编码格式。</summary>
    public MediaCodec Codec { get; init; }

    /// <summary>是否关键帧。</summary>
    public bool IsKeyFrame { get; init; }

    /// <summary>时间戳（毫秒）。</summary>
    public long TimestampMs { get; init; }

    /// <summary>帧数据长度。</summary>
    public int Length => _length;

    /// <summary>是否已释放。</summary>
    public bool IsReleased => _refCount <= 0;

    /// <summary>
    /// 初始化 PooledMediaFrame 的新实例。
    /// </summary>
    /// <param name="buffer">数据缓冲区。</param>
    /// <param name="length">数据长度或数量。</param>
    private PooledMediaFrame(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
        _refCount = 1;
    }

    /// <summary>
    /// 从源 Span 租用缓冲区并创建帧（单次拷贝）。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="timestampMs">时间戳，单位为毫秒。</param>
    /// <returns>拥有一个初始引用的池化媒体帧，调用方负责归还该引用。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledMediaFrame Create(ReadOnlySpan<byte> data, MediaFrameKind kind,
        MediaCodec codec, bool isKeyFrame, long timestampMs)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        return new PooledMediaFrame(buffer, data.Length)
        {
            Kind = kind,
            Codec = codec,
            IsKeyFrame = isKeyFrame,
            TimestampMs = timestampMs
        };
    }

    /// <summary>
    /// 直接接管已租用的缓冲区（零拷贝）。
    /// <para>调用者不再拥有此 buffer 的所有权。</para>
    /// </summary>
    /// <param name="rentedBuffer">从缓冲池租用的数据缓冲区。</param>
    /// <param name="length">数据长度或数量。</param>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="timestampMs">时间戳，单位为毫秒。</param>
    /// <returns>直接接管已租用的缓冲区（零拷贝）。 调用者不再拥有此 buffer 的所有权得到的PooledMediaFrame对象。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledMediaFrame TakeOwnership(byte[] rentedBuffer, int length,
        MediaFrameKind kind, MediaCodec codec, bool isKeyFrame, long timestampMs)
    {
        return new PooledMediaFrame(rentedBuffer, length)
        {
            Kind = kind,
            Codec = codec,
            IsKeyFrame = isKeyFrame,
            TimestampMs = timestampMs
        };
    }

    /// <summary>
    /// 获取帧数据的只读 Span。仅在帧未释放时有效。
    /// </summary>
    /// <returns>与本次提取或读取范围对应的字节数据。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetSpan() =>
        _buffer.AsSpan(0, _length);

    /// <summary>
    /// 获取帧数据的只读 Memory。仅在帧未释放时有效。
    /// </summary>
    /// <returns>与本次提取或读取范围对应的字节数据。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMemory<byte> GetMemory() =>
        _buffer.AsMemory(0, _length);

    /// <summary>
    /// 增加引用计数。返回 false 表示帧已释放。
    /// </summary>
    /// <returns>帧仍有效且成功增加引用时为 true；已经释放时为 false。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRetain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            if (current <= 0) return false;
        }
        while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);
        return true;
    }

    /// <summary>
    /// 释放引用。引用计数归零时归还缓冲区到池。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            var buffer = _buffer;
            _buffer = null!;
            _length = 0;
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    void IDisposable.Dispose() => Release();
}

using Serein.Media.Protocol;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Serein.Media.Server;

/// <summary>在有界缓存内从连续 RTP 负载重组 MPEG-PS 媒体帧。</summary>
internal sealed class PsReassembler
{
    /// <summary>重组缓冲区的初始字节容量。</summary>
    private const int InitialBufferSize = 64 * 1024;
    /// <summary>
    /// 获取 Pack起始字节序列。
    /// </summary>
    private static ReadOnlySpan<byte> PackStartBytes => [0x00, 0x00, 0x01, 0xBA];

    /// <summary>读写字节数据的缓冲区。</summary>
    private byte[]? _buffer;
    /// <summary>本次处理的数据长度。</summary>
    private int _length;
    /// <summary>缓存的字节容量上限。</summary>
    private readonly int _maxBufferSize;
    /// <summary>复用的本轮解析帧集合。</summary>
    private List<PsPesFrame>? _reusableFrames;
    /// <summary>当前媒体流识别或协商的视频编码。</summary>
    private MediaCodec _videoCodec;
    /// <summary>由 PS 流映射表识别的音频流编码。</summary>
    private readonly Dictionary<byte, MediaCodec> _audioCodecs = new();
    /// <summary>是否正在等待新的 PS 包起点。</summary>
    private bool _waitingForPackStart;
    /// <summary>跨 RTP 包保留的 PS 起始码匹配长度。</summary>
    private int _packStartMatchLength;
    /// <summary>当前媒体重组是否检测到不连续。</summary>
    private bool _discontinuity;

    /// <summary>
    /// 初始化 PsReassembler 的新实例。
    /// </summary>
    /// <param name="maxBufferSize">缓存的字节容量上限。</param>
    public PsReassembler(int maxBufferSize = 4 * 1024 * 1024)
    {
        _maxBufferSize = Math.Max(InitialBufferSize, maxBufferSize);
    }

    /// <summary>向 PS 重组器输入 RTP 负载并提取完整媒体帧。</summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="markerBit">RTP 帧结束标记位。</param>
    /// <returns>向 PS 重组器输入 RTP 负载并提取完整媒体帧得到的条目集合。</returns>
    public List<PsPesFrame>? Feed(ReadOnlySpan<byte> payload, bool markerBit)
    {
        List<PsPesFrame>? allFrames = null;

        try
        {
            if (_waitingForPackStart)
            {
                int consumed = SeekNextPackStart(payload);
                if (consumed < 0)
                    return null;
                payload = payload[consumed..];
            }

            // RTP 边界上的 PS 包起点会结束前一包，即使设备从不设置 Marker 位。
            if (_length > 0 && StartsWithPackStart(payload))
                ParseBuffered(ref allFrames);

            if (payload.Length > _maxBufferSize - _length)
            {
                DiscardUntilNextPackStart();
                return allFrames;
            }

            if (!payload.IsEmpty)
            {
                EnsureCapacity(payload.Length);
                payload.CopyTo(_buffer.AsSpan(_length));
                _length += payload.Length;
            }

            if (markerBit && _length > 0)
                ParseBuffered(ref allFrames);

            return allFrames;
        }
        catch
        {
            // 解析失败不得污染下一幅图像；等待明确的 PS 包边界重新同步，而不继续追加任意数据。
            DiscardUntilNextPackStart();
            return allFrames;
        }
    }

    /// <summary>丢弃当前不完整数据并等待下一个 PS 包起点。</summary>
    public void DiscardUntilNextPackStart()
    {
        _length = 0;
        _waitingForPackStart = true;
        _packStartMatchLength = 0;
        _discontinuity = true;
    }

    /// <summary>读取并清除本轮媒体不连续标志。</summary>
    /// <returns>读取并清除本轮媒体不连续标志是否成立。</returns>
    public bool ConsumeDiscontinuity()
    {
        bool value = _discontinuity;
        _discontinuity = false;
        return value;
    }

    /// <summary>
    /// 获取 当前缓冲区长度。
    /// </summary>
    internal int CurrentBufferLength => _length;

    /// <summary>清空本次接收或解析生命周期的累计状态。</summary>
    public void Reset()
    {
        _length = 0;
        _waitingForPackStart = false;
        _packStartMatchLength = 0;
        _discontinuity = false;
        _videoCodec = MediaCodec.Unknown;
        _audioCodecs.Clear();

        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        _reusableFrames = null;
    }

    /// <summary>
    /// 解析Buffered。
    /// </summary>
    /// <param name="allFrames">本次解复用得到的全部媒体帧。</param>
    private void ParseBuffered(ref List<PsPesFrame>? allFrames)
    {
        if (_length <= 0 || _buffer == null)
            return;

        var parsed = PsParser.ParsePsPacket(_buffer.AsSpan(0, _length), ref _videoCodec, _audioCodecs);
        _length = 0;
        if (parsed.Count == 0)
            return;

        allFrames ??= GetFrameList();
        allFrames.AddRange(parsed);
    }

    /// <returns>找到的位置索引；未找到时返回 -1。</returns>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <summary>在新负载及跨包尾部中查找 PS 包起点。</summary>
    private int SeekNextPackStart(ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i < payload.Length; i++)
        {
            byte value = payload[i];
            switch (_packStartMatchLength)
            {
                case 0:
                    _packStartMatchLength = value == 0 ? 1 : 0;
                    break;
                case 1:
                    _packStartMatchLength = value == 0 ? 2 : 0;
                    break;
                case 2:
                    _packStartMatchLength = value == 1 ? 3 : value == 0 ? 2 : 0;
                    break;
                default:
                    if (value == 0xBA)
                    {
                        EnsureCapacity(4);
                        PackStartBytes.CopyTo(_buffer);
                        _length = 4;
                        _waitingForPackStart = false;
                        _packStartMatchLength = 0;
                        return i + 1;
                    }
                    _packStartMatchLength = value == 0 ? 1 : 0;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// 启动s使用Pack起始。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <returns>启动s使用Pack起始是否成立。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithPackStart(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4
        && payload[0] == 0
        && payload[1] == 0
        && payload[2] == 1
        && payload[3] == 0xBA;

    /// <summary>
    /// 获取帧列表。
    /// </summary>
    /// <returns>获取帧列表得到的条目集合。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<PsPesFrame> GetFrameList()
    {
        if (_reusableFrames != null)
        {
            _reusableFrames.Clear();
            return _reusableFrames;
        }

        return _reusableFrames = new List<PsPesFrame>(4);
    }

    /// <summary>在允许的容量上限内扩展数据缓冲区。</summary>
    /// <param name="additional">本次需要追加的容量。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int additional)
    {
        int needed = checked(_length + additional);
        if (_buffer != null && _buffer.Length >= needed)
            return;

        uint requested = (uint)Math.Max(needed, InitialBufferSize);
        int newSize = requested >= 0x40000000u
            ? needed
            : (int)BitOperations.RoundUpToPowerOf2(requested);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        if (_buffer != null)
        {
            _buffer.AsSpan(0, _length).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = newBuffer;
    }
}

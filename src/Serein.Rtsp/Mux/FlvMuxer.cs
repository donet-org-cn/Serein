using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Serein.Media;

namespace Serein.Rtsp.Mux;

/// <summary>
/// 高性能 FLV 封装器。将统一媒体帧（<see cref="IMediaFrame"/>）封装为 FLV 字节流。
/// <para>
/// 使用 <see cref="ArrayPool{T}"/> 池化缓冲区，避免每帧分配。
/// 支持 H.264/H.265 视频和 G.711A/U、AAC 音频。
/// </para>
/// </summary>
public sealed class FlvMuxer : IDisposable
{
    // FLV 常量
    /// <summary>写入 FLV 文件头的格式版本。</summary>
    private const byte FlvVersion = 0x01;
    /// <summary>FLV 文件头中的视频存在标记。</summary>
    private const byte FlagVideo = 0x01;
    /// <summary>FLV 文件头中的音频存在标记。</summary>
    private const byte FlagAudio = 0x04;
    /// <summary>FLV 文件头的字节数。</summary>
    private const uint FlvHeaderSize = 9;

    // FLV Tag 类型
    /// <summary>FLV 音频标签的类型编号。</summary>
    private const byte TagTypeAudio = 0x08;
    /// <summary>FLV 视频标签的类型编号。</summary>
    private const byte TagTypeVideo = 0x09;

    // H.264 NAL 类型
    /// <summary>H.264 序列参数集的 NAL 类型编号。</summary>
    private const byte Nal264Sps = 7;
    /// <summary>H.264 图像参数集的 NAL 类型编号。</summary>
    private const byte Nal264Pps = 8;
    /// <summary>H.264 即时解码刷新帧的 NAL 类型编号。</summary>
    private const byte Nal264Idr = 5;

    // H.265 NAL 类型
    /// <summary>H.265 视频参数集的 NAL 类型编号。</summary>
    private const byte Nal265Vps = 32;
    /// <summary>H.265 序列参数集的 NAL 类型编号。</summary>
    private const byte Nal265Sps = 33;
    /// <summary>H.265 图像参数集的 NAL 类型编号。</summary>
    private const byte Nal265Pps = 34;
    /// <summary>H.265 随机接入图片类型范围的最小编号。</summary>
    private const byte Nal265IrapMin = 16;
    /// <summary>H.265 随机接入图片类型范围的最大编号。</summary>
    private const byte Nal265IrapMax = 21;

    /// <summary>单个视频参数集允许的最大字节数。</summary>
    private const int MaxParameterSetLength = ushort.MaxValue;
    /// <summary>Annex-B 参数集组合允许的最大字节数。</summary>
    private const int MaxAnnexBConfigurationLength = 256 * 1024;
    /// <summary>FLV 标签正文允许的最大字节数。</summary>
    private const int MaxFlvTagDataLength = 0xFFFFFF;

    /// <summary>常见访问单元的负载边界暂存在 256 字节栈空间中。</summary>
    private const int CachedPayloadNaluCount = 32;

    /// <summary>本次访问单元内一个负载 NAL 的位置；不持有输入帧引用。</summary>
    private struct PayloadNalu
    {
        internal int Offset;
        internal int Length;
    }

    // FLV 音频编码 ID
    /// <summary>FLV 中 G.711 A 律音频的格式编号。</summary>
    private const byte AudioFormatG711A = 7;
    /// <summary>FLV 中 G.711 μ 律音频的格式编号。</summary>
    private const byte AudioFormatG711U = 8;

    /// <summary>是否已经发送当前视频编码的序列头。</summary>
    private bool _hasVideoSequenceHeader;
    /// <summary>是否已经发送当前音频编码的序列头。</summary>
    private bool _hasAudioSequenceHeader;
    /// <summary>缓存的序列参数集。</summary>
    private byte[]? _sps;
    /// <summary>缓存的图像参数集。</summary>
    private byte[]? _pps;
    /// <summary>缓存的视频参数集。</summary>
    private byte[]? _vps; // H.265
    /// <summary>当前媒体流识别或协商的视频编码。</summary>
    private MediaCodec _videoCodec = MediaCodec.Unknown;

    // MSE 要求首个送入 SourceBuffer 的视频帧必须是 IDR（关键帧），
    // 否则解码器无法初始化（UnrecoverableEarlyEof / 黑屏）。
    // 摄像头 RTSP 流在 SPS/PPS 之后可能先发 P 帧再发 IDR，
    // 必须在第一个 IDR 到达前丢弃所有非关键帧。
    /// <summary>当前封装状态是否已经收到首个恢复关键帧。</summary>
    private bool _hasFirstIdr;

    /// <summary>诊断日志回调。</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    // 分别的音视频时间戳基准
    /// <summary>视频流时间戳的归一化基准。</summary>
    private long _videoBaseTimestamp = -1;
    /// <summary>音频流时间戳的归一化基准。</summary>
    private long _audioBaseTimestamp = -1;
    /// <summary>上一视频标签的时间戳。</summary>
    private uint _lastVideoTimestamp;
    /// <summary>上一音频标签的时间戳。</summary>
    private uint _lastAudioTimestamp;

    // 可复用的池化缓冲区
    /// <summary>媒体封装时复用的工作缓冲区。</summary>
    private byte[]? _workBuffer;
    /// <summary>工作缓冲区的初始字节容量。</summary>
    private const int WorkBufferInitialSize = 4096;

    /// <summary>是否包含音频。</summary>
    public bool HasAudio { get; set; } = true;

    /// <summary>是否包含视频。</summary>
    public bool HasVideo { get; set; } = true;

    /// <summary>是否使用池化输出。</summary>
    public bool UsePooledOutput { get; set; } = true;

    /// <summary>
    /// 获取WorkSpan。
    /// </summary>
    /// <param name="minSize">缓冲区所需的最小字节容量。</param>
    /// <returns>与本次提取或读取范围对应的字节数据。</returns>
    private Span<byte> GetWorkSpan(int minSize)
    {
        if (_workBuffer == null || _workBuffer.Length < minSize)
        {
            if (_workBuffer != null)
                ArrayPool<byte>.Shared.Return(_workBuffer);
            _workBuffer = ArrayPool<byte>.Shared.Rent(minSize);
        }
        return _workBuffer.AsSpan(0, minSize);
    }

    /// <summary>
    /// 获取 FLV 文件头（含 PreviousTagSize0），共 13 字节。
    /// </summary>
    /// <returns>与本次提取或读取范围对应的字节数据。</returns>
    public byte[] GetFlvHeader()
    {
        var header = new byte[FlvHeaderSize + 4];
        "FLV"u8.CopyTo(header);
        header[3] = FlvVersion;
        header[4] = (byte)((HasAudio ? FlagAudio : 0) | (HasVideo ? FlagVideo : 0));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), FlvHeaderSize);
        return header;
    }

    /// <summary>
    /// 将 FLV 文件头写入目标 Span（需要至少 13 字节）。
    /// </summary>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    public void WriteFlvHeaderTo(Span<byte> destination)
    {
        "FLV"u8.CopyTo(destination);
        destination[3] = FlvVersion;
        destination[4] = (byte)((HasAudio ? FlagAudio : 0) | (HasVideo ? FlagVideo : 0));
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], FlvHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[9..], 0);
    }

    /// <summary>
    /// 获取 FLV 头所需字节数。
    /// </summary>
    public static int FlvHeaderSizeTotal => 13;

    /// <summary>
    /// 将一个媒体帧封装为 FLV Tag 字节。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>将一个媒体帧封装为 FLV Tag 字节后的字节数据。</returns>
    public byte[] MuxFrame(IMediaFrame frame)
    {
        if (frame is PooledMediaFrame pooled)
            return MuxPooledFrame(pooled);

        return Array.Empty<byte>();
    }

    /// <summary>
    /// 将一个池化媒体帧封装为 FLV Tag 字节。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>将一个池化媒体帧封装为 FLV Tag 字节后的字节数据。</returns>
    public byte[] MuxPooledFrame(PooledMediaFrame frame)
        => MuxPooledFrameCompat(frame);

    /// <summary>
    /// 估算封装后的最大输出大小。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>估算封装后的最大输出大小计算得到的数值。</returns>
    public int GetMaxOutputSize(PooledMediaFrame frame)
    {
        var bodyLength = frame.Length;
        if (bodyLength == 0) return 0;

        if (frame.Kind == MediaFrameKind.Audio)
        {
            if (!HasAudio) return 0;
            return bodyLength + 64;
        }

        if (frame.Kind != MediaFrameKind.Video)
            return 0;
        if (!HasVideo)
            return 0;

        var body = frame.GetSpan();
        var nalCount = CountNalus(body);
        if (nalCount == 0) nalCount = 1;

        // 已经发送序列头后仍要合并缓存参数集：当前帧可能只更新一种参数集，新序列头仍需引用其他已缓存参数集。
        var sequenceHeaderSize = GetPendingVideoSequenceHeaderSize(frame.Codec);
        return checked(bodyLength + nalCount * 4 + sequenceHeaderSize + 64);
    }

    /// <summary>将视频参数集写入目标 FLV 序列头缓冲区。</summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="annexBConfig">采用 Annex-B 格式的视频参数集。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <returns>实际写入的元素数量。</returns>
    public int WriteVideoSequenceHeaderTo(MediaCodec codec, ReadOnlySpan<byte> annexBConfig,
        Span<byte> destination, uint timestamp = 0)
    {
        if (!HasVideo || GetMaxVideoSequenceHeaderSize(codec, annexBConfig.Length) == 0)
            return 0;

        EnsureVideoCodec(codec);
        CollectParameterSets(annexBConfig, codec);
        return codec == MediaCodec.H265
            ? TryWriteHevcSequenceHeaderTo(timestamp, destination)
            : TryWriteAvcSequenceHeaderTo(timestamp, destination);
    }

    /// <summary>计算写入视频序列头需要预留的最大字节数。</summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="annexBConfigLength">视频参数集的有效字节数。</param>
    /// <returns>计算写入视频序列头需要预留的最大字节数计算得到的数值。</returns>
    public static int GetMaxVideoSequenceHeaderSize(MediaCodec codec, int annexBConfigLength)
    {
        if (codec is not (MediaCodec.H264 or MediaCodec.H265) || annexBConfigLength <= 0 ||
            annexBConfigLength > MaxAnnexBConfigurationLength)
            return 0;

        return annexBConfigLength + 64;
    }

    /// <summary>
    /// 将一个池化媒体帧封装到调用方提供的缓冲区。返回写入字节数。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    public int MuxPooledFrameTo(PooledMediaFrame frame, byte[] destination)
        => MuxPooledFrameTo(frame, destination.AsSpan());

    /// <summary>通过兼容数组接口封装池化媒体帧。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>通过兼容数组接口封装池化媒体帧后的字节数据。</returns>
    private byte[] MuxPooledFrameCompat(PooledMediaFrame frame)
    {
        uint timestamp;
        if (frame.Kind == MediaFrameKind.Video)
        {
            if (!HasVideo) return Array.Empty<byte>();
            timestamp = NormalizeTimestamp(frame.TimestampMs, ref _videoBaseTimestamp, ref _lastVideoTimestamp, "video");

            var result = MuxVideo(frame.GetSpan(), frame.Codec, timestamp);
            if (_videoFrameCount <= 10 || (result.Length == 0 && _videoFrameCount <= 100))
                DiagnosticLog?.Invoke($"[FLV入口] 视频帧#{_videoFrameCount}: codec={frame.Codec}, dataLen={frame.GetSpan().Length}, resultLen={result.Length}, ts={timestamp}, hasSeqHdr={_hasVideoSequenceHeader}");
            return result;
        }

        if (frame.Kind == MediaFrameKind.Audio)
        {
            if (!HasAudio) return Array.Empty<byte>();
            timestamp = NormalizeTimestamp(frame.TimestampMs, ref _audioBaseTimestamp, ref _lastAudioTimestamp, "audio");

            _audioFrameCount++;
            return MuxAudio(frame.GetSpan(), frame.Codec, timestamp);
        }

        return Array.Empty<byte>();
    }

    /// <summary>重置封装器状态。</summary>
    public void Reset()
    {
        _hasVideoSequenceHeader = false;
        _hasAudioSequenceHeader = false;
        _sps = null;
        _pps = null;
        _vps = null;
        _videoCodec = MediaCodec.Unknown;
        _hasFirstIdr = false;
        _videoBaseTimestamp = -1;
        _audioBaseTimestamp = -1;
        _lastVideoTimestamp = 0;
        _lastAudioTimestamp = 0;
    }

    #region 视频封装

    /// <summary>累计视频帧数量。</summary>
    private int _videoFrameCount = 0;
    /// <summary>累计音频帧数量。</summary>
    private int _audioFrameCount = 0;

    /// <summary>将视频访问单元和必要的参数集封装为 FLV 标签。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <returns>将视频访问单元和必要的参数集封装为 FLV 标签后的字节数据。</returns>
    private byte[] MuxVideo(ReadOnlySpan<byte> body, MediaCodec codec, uint timestamp)
    {
        if (body.Length == 0) return Array.Empty<byte>();

        _videoFrameCount++;
        var nalCount = CountNalus(body);
        if (_videoFrameCount <= 3 || (nalCount == 1 && _videoFrameCount <= 20))
        {
            var preview = body.Length > 64 ? body[..64] : body;
            DiagnosticLog?.Invoke($"[FLV视频] 帧#{_videoFrameCount}: {body.Length}B, codec={codec}, NAL数={nalCount}, hasVPS={_vps != null}, hasSPS={_sps != null}, hasPPS={_pps != null}, hasSeqHdr={_hasVideoSequenceHeader}, body前64B: {Convert.ToHexString(preview)}");
        }

        if (nalCount == 0) return Array.Empty<byte>();

        var required = checked(body.Length + nalCount * 4 +
            GetPendingVideoSequenceHeaderSize(codec) + 64);
        var buffer = ArrayPool<byte>.Shared.Rent(required);
        try
        {
            var written = MuxVideoTo(body, codec, timestamp, buffer);
            return written > 0 ? buffer.AsSpan(0, written).ToArray() : Array.Empty<byte>();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 将 FLV Tag 直接写入 Span。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    public int MuxPooledFrameTo(PooledMediaFrame frame, Span<byte> destination)
    {
        uint timestamp;
        if (frame.Kind == MediaFrameKind.Video)
        {
            if (!HasVideo) return 0;
            timestamp = NormalizeTimestamp(frame.TimestampMs, ref _videoBaseTimestamp, ref _lastVideoTimestamp, "video");
            return MuxVideoTo(frame.GetSpan(), frame.Codec, timestamp, destination);
        }

        if (frame.Kind == MediaFrameKind.Audio)
        {
            if (!HasAudio) return 0;
            timestamp = NormalizeTimestamp(frame.TimestampMs, ref _audioBaseTimestamp, ref _lastAudioTimestamp, "audio");
            return MuxAudioTo(frame.GetSpan(), frame.Codec, timestamp, destination);
        }

        return 0;
    }

    /// <summary>相对起始时间戳归一化并维持输出时间戳连续性。</summary>
    /// <param name="frameTimestampMs">媒体帧时间戳，单位为毫秒。</param>
    /// <param name="baseTimestamp">用于归一化的起始时间戳。</param>
    /// <param name="lastTimestamp">上一帧时间戳。</param>
    /// <param name="track">媒体轨道配置。</param>
    /// <returns>相对起始时间戳归一化并维持输出时间戳连续性计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NormalizeTimestamp(long frameTimestampMs, ref long baseTimestamp, ref uint lastTimestamp, string track)
    {
        if (baseTimestamp < 0)
        {
            baseTimestamp = frameTimestampMs;
        }

        var relativeTimestamp = frameTimestampMs - baseTimestamp;

        // 某些摄像头直播/回放会出现 PTS 回退；若直接转 uint 会回绕成数十亿毫秒，
        // 前端播放器就会显示成几百小时。
        if (relativeTimestamp < 0)
        {
            DiagnosticLog?.Invoke($"[FLV入口] {track} 时间戳回退: base={baseTimestamp}, current={frameTimestampMs}, last={lastTimestamp}. 将重建基准避免回绕。");
            baseTimestamp = frameTimestampMs - lastTimestamp;
            relativeTimestamp = lastTimestamp;
        }

        if (relativeTimestamp < lastTimestamp)
        {
            relativeTimestamp = lastTimestamp;
        }

        if (relativeTimestamp > uint.MaxValue)
        {
            DiagnosticLog?.Invoke($"[FLV入口] {track} 时间戳过大: relative={relativeTimestamp}, last={lastTimestamp}. 将钳制为 lastTimestamp。");
            relativeTimestamp = lastTimestamp;
        }

        lastTimestamp = (uint)relativeTimestamp;
        return lastTimestamp;
    }

    /// <summary>把视频访问单元封装到调用方提供的 FLV 缓冲区。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    [SkipLocalsInit] // 只读取分析阶段已写入的槽位，避免逐帧清零整个栈缓存。
    private int MuxVideoTo(ReadOnlySpan<byte> body, MediaCodec codec, uint timestamp, Span<byte> destination)
    {
        if (body.Length == 0 || codec is not (MediaCodec.H264 or MediaCodec.H265))
            return 0;

        EnsureVideoCodec(codec);

        var isH265 = codec == MediaCodec.H265;
        var payloadLength = 0;
        var payloadCount = 0;
        var isKeyFrame = false;
        Span<PayloadNalu> payloadNalus = stackalloc PayloadNalu[CachedPayloadNaluCount];
        AnalyzeVideoAccessUnit(body, codec, ref payloadLength, ref payloadCount, ref isKeyFrame, payloadNalus);

        var written = isH265
            ? TryWriteHevcSequenceHeaderTo(timestamp, destination)
            : TryWriteAvcSequenceHeaderTo(timestamp, destination);

        if (payloadCount == 0 || !_hasVideoSequenceHeader)
            return written;

        var opensVideoGate = !_hasFirstIdr;
        if (opensVideoGate)
        {
            if (!isKeyFrame)
            {
                DiagnosticLog?.Invoke("[FLV gate] Dropping video until a random-access frame arrives.");
                return written;
            }
        }

        var accessUnitWritten = WriteVideoAccessUnitTagTo(body, timestamp, isKeyFrame, isH265,
            payloadLength, payloadCount, payloadNalus, destination[written..]);
        if (accessUnitWritten > 0 && opensVideoGate)
        {
            _hasFirstIdr = true;
            DiagnosticLog?.Invoke("[FLV gate] Random-access frame received; video output resumed.");
        }

        return written + accessUnitWritten;
    }

    /// <summary>在参数集齐全时向目标缓冲区写入 AVC 序列头。</summary>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private int TryWriteAvcSequenceHeaderTo(uint timestamp, Span<byte> dest)
    {
        if (_sps == null || _pps == null || _hasVideoSequenceHeader ||
            _sps.Length < 4 || _pps.Length < 2 ||
            !IsValidParameterSet(_sps) || !IsValidParameterSet(_pps))
            return 0;

        var configLen = 11 + _sps.Length + _pps.Length;
        var tagDataLen = 5 + configLen;
        var totalLen = 11 + tagDataLen + 4;
        if (tagDataLen > MaxFlvTagDataLength || dest.Length < totalLen)
            return 0;

        WriteFlvTagHeaderTo(dest, TagTypeVideo, tagDataLen, timestamp);
        dest[11] = 0x17;
        dest[12] = 0x00;
        dest[13] = 0x00; dest[14] = 0x00; dest[15] = 0x00;

        var config = dest.Slice(16, configLen);
        config.Clear();
        config[0] = 0x01;
        config[1] = _sps[1]; config[2] = _sps[2]; config[3] = _sps[3];
        config[4] = 0xFF; config[5] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(config[6..], (ushort)_sps.Length);
        _sps.AsSpan().CopyTo(config[8..]);
        int off = 8 + _sps.Length;
        config[off++] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(config[off..], (ushort)_pps.Length);
        _pps.AsSpan().CopyTo(config[(off + 2)..]);

        WritePreviousTagSizeTo(dest[(16 + configLen)..], tagDataLen);
        _hasVideoSequenceHeader = true;
        return totalLen;
    }

    /// <summary>在参数集齐全时向目标缓冲区写入 HEVC 序列头。</summary>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private int TryWriteHevcSequenceHeaderTo(uint timestamp, Span<byte> dest)
    {
        if (_vps == null || _sps == null || _pps == null || _hasVideoSequenceHeader ||
            _vps.Length < 3 || _sps.Length < 13 || _pps.Length < 3 ||
            !IsValidParameterSet(_vps) ||
            !IsValidParameterSet(_sps) || !IsValidParameterSet(_pps))
            return 0;

        var configLen = 23 + 5 + _vps.Length + 5 + _sps.Length + 5 + _pps.Length;
        var tagDataLen = 5 + configLen;
        var totalLen = 11 + tagDataLen + 4;
        if (tagDataLen > MaxFlvTagDataLength || dest.Length < totalLen)
            return 0;

        WriteFlvTagHeaderTo(dest, TagTypeVideo, tagDataLen, timestamp);
        dest[11] = 0x90;
        dest[12] = (byte)'h'; dest[13] = (byte)'v'; dest[14] = (byte)'c'; dest[15] = (byte)'1';

        var config = dest.Slice(16, configLen);
        config.Clear();
        int offset = 0;

        _ = HevcSpsParser.TryParse(_sps, out var spsConfiguration);

        config[offset++] = 0x01;
        config[offset++] = spsConfiguration.GeneralProfile;
        BinaryPrimitives.WriteUInt32BigEndian(
            config[offset..], spsConfiguration.GeneralProfileCompatibilityFlags);
        offset += 4;
        for (int shift = 40; shift >= 0; shift -= 8)
            config[offset++] = (byte)(spsConfiguration.GeneralConstraintIndicatorFlags >> shift);
        config[offset++] = spsConfiguration.GeneralLevelIdc;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], 0xF000); offset += 2;
        config[offset++] = 0xFC;
        config[offset++] = (byte)(0xFC | spsConfiguration.ChromaFormatIdc);
        config[offset++] = (byte)(0xF8 | spsConfiguration.BitDepthLumaMinus8);
        config[offset++] = (byte)(0xF8 | spsConfiguration.BitDepthChromaMinus8);
        offset += 2;
        config[offset++] = (byte)((spsConfiguration.NumTemporalLayers << 3) |
            (spsConfiguration.TemporalIdNested ? 0x04 : 0) | 0x03);
        config[offset++] = 0x03;

        config[offset++] = 0xA0;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], 1); offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], (ushort)_vps.Length); offset += 2;
        _vps.AsSpan().CopyTo(config[offset..]); offset += _vps.Length;

        config[offset++] = 0xA1;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], 1); offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], (ushort)_sps.Length); offset += 2;
        _sps.AsSpan().CopyTo(config[offset..]); offset += _sps.Length;

        config[offset++] = 0xA2;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], 1); offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(config[offset..], (ushort)_pps.Length); offset += 2;
        _pps.AsSpan().CopyTo(config[offset..]); offset += _pps.Length;

        var w = 16 + offset;
        WritePreviousTagSizeTo(dest[w..], tagDataLen); w += 4;
        _hasVideoSequenceHeader = true;
        return w;
    }

    /// <summary>
    /// 写入视频AccessUnitTag目标。
    /// </summary>
    /// <param name="body">消息正文。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="isH265">是否按 H.265 格式解释视频数据。</param>
    /// <param name="payloadLength">有效负载的字节数。</param>
    /// <param name="payloadCount">有效负载的字节数。</param>
    /// <param name="payloadNalus">分析时缓存的负载边界；数量超出缓存时回退到逐段读取。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int WriteVideoAccessUnitTagTo(ReadOnlySpan<byte> body, uint timestamp,
        bool isKeyFrame, bool isH265, int payloadLength, int payloadCount,
        ReadOnlySpan<PayloadNalu> payloadNalus, Span<byte> dest)
    {
        var videoHeaderLength = isH265 ? 8 : 5;
        var tagDataLen = checked(videoHeaderLength + payloadLength + payloadCount * 4);
        var totalLength = checked(11 + tagDataLen + 4);
        if (tagDataLen > MaxFlvTagDataLength || dest.Length < totalLength)
            return 0;

        WriteFlvTagHeaderTo(dest, TagTypeVideo, tagDataLen, timestamp);
        var w = 11;
        if (isH265)
        {
            dest[w++] = isKeyFrame ? (byte)0x91 : (byte)0xA1;
            dest[w++] = (byte)'h'; dest[w++] = (byte)'v'; dest[w++] = (byte)'c'; dest[w++] = (byte)'1';
            dest[w++] = 0x00; dest[w++] = 0x00; dest[w++] = 0x00;
        }
        else
        {
            dest[w++] = isKeyFrame ? (byte)0x17 : (byte)0x27;
            dest[w++] = 0x01;
            dest[w++] = 0x00; dest[w++] = 0x00; dest[w++] = 0x00;
        }

        if (payloadCount <= payloadNalus.Length)
        {
            // 复用分析阶段的边界和类型判定，无需再次扫描整个访问单元。
            foreach (var payload in payloadNalus[..payloadCount])
            {
                BinaryPrimitives.WriteInt32BigEndian(dest[w..], payload.Length);
                w += 4;
                body.Slice(payload.Offset, payload.Length).CopyTo(dest[w..]);
                w += payload.Length;
            }
        }
        else if (!TryReadNextNalu(body, 0, out var nal, out var nextOffset))
        {
            WritePayloadNalu(body, isH265, dest, ref w);
        }
        else
        {
            while (true)
            {
                WritePayloadNalu(nal, isH265, dest, ref w);
                if (!TryReadNextNalu(body, nextOffset, out nal, out nextOffset))
                    break;
            }
        }

        WritePreviousTagSizeTo(dest[w..], tagDataLen);
        return w + 4;
    }

    /// <summary>汇总视频访问单元的参数集与关键帧信息。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="payloadLength">有效负载的字节数。</param>
    /// <param name="payloadCount">有效负载的字节数。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="payloadNalus">可选的栈缓冲区，用于缓存当前帧的负载边界。</param>
    private void AnalyzeVideoAccessUnit(ReadOnlySpan<byte> body, MediaCodec codec,
        ref int payloadLength, ref int payloadCount, ref bool isKeyFrame,
        Span<PayloadNalu> payloadNalus = default)
    {
        if (!TryReadNextNalu(body, 0, out var nal, out var nextOffset))
        {
            if (AnalyzeNalu(body, codec, ref payloadLength, ref payloadCount, ref isKeyFrame) &&
                payloadCount <= payloadNalus.Length)
                payloadNalus[payloadCount - 1] = new PayloadNalu { Offset = 0, Length = body.Length };
            return;
        }

        while (true)
        {
            if (AnalyzeNalu(nal, codec, ref payloadLength, ref payloadCount, ref isKeyFrame) &&
                payloadCount <= payloadNalus.Length)
                payloadNalus[payloadCount - 1] = new PayloadNalu { Offset = nextOffset - nal.Length, Length = nal.Length };
            if (!TryReadNextNalu(body, nextOffset, out nal, out nextOffset))
                break;
        }
    }

    /// <summary>检查一个 NAL 的类型并更新访问单元扫描结果。</summary>
    /// <param name="nal">视频 NAL 单元。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="payloadLength">有效负载的字节数。</param>
    /// <param name="payloadCount">有效负载的字节数。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <returns>当前 NAL 是否计入输出负载。</returns>
    private bool AnalyzeNalu(ReadOnlySpan<byte> nal, MediaCodec codec,
        ref int payloadLength, ref int payloadCount, ref bool isKeyFrame)
    {
        if (nal.Length == 0)
            return false;

        var isH265 = codec == MediaCodec.H265;
        if (isH265 && nal.Length < 2)
            return false;

        var nalType = isH265 ? ((nal[0] >> 1) & 0x3F) : (nal[0] & 0x1F);
        if (isH265)
        {
            switch (nalType)
            {
                case Nal265Vps: UpdateParameterSet(ref _vps, nal, 3); return false;
                case Nal265Sps: UpdateParameterSet(ref _sps, nal, 13); return false;
                case Nal265Pps: UpdateParameterSet(ref _pps, nal, 3); return false;
            }

            isKeyFrame |= nalType is >= Nal265IrapMin and <= Nal265IrapMax;
        }
        else
        {
            switch (nalType)
            {
                case Nal264Sps: UpdateParameterSet(ref _sps, nal, 4); return false;
                case Nal264Pps: UpdateParameterSet(ref _pps, nal, 2); return false;
            }

            isKeyFrame |= nalType == Nal264Idr;
        }

        payloadLength = checked(payloadLength + nal.Length);
        payloadCount++;
        return true;
    }

    /// <summary>
    /// 写入负载Nalu。
    /// </summary>
    /// <param name="nal">视频 NAL 单元。</param>
    /// <param name="isH265">是否按 H.265 格式解释视频数据。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    private static void WritePayloadNalu(ReadOnlySpan<byte> nal, bool isH265,
        Span<byte> destination, ref int offset)
    {
        if (!IsPayloadNalu(nal, isH265))
            return;

        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], nal.Length);
        offset += 4;
        nal.CopyTo(destination[offset..]);
        offset += nal.Length;
    }

    /// <summary>
    /// 判断是否负载Nalu。
    /// </summary>
    /// <param name="nal">视频 NAL 单元。</param>
    /// <param name="isH265">是否按 H.265 格式解释视频数据。</param>
    /// <returns>是否负载Nalu的判定结果。</returns>
    private static bool IsPayloadNalu(ReadOnlySpan<byte> nal, bool isH265)
    {
        if (nal.Length == 0 || (isH265 && nal.Length < 2))
            return false;

        var nalType = isH265 ? ((nal[0] >> 1) & 0x3F) : (nal[0] & 0x1F);
        return isH265
            ? nalType is not (Nal265Vps or Nal265Sps or Nal265Pps)
            : nalType is not (Nal264Sps or Nal264Pps);
    }

    /// <summary>从视频数据中收集并更新 VPS、SPS 与 PPS。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    private void CollectParameterSets(ReadOnlySpan<byte> body, MediaCodec codec)
    {
        var ignoredLength = 0;
        var ignoredCount = 0;
        var ignoredKeyFrame = false;
        AnalyzeVideoAccessUnit(body, codec, ref ignoredLength, ref ignoredCount, ref ignoredKeyFrame);
    }

    /// <summary>在编码格式变化时重置参数集与关键帧恢复状态。</summary>
    /// <param name="codec">媒体编码格式。</param>
    private void EnsureVideoCodec(MediaCodec codec)
    {
        if (_videoCodec == codec)
            return;

        _videoCodec = codec;
        _sps = null;
        _pps = null;
        _vps = null;
        _hasVideoSequenceHeader = false;
        _hasFirstIdr = false;
    }

    /// <summary>
    /// 更新参数Set。
    /// </summary>
    /// <param name="current">当前条目或状态。</param>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <param name="minimumLength">缓冲区所需的最小长度。</param>
    private void UpdateParameterSet(ref byte[]? current, ReadOnlySpan<byte> value, int minimumLength)
    {
        if (value.Length < minimumLength || value.Length > MaxParameterSetLength ||
            (current != null && value.SequenceEqual(current)))
            return;

        current = value.ToArray();
        _hasVideoSequenceHeader = false;
        _hasFirstIdr = false;
    }

    /// <summary>
    /// 判断是否Valid参数Set。
    /// </summary>
    /// <param name="parameterSet">视频参数集 NAL 单元。</param>
    /// <returns>是否Valid参数Set的判定结果。</returns>
    private static bool IsValidParameterSet(byte[] parameterSet)
        => parameterSet.Length > 0 && parameterSet.Length <= MaxParameterSetLength;

    /// <summary>
    /// 获取待处理视频Sequence头部大小。
    /// </summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>获取待处理视频Sequence头部大小计算得到的数值。</returns>
    private int GetPendingVideoSequenceHeaderSize(MediaCodec codec)
    {
        if (_videoCodec != codec)
            return codec == MediaCodec.H265 ? 58 : codec == MediaCodec.H264 ? 31 : 0;

        if (codec == MediaCodec.H264)
            return 31 + (_sps?.Length ?? 0) + (_pps?.Length ?? 0);

        if (codec == MediaCodec.H265)
            return 58 + (_vps?.Length ?? 0) + (_sps?.Length ?? 0) + (_pps?.Length ?? 0);

        return 0;
    }

    #endregion

    #region 音频封装

    /// <summary>是否丢弃 G.711A/U 音频。</summary>
    public bool DiscardG711Audio { get; set; } = false;

    /// <summary>根据音频编码格式生成 FLV 音频标签。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <returns>根据音频编码格式生成 FLV 音频标签后的字节数据。</returns>
    private byte[] MuxAudio(ReadOnlySpan<byte> body, MediaCodec codec, uint timestamp)
    {
        if (body.Length == 0) return Array.Empty<byte>();
        if (DiscardG711Audio && (codec == MediaCodec.G711A || codec == MediaCodec.G711U)) return Array.Empty<byte>();

        return codec switch
        {
            MediaCodec.G711A => MuxG711(body, timestamp, AudioFormatG711A),
            MediaCodec.G711U => MuxG711(body, timestamp, AudioFormatG711U),
            MediaCodec.AAC => MuxAac(body, timestamp),
            _ => Array.Empty<byte>()
        };
    }

    /// <summary>把音频帧封装到目标 FLV 缓冲区。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private int MuxAudioTo(ReadOnlySpan<byte> body, MediaCodec codec, uint timestamp, Span<byte> dest)
    {
        if (body.Length == 0) return 0;
        if (DiscardG711Audio && (codec == MediaCodec.G711A || codec == MediaCodec.G711U)) return 0;

        return codec switch
        {
            MediaCodec.G711A => MuxG711To(body, timestamp, AudioFormatG711A, dest),
            MediaCodec.G711U => MuxG711To(body, timestamp, AudioFormatG711U, dest),
            MediaCodec.AAC => MuxAacTo(body, timestamp, dest),
            _ => 0
        };
    }

    /// <summary>把 G.711 音频负载封装为 FLV 标签。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="format">音频格式描述或格式编号。</param>
    /// <returns>把 G.711 音频负载封装为 FLV 标签后的字节数据。</returns>
    private byte[] MuxG711(ReadOnlySpan<byte> body, uint timestamp, byte format)
    {
        int tagDataLen = 1 + body.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(tagDataLen + 16);
        try
        {
            var span = buffer.AsSpan();
            WriteFlvTagHeaderTo(span, TagTypeAudio, tagDataLen, timestamp);
            span[11] = (byte)((format << 4) | 0x02);
            body.CopyTo(span[12..]);
            WritePreviousTagSizeTo(span[(12 + body.Length)..], tagDataLen);
            return buffer.AsSpan(0, 11 + tagDataLen + 4).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把 G.711 音频封装到目标 FLV 缓冲区。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="format">音频格式描述或格式编号。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private int MuxG711To(ReadOnlySpan<byte> body, uint timestamp, byte format, Span<byte> dest)
    {
        int tagDataLen = 1 + body.Length;
        WriteFlvTagHeaderTo(dest, TagTypeAudio, tagDataLen, timestamp);
        dest[11] = (byte)((format << 4) | 0x02);
        body.CopyTo(dest[12..]);
        WritePreviousTagSizeTo(dest[(12 + body.Length)..], tagDataLen);
        return 11 + tagDataLen + 4;
    }

    /// <summary>把 AAC 配置或原始音频封装为 FLV 标签。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <returns>把 AAC 配置或原始音频封装为 FLV 标签后的字节数据。</returns>
    private byte[] MuxAac(ReadOnlySpan<byte> body, uint timestamp)
    {
        int totalSize = body.Length + 64;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            var span = buffer.AsSpan();
            int w = 0;

            if (!_hasAudioSequenceHeader)
            {
                _hasAudioSequenceHeader = true;
                ReadOnlySpan<byte> asc = [0x15, 0x88];
                int seqLen = 2 + asc.Length;
                WriteFlvTagHeaderTo(span[w..], TagTypeAudio, seqLen, timestamp);
                int off = w + 11;
                span[off++] = 0xAF;
                span[off++] = 0x00;
                asc.CopyTo(span[off..]); off += asc.Length;
                WritePreviousTagSizeTo(span[off..], seqLen); off += 4;
                w = off;
            }

            {
                int tagDataLen = 2 + body.Length;
                WriteFlvTagHeaderTo(span[w..], TagTypeAudio, tagDataLen, timestamp);
                int off = w + 11;
                span[off++] = 0xAF;
                span[off++] = 0x01;
                body.CopyTo(span[off..]); off += body.Length;
                WritePreviousTagSizeTo(span[off..], tagDataLen); off += 4;
                w = off;
            }

            return span[..w].ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把 AAC 音频配置或负载封装到目标 FLV 缓冲区。</summary>
    /// <param name="body">消息正文。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private int MuxAacTo(ReadOnlySpan<byte> body, uint timestamp, Span<byte> dest)
    {
        int w = 0;
        if (!_hasAudioSequenceHeader)
        {
            _hasAudioSequenceHeader = true;
            ReadOnlySpan<byte> asc = [0x15, 0x88];
            int seqLen = 2 + asc.Length;
            WriteFlvTagHeaderTo(dest[w..], TagTypeAudio, seqLen, timestamp);
            int off = w + 11;
            dest[off++] = 0xAF;
            dest[off++] = 0x00;
            asc.CopyTo(dest[off..]); off += asc.Length;
            WritePreviousTagSizeTo(dest[off..], seqLen); off += 4;
            w = off;
        }

        {
            int tagDataLen = 2 + body.Length;
            WriteFlvTagHeaderTo(dest[w..], TagTypeAudio, tagDataLen, timestamp);
            int off = w + 11;
            dest[off++] = 0xAF;
            dest[off++] = 0x01;
            body.CopyTo(dest[off..]); off += body.Length;
            WritePreviousTagSizeTo(dest[off..], tagDataLen); off += 4;
            w = off;
        }
        return w;
    }

    #endregion

    #region NAL 解析

    /// <summary>统计 Annex-B 数据中的 NAL 单元数量。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>统计 Annex-B 数据中的 NAL 单元数量计算得到的数值。</returns>
    private static int CountNalus(ReadOnlySpan<byte> data)
    {
        var count = 0;
        var offset = 0;
        while (TryReadNextNalu(data, offset, out _, out offset))
        {
            count++;
        }

        return count == 0 && data.Length > 0 ? 1 : count;
    }

    /// <summary>尝试取得下一个 NAL 单元及后续偏移量。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="nextOffset">下一段数据的起始偏移量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryReadNextNalu(ReadOnlySpan<byte> data, int offset, out ReadOnlySpan<byte> nalu, out int nextOffset)
    {
        return AnnexBStartCode.TryReadNext(data, offset, out nalu, out nextOffset);
    }

    #endregion

    #region FLV Tag 写入（Span 直写，零分配）

    /// <summary>
    /// 写入FlvTag头部目标。
    /// </summary>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <param name="tagType">FLV 标签类型。</param>
    /// <param name="dataSize">数据的字节数。</param>
    /// <param name="timestamp">FLV 标签时间戳，单位为毫秒。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFlvTagHeaderTo(Span<byte> dest, byte tagType, int dataSize, uint timestamp)
    {
        dest[0] = tagType;
        dest[1] = (byte)((dataSize >> 16) & 0xFF);
        dest[2] = (byte)((dataSize >> 8) & 0xFF);
        dest[3] = (byte)(dataSize & 0xFF);
        dest[4] = (byte)((timestamp >> 16) & 0xFF);
        dest[5] = (byte)((timestamp >> 8) & 0xFF);
        dest[6] = (byte)(timestamp & 0xFF);
        dest[7] = (byte)((timestamp >> 24) & 0xFF);
        dest[8] = 0x00; dest[9] = 0x00; dest[10] = 0x00;
    }

    /// <summary>
    /// 写入PreviousTag大小目标。
    /// </summary>
    /// <param name="dest">写入结果的目标缓冲区。</param>
    /// <param name="dataSize">数据的字节数。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePreviousTagSizeTo(Span<byte> dest, int dataSize)
    {
        BinaryPrimitives.WriteInt32BigEndian(dest, 11 + dataSize);
    }

    #endregion

    /// <summary>归还池化数组。</summary>
    /// <param name="buffer">数据缓冲区。</param>
    public static void ReturnPooled(byte[] buffer)
    {
        if (buffer != null && buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <inheritdoc/>
    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        if (_workBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_workBuffer);
            _workBuffer = null;
        }
    }
}

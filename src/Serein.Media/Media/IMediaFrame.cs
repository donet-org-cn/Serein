namespace Serein.Media;

/// <summary>
/// 媒体帧接口。所有协议的 RTP 帧均实现此接口以实现统一处理。
/// </summary>
public interface IMediaFrame
{
    /// <summary>帧类型（视频/音频/透传）。</summary>
    MediaFrameKind Kind { get; }

    /// <summary>编码格式。</summary>
    MediaCodec Codec { get; }

    /// <summary>是否关键帧。</summary>
    bool IsKeyFrame { get; }

    /// <summary>时间戳（毫秒）。</summary>
    long TimestampMs { get; }
}

/// <summary>帧类型。</summary>
public enum MediaFrameKind : byte
{
    /// <summary>视频帧。</summary>
    Video = 0,

    /// <summary>音频帧。</summary>
    Audio = 1,

    /// <summary>透传数据。</summary>
    Transparent = 2,
}

/// <summary>
/// 媒体编码格式。
/// </summary>
public enum MediaCodec : byte
{
    /// <summary>未知。</summary>
    Unknown = 0,

    /// <summary>H.264 视频编码。</summary>
    H264 = 1,

    /// <summary>H.265 视频编码。</summary>
    H265 = 2,

    /// <summary>AVS 视频编码。</summary>
    AVS = 3,

    /// <summary>SVAC 视频编码。</summary>
    SVAC = 4,

    /// <summary>G.711 A 律音频编码。</summary>
    G711A = 10,

    /// <summary>G.711 μ 律音频编码。</summary>
    G711U = 11,

    /// <summary>G.726 音频编码。</summary>
    G726 = 12,

    /// <summary>G.729 音频编码。</summary>
    G729 = 13,

    /// <summary>自适应差分脉冲编码音频。</summary>
    ADPCM = 14,

    /// <summary>AAC 音频编码。</summary>
    AAC = 15,

    /// <summary>未压缩的脉冲编码音频。</summary>
    PCM = 16,

    /// <summary>MPEG 节目流封装格式。</summary>
    PS = 20,
}

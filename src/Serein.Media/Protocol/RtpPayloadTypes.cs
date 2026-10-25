namespace Serein.Media.Protocol;

/// <summary>
/// 常用 RTP 负载类型。
/// </summary>
public static class RtpPayloadTypes
{
    /// <summary>PCMU / G.711 μ-law，静态负载类型，8kHz。</summary>
    public const byte Pcmu = 0;

    /// <summary>PCMA / G.711 A-law，静态负载类型，8kHz。</summary>
    public const byte Pcma = 8;

    /// <summary>电话事件 / DTMF，通常动态映射为 101。</summary>
    public const byte TelephoneEvent = 101;

    /// <summary>H.264 视频，常见动态负载类型。</summary>
    public const byte H264 = 96;

    /// <summary>H.265 视频，常见动态负载类型。</summary>
    public const byte H265 = 98;

    /// <summary>Opus 音频，常见动态负载类型。</summary>
    public const byte Opus = 111;
}

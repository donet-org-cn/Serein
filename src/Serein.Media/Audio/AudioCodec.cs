namespace Serein.Media.Audio;

/// <summary>
/// VoIP 音频编解码类型。
/// </summary>
public enum AudioCodec
{
    /// <summary>
    /// 未知或不支持的编解码。
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// G.711 u-law，对应静态负载类型 0。
    /// </summary>
    Pcmu = 1,

    /// <summary>
    /// G.711 A-law，对应静态负载类型 8。
    /// </summary>
    Pcma = 2,
}

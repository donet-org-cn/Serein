namespace Serein.Media.Audio;

/// <summary>
/// SDP 中声明的音频格式。
/// </summary>
public sealed class AudioFormat
{
    /// <summary>
    /// RTP 负载类型。
    /// </summary>
    public int PayloadType { get; init; }

    /// <summary>
    /// 编解码类型。
    /// </summary>
    public AudioCodec Codec { get; init; }

    /// <summary>
    /// 采样率。
    /// </summary>
    public int ClockRate { get; init; } = 8000;

    /// <summary>
    /// 声道数。
    /// </summary>
    public int Channels { get; init; } = 1;

    /// <summary>
    /// RTP 映射名。
    /// </summary>
    public string EncodingName => Codec switch
    {
        AudioCodec.Pcmu => "PCMU",
        AudioCodec.Pcma => "PCMA",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// 创建静态 RTP 音频格式。
    /// </summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>创建静态 RTP 音频格式得到的AudioFormat对象。</returns>
    public static AudioFormat CreateStatic(AudioCodec codec)
    {
        return codec switch
        {
            AudioCodec.Pcmu => new AudioFormat { Codec = codec, PayloadType = 0, ClockRate = 8000, Channels = 1 },
            AudioCodec.Pcma => new AudioFormat { Codec = codec, PayloadType = 8, ClockRate = 8000, Channels = 1 },
            _ => new AudioFormat { Codec = AudioCodec.Unknown, PayloadType = -1 },
        };
    }

    /// <summary>
    /// 从 SDP 的 rtpmap 信息创建音频格式。
    /// </summary>
    /// <param name="payloadType">RTP 负载类型。</param>
    /// <param name="encodingName">媒体编码名称。</param>
    /// <param name="clockRate">RTP 媒体时钟频率，单位为赫兹。</param>
    /// <param name="channels">交错 PCM 数据的声道数。</param>
    /// <returns>从 SDP 的 rtpmap 信息创建音频格式得到的AudioFormat对象。</returns>
    public static AudioFormat FromRtpMap(int payloadType, string encodingName, int clockRate, int channels = 1)
    {
        var codec = encodingName.ToUpperInvariant() switch
        {
            "PCMU" => AudioCodec.Pcmu,
            "PCMA" => AudioCodec.Pcma,
            _ => AudioCodec.Unknown,
        };

        return new AudioFormat
        {
            PayloadType = payloadType,
            Codec = codec,
            ClockRate = clockRate,
            Channels = channels,
        };
    }
}

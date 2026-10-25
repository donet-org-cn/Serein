namespace Serein.Media.Relay;

/// <summary>
/// RTP 服务端实时转写接口。
/// </summary>
public interface IRtpAudioTranscriber
{
    /// <summary>推送一帧 PCM16 音频给转写器。实现方应快速返回或自行排队。</summary>
    /// <param name="context">当前操作的上下文。</param>
    /// <param name="pcm">PCM 音频样本。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>推送一帧 PCM16 音频给转写器。实现方应快速返回或自行排队完成时结束的异步任务。</returns>
    ValueTask PushPcmAsync(RtpAudioProcessingContext context, ReadOnlyMemory<short> pcm, CancellationToken cancellationToken = default);
}

/// <summary>
/// RTP 服务端处理后音频输出接口。
/// </summary>
public interface IRtpProcessedAudioSink
{
    /// <summary>写入处理后的 PCM16 音频。</summary>
    /// <param name="context">当前操作的上下文。</param>
    /// <param name="pcm">PCM 音频样本。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>写入处理后的 PCM16 音频完成时结束的异步任务。</returns>
    ValueTask WriteAsync(RtpAudioProcessingContext context, ReadOnlyMemory<short> pcm, CancellationToken cancellationToken = default);
}

/// <summary>
/// RTP 音频处理上下文。
/// </summary>
/// <param name="StreamId">媒体流标识。</param>
/// <param name="Channel">媒体通道。</param>
/// <param name="Codec">媒体编码格式。</param>
/// <param name="TimestampMs">时间戳，单位为毫秒。</param>
/// <param name="SampleRate">音频采样率，单位为赫兹。</param>
/// <param name="Channels">交错 PCM 数据的声道数。</param>
public readonly record struct RtpAudioProcessingContext(
    string StreamId,
    string Channel,
    MediaCodec Codec,
    long TimestampMs,
    int SampleRate,
    int Channels);

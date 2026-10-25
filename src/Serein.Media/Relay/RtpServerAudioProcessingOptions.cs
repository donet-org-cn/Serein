namespace Serein.Media.Relay;

/// <summary>
/// RTP 服务端音频处理配置。
/// <para>处理链路运行在后台有界队列中，不阻塞 RTP 收包、发布和转发。</para>
/// </summary>
public sealed class RtpServerAudioProcessingOptions
{
    /// <summary>是否启用音频处理。</summary>
    public bool Enabled { get; set; }

    /// <summary>是否启用实时转写。</summary>
    public bool EnableTranscription { get; set; }

    /// <summary>是否录制解码后的音频 WAV。</summary>
    public bool EnableAudioRecording { get; set; }

    /// <summary>WAV 录音文件路径模板，支持 {StreamId}、{Channel}、{Date}、{Time}、{DateTime}。</summary>
    public string AudioRecordPath { get; set; } = "recordings/{StreamId}/{Channel}/{DateTime}.wav";

    /// <summary>后台队列容量。</summary>
    public int QueueCapacity { get; set; } = 128;

    /// <summary>队列满时是否丢弃旧帧。开启后可保护 RTP 主链路速度。</summary>
    public bool DropOldestWhenBusy { get; set; } = true;

    /// <summary>音频采样率。PS/RTP 常见 G.711 为 8000。</summary>
    public int SampleRate { get; set; } = 8000;

    /// <summary>声道数。</summary>
    public int Channels { get; set; } = 1;

    /// <summary>实时转写适配器，可由 sherpa-onnx streaming recognizer 实现。</summary>
    public IRtpAudioTranscriber? Transcriber { get; set; }

    /// <summary>处理后的 PCM 输出，可用于监听、二次推流或业务分析。</summary>
    public IRtpProcessedAudioSink? ProcessedAudioSink { get; set; }
}

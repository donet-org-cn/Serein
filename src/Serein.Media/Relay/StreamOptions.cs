namespace Serein.Media.Relay;

/// <summary>
/// 流注册选项。控制流的生命周期行为。
/// </summary>
public sealed class StreamOptions
{
    /// <summary>流 ID。</summary>
    public string StreamId { get; init; } = string.Empty;

    /// <summary>通道 ID。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>流来源协议。</summary>
    public StreamSource Source { get; init; } = StreamSource.Unknown;

    /// <summary>
    /// 无人观看时是否自动断流（停止上游推流）。
    /// <para>
    /// 开启后，当最后一个订阅者离开时，Hub 会触发 <see cref="MediaStreamHub.StreamAutoStopRequiredAsync"/> 事件，
    /// 上游应响应此事件停止推流。当有新订阅者时，触发 <see cref="MediaStreamHub.StreamAutoStartRequiredAsync"/> 重新拉流。
    /// </para>
    /// </summary>
    public bool AutoStopWhenNoViewers { get; set; }

    /// <summary>
    /// 自动断流等待时间（秒）。在无订阅者后等待一段时间再断流，避免短暂断开后立即重连。
    /// <para>默认 5 秒。</para>
    /// </summary>
    public int AutoStopDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 是否保存流到文件。
    /// <para>
    /// 开启后，Hub 会在收到帧时同时写入录制文件。
    /// 文件路径由 <see cref="RecordPath"/> 指定。
    /// </para>
    /// </summary>
    public bool RecordToFile { get; set; }

    /// <summary>
    /// 录制文件保存路径模板。
    /// <para>
    /// 支持占位符: {StreamId}, {Channel}, {Date}, {Time}, {DateTime}
    /// 例如: "recordings/{StreamId}/{Channel}/{DateTime}.flv"
    /// </para>
    /// </summary>
    public string RecordPath { get; set; } = "recordings/{StreamId}/{Channel}/{DateTime}.flv";

    /// <summary>
    /// 录制格式。
    /// </summary>
    public RecordFormat RecordFormat { get; set; } = RecordFormat.FLV;

    /// <summary>
    /// 录制写盘队列容量。录制在后台顺序写入；队列拥塞时优先保护实时媒体链路，
    /// 视频会等待下一关键帧后恢复录制。默认 256 帧。
    /// </summary>
    public int RecordingQueueCapacity { get; set; } = 256;

    /// <summary>
    /// 订阅缓冲区容量。
    /// </summary>
    public int SubscriptionCapacity { get; set; } = 256;

    /// <summary>
    /// 最大允许的订阅者数量。0 表示不限制。
    /// </summary>
    public int MaxSubscribers { get; set; }

    /// <summary>
    /// RTP 服务端音频处理配置。用于后台转写、降噪和 WAV 录音。
    /// </summary>
    public RtpServerAudioProcessingOptions AudioProcessing { get; set; } = new();
}

/// <summary>流来源协议。</summary>
public enum StreamSource
{
    /// <summary>未知。</summary>
    Unknown = 0,

    /// <summary>RTSP 拉流。</summary>
    Rtsp = 1,

    /// <summary>RTP 承载的 PS 媒体流。</summary>
    PsRtp = 2,

    /// <summary>标准 RTP。</summary>
    Rtp = 3,

    /// <summary>应用直接发布的媒体帧。</summary>
    Application = 4,
}

/// <summary>录制格式。</summary>
public enum RecordFormat
{
    /// <summary>FLV 格式。</summary>
    FLV = 0,

    /// <summary>裸 ES 流（H.264/H.265 Annex B）。</summary>
    RawES = 1,
}

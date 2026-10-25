# Serein.Media

## 帧与中心

`PooledMediaFrame.Create(data, kind, codec, isKeyFrame, timestampMs)` 创建池化帧。使用 `TryRetain()` 增加引用并检查是否成功，`Release()` 归还引用；`GetSpan()` / `GetMemory()` 只能在持有引用时访问。

`MediaStreamHub`（`Serein.Media.Relay`）的主要操作：

| API | 用途 |
| --- | --- |
| `RegisterStream(StreamOptions)` | 定义流、录制、队列与自动停止行为 |
| `Publish` / `PublishAndRelease` | 发布媒体帧；后者消费调用方引用 |
| `SubscribeAsync` / `SubscribeLiveAsync` | 订阅帧；实时订阅提供过载后的关键帧恢复 |
| `TryGetVideoInfo` | 读取缓存的视频编码与参数集 |
| `MarkStreamConnected` / `MarkStreamDisconnectedAsync` | 更新上游媒体状态 |
| `UnregisterStreamAsync` | 注销媒体流并回收订阅、队列与录制资源 |
| `FramePublishedCallback` | 同步借用帧的输出回调 |

生命周期、观看人数、每秒统计、质量与录制状态均有事件。`StreamAutoStartRequiredAsync` / `StreamAutoStopRequiredAsync` 是扩展点；应用决定如何控制上游。

## RTP / PS

- `StandardRtpHeader`、`RtpPacketBuilder`、`RtpPayloadTypes`：标准 RTP 头与数据包构造。
- `PsParser`、`PsMuxer`、`PsPesFrame`：节目流与 PES 处理。
- `RtpClient`：接收标准 RTP；可配置为 PS 解封装或原始 H.264/H.265 组帧。
- `PsRtpReceiver`：多 SSRC 的 PS/RTP 输入，包含有界重排、丢包与队列统计，支持 UDP 和 TCP 输入。
- `PsRtpSource`：把接收器的媒体帧与生命周期桥接到共享 Hub。

`RegisterStream(streamId, channelId, ssrc)` 仅绑定媒体路由。`GetIngressStatistics(ssrc)` 返回该 SSRC 的不可变入站统计快照。

## 音频

`Serein.Media.Audio` 提供 `G711Codec`、`PcmAudioConverter`、`AudioFileDecoder`、`WaveFileWriter`、`AudioFormat`、`RtpAudioPacketBuilder`。

`PcmAudioConverter.ConvertToMono8k` 把 PCM16 转成 8 kHz 单声道。`AudioFileDecoder.DecodeToMono8kPcm` 支持 WAV 与托管 MP3 解码。媒体中心也提供音频处理扩展接口和 PCM/WAV 录制器。

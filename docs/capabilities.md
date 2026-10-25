# 能力对照与范围

参考 [ZLMediaKit 官方介绍](https://docs.zlmediakit.com/zh/) 的多协议媒体处理结构。以下“已有”均指本仓库源码中的实现，不表示与其 API、完整协议覆盖或性能等价。

| 能力 | Serein 当前状态 |
| --- | --- |
| RTSP 拉流 | 已有：UDP、TCP interleaved、Digest、连接超时 |
| RTSP 输出 | 已有：RTSP 控制与 RTP/UDP；无 TCP interleaved 输出 |
| RTP / PS | 已有：标准 RTP、PS 封装/解封装、H.264/H.265 重组、PS/RTP 入站重排 |
| JT1078 原始音视频输入 | 尚未恢复；媒体解析可独立于 JT808 信令实现 |
| HTTP / WS-FLV | 已有：共享媒体 Hub、参数集缓存、迟加入、关键帧恢复 |
| H.265 FLV | 已有 Enhanced FLV；客户端需支持 HEVC / hvc1 |
| 文件 | 已有 FLV、原始 ES 和 WAV；无 MP4 |
| 音频处理 | 已有 G.711、PCM 重采样、WAV 和托管 MP3 解码 |
| AAC | 有负载标识和封装路径；不是通用 AAC 编解码器 |
| RTMP | 未实现 |
| HLS / TS / fMP4 | 未实现 |
| WebRTC / ICE / DTLS / SRTP | 未实现 |
| 按需开关 | Hub 有自动启停事件；默认配置的 RTSP 源持续运行并重连 |
| 状态 API | 已有只读配置源列表；无 ZLMediaKit API 兼容层 |
| WebHook / 集群 | 未实现；应用可消费库事件自行扩展 |
| 设备注册与控制 | 不包含：由外部业务系统完成 |

本仓库没有生产设备兼容性认证或大规模负载结论。现有验证覆盖合成媒体、回环网络、封装边界、队列压力和生命周期，不代表具体设备环境的吞吐或延迟。

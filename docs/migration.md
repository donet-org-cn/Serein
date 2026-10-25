# 迁移与命名

本仓库仅从原项目抽取流媒体处理代码。原项目保持不变。

| 原名称 | 新名称 |
| --- | --- |
| `Qcxt.Net.Rtp` / `Qcxt.Net.RTP` | `Serein.Media` |
| `Qcxt.Net.RTSP` | `Serein.Rtsp` |
| `Qcxt.Net.Rtp.AspNetCore` | `Serein.AspNetCore` |
| `Gb28181RtpServer` | `PsRtpReceiver` |
| `Gb28181Adapter` | `PsRtpSource` |
| `RegisterSession` / `UnregisterSession` | `RegisterStream` / `UnregisterStream` |
| `DeviceId`（媒体会话） | `StreamId` |
| `VoipPcmAudioConverter` | `Serein.Media.Audio.PcmAudioConverter` |
| `VoipAudioFileDecoder` | `Serein.Media.Audio.AudioFileDecoder` |
| `VoipWaveFileWriter` | `Serein.Media.Audio.WaveFileWriter` |

`Serein.Hosting` 是新增的统一托管入口。旧私有基础包与私有包源均已移除，目标框架统一为 .NET 10，版本从 `0.1.0-preview.1` 开始。

原 SIP、国标信令、JT808/JT1078、ONVIF、PBX 与设备适配器不导入此库。需要这些业务时，请在独立项目中调用 Serein 的媒体输入/输出 API。

流标识和通道由应用定义；SSRC 映射仍用于将 RTP 包送到正确的媒体流。`StreamSource` 现在只有 Unknown、Rtsp、PsRtp、Rtp 和 Application。没有公共包发布前的二进制兼容承诺，迁移时需重新编译调用方。

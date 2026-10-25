# Serein · 纯 .NET 流媒体引擎

Serein（/səˈreɪn/）专注于媒体接入、解包、转封装、分发与录制。它从已有 .NET 媒体代码中提取实现，以统一媒体帧和 `MediaStreamHub` 连接 RTSP/RTP 输入与 RTSP、HTTP-FLV、WebSocket-FLV 和文件输出。

**C# / .NET 10，纯托管实现。** 不依赖 ZLMediaKit、FFmpeg、原生编解码 DLL、外部转码进程或私有 NuGet 包。MP3 解码使用托管库 NLayer。

只包含流媒体处理。设备注册、SIP 信令、国标目录/级联、JT808/JT1078 设备服务、ONVIF 和电话交换业务均不在本仓库中。

当前版本：`0.1.0-preview.1`，已通过 GitHub Actions 发布到 NuGet，可直接安装：

```sh
dotnet add package Serein.AspNetCore --version 0.1.0-preview.1
```

只使用媒体内核时安装 `Serein.Media`。包列表：[Media](https://www.nuget.org/packages/Serein.Media/0.1.0-preview.1)、[Rtsp](https://www.nuget.org/packages/Serein.Rtsp/0.1.0-preview.1)、[Hosting](https://www.nuget.org/packages/Serein.Hosting/0.1.0-preview.1)、[AspNetCore](https://www.nuget.org/packages/Serein.AspNetCore/0.1.0-preview.1)。

## 当前能力

| 输入与处理 | 输出与服务 |
| --- | --- |
| RTSP 拉流：UDP / TCP interleaved、Digest | RTSP 服务：RTP/UDP 输出 |
| 标准 RTP、RTP 承载的 PS | HTTP-FLV / WebSocket-FLV / TCP-FLV |
| H.264/H.265 NAL 重组、PS 封装/解封装 | H.264 FLV / H.265 Enhanced FLV |
| G.711 A/μ-law、PCM 重采样、WAV、托管 MP3 解码 | FLV、原始 ES、PCM/WAV 录制 |
| 池化帧、引用计数、多订阅、关键帧恢复 | 连接、观看、质量、统计与录制事件 |
| 有界队列、丢包统计、PS/RTP 重排 | DI、配置源自动重连、只读状态 API |

视频链路主要做解包、重新封装和转发，不执行视频解码后再编码。**RTMP、HLS、TS、fMP4、MP4 和 WebRTC 尚未实现**，也不提供通用 H.264/H.265/AAC/Opus 编解码。借鉴 ZLMediaKit 的媒体引擎组织方式，不承诺全部功能或性能等价。详见[能力对照](docs/capabilities.md)。

国标视频的标准 RTP/PS 媒体接入已经保留，不包含 SIP 注册、目录或点播信令。JT1078 的原始音视频报文接入尚未恢复；它属于可独立加入的媒体输入模块，不需要引入 JT808 设备服务。

## 快速运行

需要 .NET 10 SDK：

```sh
dotnet restore Serein.slnx
dotnet build Serein.slnx -c Release --no-restore
dotnet run --project samples/Serein.Server -c Release --no-build
```

默认监听 `http://127.0.0.1:5080`，不连接任何设备：

- `GET /api/status`：服务信息。
- `GET /api/streams`：配置的 RTSP 源状态，不含源地址与密码。

把 `samples/Serein.Server/appsettings.json` 中的 `Serein` 配置改为：

```json
{
  "Sources": [
    {
      "StreamId": "camera",
      "Channel": "main",
      "Url": "rtsp://127.0.0.1:8554/live",
      "Transport": "Tcp",
      "UsePsDemux": false,
      "RecordToFile": false
    }
  ],
  "RtspOutput": {
    "Enabled": true,
    "ListenAddress": "127.0.0.1",
    "Port": 8555
  }
}
```

将 `Url` 换为你的源地址。账号使用 `Username`、`Password` 字段，或环境变量 `Serein__Sources__0__Password`。普通 H.264/H.265 RTP 负载使用 `UsePsDemux: false`，PS 负载使用 `true`。

播放地址：

- `http://127.0.0.1:5080/live/camera/main.flv`
- `ws://127.0.0.1:5080/ws-live/camera/main`
- `rtsp://127.0.0.1:8555/camera/main`（播放器使用 UDP 传输）

FLV 播放需要支持相应编码的客户端；示例不包含浏览器解码器。

## 作为库使用

安装 `Serein.AspNetCore` NuGet 包，或引用 `src/Serein.AspNetCore/Serein.AspNetCore.csproj`：

```csharp
using Serein.AspNetCore;
using Serein.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerein(options =>
{
    options.Sources.Add(new RtspSourceOptions
    {
        StreamId = "camera",
        Channel = "main",
        Url = "rtsp://127.0.0.1:8554/live"
    });
});

var app = builder.Build();
app.UseWebSockets();
app.UseHttpFlv();
app.UseWebSocketFlv();
app.MapSereinApi(); // 可继续调用 RequireAuthorization()
app.Run();
```

通过 PS/RTP 接入已有媒体流，无需设备信令：

```csharp
using System.Net;
using Serein.Media.Adapters;
using Serein.Media.Relay;
using Serein.Media.Server;

await using var hub = new MediaStreamHub();
await using var receiver = new PsRtpReceiver
{
    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 6000)
};
await using var source = new PsRtpSource(receiver, hub);
source.RegisterStream("camera", "main", ssrc: 1234);
await source.StartAsync(cancellationToken);
// SSRC 与流标识的绑定只用于媒体路由。上游负责向 6000 端口发送 RTP/PS 数据。
```

应用也可以直接创建 `PooledMediaFrame` 并调用 `hub.PublishAndRelease`，或单独使用 PS、RTP、FLV 和音频工具，不必启动服务器。

## 四个包

| 包 / 命名空间 | 职责 |
| --- | --- |
| `Serein.Media` | 媒体帧、RTP/PS、音频处理、统一媒体中心 |
| `Serein.Rtsp` | RTSP 客户端/服务器、FLV 封装与录制 |
| `Serein.Hosting` | DI、RTSP 源配置、自动重连、可选 RTSP 输出 |
| `Serein.AspNetCore` | HTTP/WS-FLV 和状态 API |

依赖方向：`AspNetCore → Hosting → Rtsp → Media`。媒体内核可以用于控制台和 Worker Service。

## 帧所有权

`PooledMediaFrame.Create` 返回一个引用，持有者必须调用 `Release()`。`Publish` 保留调用方引用，`PublishAndRelease` 消费调用方引用。订阅队列和 `RtspPullClient.FrameReceivedAsync` 交付的每个帧都要在 `finally` 中释放。`PsRtpReceiver.FrameReceivedAsync` 和 `FramePublishedCallback` 借用帧；需要异步持有时先调用 `TryRetain()`，成功后使用并最终释放新增引用。

```csharp
await using var subscription = await hub.SubscribeLiveAsync("camera", "main");
await foreach (var frame in subscription.Reader.ReadAllAsync(cancellationToken))
{
    try { Consume(frame.GetSpan()); }
    finally { frame.Release(); }
}
```

## 验证与打包

测试是可执行回归程序，使用 `dotnet run` 执行：

```sh
dotnet run --project tests/Serein.Media.Tests -c Release --no-build
dotnet run --project tests/Serein.Hosting.Tests -c Release --no-build
dotnet pack Serein.slnx -c Release --no-build -o artifacts/packages
```

一键验证：`pwsh -File tools/verify.ps1`；Windows PowerShell 可使用 `powershell -ExecutionPolicy Bypass -File tools/verify.ps1`。CI 配置覆盖 Windows / Linux；实际执行结果见[验证记录](docs/verification.md)。

NuGet 发布流水线：[Publish NuGet](https://github.com/hnlyf168/Serein/actions/workflows/publish-nuget.yml)。推送 `v0.1.0-preview.1` 这类版本标签时，在 Windows/Linux 验证通过后发布四个包；手动运行默认只验证打包。首次发布需配置 NuGet 身份，见[发布指南](docs/publishing.md)。

## 文档

- [架构与扩展](docs/architecture.md)
- [能力对照](docs/capabilities.md)
- [迁移与命名](docs/migration.md)
- [部署与配置](docs/deployment.md)
- [NuGet 发布与凭据配置](docs/publishing.md)
- [API 索引](docs/api/README.md)
- [贡献指南](CONTRIBUTING.md)、[变更记录](CHANGELOG.md)

采用 [MIT 许可证](LICENSE)。保留原作者归属，见 [NOTICE](NOTICE)。第三方依赖见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

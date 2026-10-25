# 部署与配置

直接运行：

```sh
dotnet run --project samples/Serein.Server -c Release
```

发布框架依赖服务器：

```sh
dotnet publish samples/Serein.Server -c Release --no-self-contained -o artifacts/server
dotnet artifacts/server/Serein.Server.dll
```

服务器需要 .NET 10 ASP.NET Core Runtime。纯库场景不需要部署示例服务器。

## 配置

`Serein:Sources` 是 RTSP 源数组，默认空。每个源的 `StreamId`、`Channel` 允许 ASCII 字母、数字、`-`、`_`，最长 128 字符，组合不能重复。

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Transport` | `Tcp` | 输入的 RTP 传输模式，`Tcp` 或 `Udp` |
| `UsePsDemux` | `false` | 负载是否为 PS |
| `ConnectTimeoutMilliseconds` | 5000 | 连接及 RTSP 协商超时 |
| `ReconnectDelayMilliseconds` | 3000 | 断开后等待重试 |
| `RecordToFile` | false | 启用 FLV 录制；相对当前工作目录写入 recordings |
| `Username` / `Password` | 空 | 输入源鉴权 |

环境变量使用双下划线，例如 `Serein__Sources__0__Url`。`appsettings.Example.json` 是配置参考，默认不会自动作为活动配置加载。

`Serein:RtspOutput` 的 `Enabled` 默认 false，默认地址 `127.0.0.1:8554`。启用后输出路径为 `rtsp://host:port/{streamId}/{channel}`，媒体走 UDP。HTTP 默认地址为 `127.0.0.1:5080`，由 `Urls` 或 `ASPNETCORE_URLS` 修改。

## 状态与认证

`/api/streams` 列出通过 `Sources` 配置的 RTSP 源。`Connected` 表示控制连接已协商成功；`Subscribers` 表示 Hub 中的订阅数，不包含通过同步输出回调发送的 RTSP 观看者。状态从 pending、connecting、connected、retrying 到 stopped，不返回源 URL 或凭据。

示例绑定本机。对外提供服务时，可在调用 `UseHttpFlv` / `UseWebSocketFlv` 前加入应用自己的认证授权中间件，状态路由组支持 `RequireAuthorization()`。独立使用 RTSP 服务器可通过 `AuthenticateRequest` 设置鉴权策略。示例没有内置账户系统。

## NuGet

```sh
dotnet pack Serein.slnx -c Release -o artifacts/packages
dotnet add YourApp.csproj package Serein.AspNetCore --version 0.1.0-preview.1 --source artifacts/packages
```

四个库均已发布 `0.1.0-preview.1` 到公共 NuGet：

```sh
dotnet add YourApp.csproj package Serein.AspNetCore --version 0.1.0-preview.1
```

可以按需将包名替换为 `Serein.Media`、`Serein.Rtsp` 或 `Serein.Hosting`。源代码构建仅访问公开 NuGet 源。

自动发布使用独立的 [NuGet 发布流水线](publishing.md)，支持版本标签和手动运行。配置完成前可先运行不上传包的打包验证。

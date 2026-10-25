# Serein.Rtsp

## RTSP 拉流

`Serein.Rtsp.Client.RtspPullClient` 提供：

- `ConnectAsync(url, cancellationToken)`、`StopAsync`、`DisposeAsync`。
- `Transport`：`RtspTransportMode.Udp` 或 `Tcp`。
- `UsePsDemux`：根据 RTP 负载选择 PS 或原始编码组帧。
- `Username` / `Password`、`ConnectTimeout`。
- `FrameReceivedAsync`、`StreamConnectedAsync`、`StreamDisconnectedAsync`、丢包与统计事件。

`FrameReceivedAsync` 交付给每个订阅者一个帧引用，处理后必须 `Release()`。生产应用可用 `Serein.Hosting` 管理重连与停机。

## RTSP 输出

`Serein.Rtsp.Server.RtspServer` 提供 `ListenEndPoint`、`RtpPortStart`、请求大小/超时限制、`AuthenticateRequest` 与 `ResolveVideoInfo`。

调用 `PushFrame(frame, streamId, channel)` 向相应播放会话分发帧。输入帧为借用，服务器按异步发送需要保留引用。`StartAsync` 返回时监听已绑定；`StopAsync` 与 `DisposeAsync` 回收服务器资源。输出目前使用 UDP RTP。

## FLV 与录制

- `Serein.Rtsp.Mux.FlvMuxer`：将统一媒体帧封装为 FLV 标签，支持 AVC、Enhanced FLV HEVC 与视频配置缓存初始化。
- `TcpFlvServer`：客户端发送 `streamId/channel\n` 后接收 FLV 字节流。
- `FlvStreamBridge`：连接媒体中心与 TCP-FLV 输出。
- `Serein.Rtsp.Relay.StreamRecorder`：后台顺序写入 FLV 或原始 ES，支持停止排空与文件生命周期事件。

HTTP / WebSocket 输出位于 `Serein.AspNetCore`，通过 `UseHttpFlv` / `UseWebSocketFlv` 使用同一个媒体中心。

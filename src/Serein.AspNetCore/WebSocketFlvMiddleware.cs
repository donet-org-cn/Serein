using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serein.Media.Relay;
using Serein.Rtsp.Mux;
using System.Buffers;
using System.Net.WebSockets;

namespace Serein.AspNetCore;

/// <summary>
/// WebSocket-FLV 流媒体中间件。
/// <para>
/// 通过 WebSocket Binary 帧向客户端推送 FLV 音视频流。
/// 请求格式: <c>WebSocket {PathPrefix}/{streamId}/{channel}</c>
/// </para>
/// <para>
/// 基于 <see cref="MediaStreamHub"/> 订阅帧数据，使用 <see cref="FlvMuxer"/> 封装。
/// 协议无关——PS/RTP、RTSP 等任何上游推送的帧都可通过此中间件转为 FLV 实时播放。
/// </para>
/// </summary>
public sealed class WebSocketFlvMiddleware
{
    /// <summary>HTTP 请求管线中的后续中间件。</summary>
    private readonly RequestDelegate _next;
    /// <summary>媒体流注册与订阅中心。</summary>
    private readonly MediaStreamHub _hub;
    /// <summary>服务配置。</summary>
    private readonly RtpFlvOptions _options;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger<WebSocketFlvMiddleware> _logger;

    /// <summary>
    /// 初始化 WebSocketFlvMiddleware 的新实例。
    /// </summary>
    /// <param name="next">下一个条目或序列位置。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    /// <param name="options">配置选项。</param>
    /// <param name="logger">日志记录器。</param>
    public WebSocketFlvMiddleware(RequestDelegate next, MediaStreamHub hub,
        RtpFlvOptions options, ILogger<WebSocketFlvMiddleware> logger)
    {
        _next = next;
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    /// <summary>处理 HTTP 请求。</summary>
    /// <param name="context">当前操作的上下文。</param>
    /// <returns>处理 HTTP 请求完成时结束的异步任务。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpFlvMiddleware.TryMatchPath(context.Request.Path, _options.WebSocketFlvPath, out var streamId, out var channel))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("此端点仅接受 WebSocket 连接").ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("WebSocket-FLV 请求: {StreamId}/{Channel} from {ClientIp}",
            streamId, channel, context.Connection.RemoteIpAddress);

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        var description = $"WS-FLV/{context.Connection.RemoteIpAddress}";
        await using var subscription = await _hub.SubscribeLiveAsync(streamId, channel, description)
            .ConfigureAwait(false);

        using var muxer = new FlvMuxer { HasAudio = _options.HasAudio, HasVideo = _options.HasVideo };

        // 写入 FLV 头
        var header = new byte[FlvMuxer.FlvHeaderSizeTotal];
        muxer.WriteFlvHeaderTo(header);
        await webSocket.SendAsync(header, WebSocketMessageType.Binary, true,
            context.RequestAborted).ConfigureAwait(false);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var receiveTask = ConsumeReceiveAsync(webSocket, streamCts.Token);
        _ = receiveTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            streamCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var flvBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            if (_options.HasVideo &&
                _hub.TryGetVideoInfo(streamId, channel, out var codec, out var videoConfig) &&
                videoConfig is { Length: > 0 })
            {
                var required = FlvMuxer.GetMaxVideoSequenceHeaderSize(codec, videoConfig.Length);
                if (required > flvBuffer.Length)
                {
                    var largerBuffer = ArrayPool<byte>.Shared.Rent(required);
                    ArrayPool<byte>.Shared.Return(flvBuffer);
                    flvBuffer = largerBuffer;
                }

                if (required > 0)
                {
                    var configWritten = muxer.WriteVideoSequenceHeaderTo(codec, videoConfig, flvBuffer);
                    if (configWritten > 0)
                    {
                        await webSocket.SendAsync(flvBuffer.AsMemory(0, configWritten),
                            WebSocketMessageType.Binary, true, streamCts.Token).ConfigureAwait(false);
                    }
                }
            }

            await foreach (var frame in subscription.Reader.ReadAllAsync(streamCts.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (webSocket.State != WebSocketState.Open)
                        break;

                    var required = muxer.GetMaxOutputSize(frame);
                    if (required > flvBuffer.Length)
                    {
                        var largerBuffer = ArrayPool<byte>.Shared.Rent(required);
                        ArrayPool<byte>.Shared.Return(flvBuffer);
                        flvBuffer = largerBuffer;
                    }

                    var written = muxer.MuxPooledFrameTo(frame, flvBuffer);
                    if (written > 0)
                    {
                        await webSocket.SendAsync(flvBuffer.AsMemory(0, written), WebSocketMessageType.Binary, true,
                            streamCts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket-FLV 推送异常: {StreamId}/{Channel}", streamId, channel);
        }
        finally
        {
            streamCts.Cancel();
            ArrayPool<byte>.Shared.Return(flvBuffer);
        }

        if (webSocket.State == WebSocketState.Open)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "流结束",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        await receiveTask.ConfigureAwait(false);
        _logger.LogInformation("WebSocket-FLV 断开: {StreamId}/{Channel}", streamId, channel);
    }

    /// <summary>持续读取 WebSocket 对端消息以观察关闭或取消。</summary>
    /// <param name="webSocket">承载音频数据的 WebSocket 连接。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>持续读取 WebSocket 对端消息以观察关闭或取消完成时结束的异步任务。</returns>
    private static async Task ConsumeReceiveAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}

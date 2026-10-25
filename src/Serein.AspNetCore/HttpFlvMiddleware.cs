using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serein.Media.Relay;
using Serein.Rtsp.Mux;
using System.Buffers;

namespace Serein.AspNetCore;

/// <summary>
/// HTTP-FLV 流媒体中间件。
/// <para>
/// 通过 HTTP chunked transfer 向客户端推送 FLV 音视频流。
/// 请求格式: <c>GET {PathPrefix}/{streamId}/{channel}.flv</c>
/// </para>
/// <para>
/// 基于 <see cref="MediaStreamHub"/> 订阅帧数据，使用 <see cref="FlvMuxer"/> 封装。
/// 协议无关——PS/RTP、RTSP 等任何上游推送的帧都可通过此中间件转为 FLV 实时播放。
/// </para>
/// </summary>
public sealed class HttpFlvMiddleware
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
    private readonly ILogger<HttpFlvMiddleware> _logger;

    /// <summary>
    /// 初始化 HttpFlvMiddleware 的新实例。
    /// </summary>
    /// <param name="next">下一个条目或序列位置。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    /// <param name="options">配置选项。</param>
    /// <param name="logger">日志记录器。</param>
    public HttpFlvMiddleware(RequestDelegate next, MediaStreamHub hub,
        RtpFlvOptions options, ILogger<HttpFlvMiddleware> logger)
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
        if (!TryMatchPath(context.Request.Path, _options.HttpFlvPath, out var streamId, out var channel))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("HTTP-FLV 请求: {StreamId}/{Channel} from {ClientIp}",
            streamId, channel, context.Connection.RemoteIpAddress);

        var description = $"HTTP-FLV/{context.Connection.RemoteIpAddress}";
        await using var subscription = await _hub.SubscribeLiveAsync(streamId, channel, description)
            .ConfigureAwait(false);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "video/x-flv";
        context.Response.Headers["Cache-Control"] = "no-cache, no-store";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";

        using var muxer = new FlvMuxer { HasAudio = _options.HasAudio, HasVideo = _options.HasVideo };
        var flvBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            // 先写文件头，再发送流中心缓存的参数集初始化迟加入者，然后开始消费实时帧。
            var header = new byte[FlvMuxer.FlvHeaderSizeTotal];
            muxer.WriteFlvHeaderTo(header);
            await context.Response.Body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);

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
                        await context.Response.Body.WriteAsync(flvBuffer.AsMemory(0, configWritten),
                            context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }

            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            await foreach (var frame in subscription.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                using (frame)
                {
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
                        await context.Response.Body.WriteAsync(flvBuffer.AsMemory(0, written), context.RequestAborted).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP-FLV 推送异常: {StreamId}/{Channel}", streamId, channel);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flvBuffer);
        }

        _logger.LogInformation("HTTP-FLV 断开: {StreamId}/{Channel}", streamId, channel);
    }

    /// <summary>尝试从 HTTP 请求路径提取媒体流与通道标识。</summary>
    /// <param name="path">文件或资源路径。</param>
    /// <param name="prefix">文本或数据前缀。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    internal static bool TryMatchPath(PathString path, string prefix, out string streamId, out string channel)
    {
        streamId = "";
        channel = "";

        var value = path.Value;
        if (value == null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remaining = value.AsSpan(prefix.Length);
        if (remaining.Length == 0) return false;

        // 流路由格式：/{streamId}/{channel}.flv。
        if (remaining[0] == '/') remaining = remaining[1..];

        var slashIdx = remaining.IndexOf('/');
        if (slashIdx < 0) return false;

        streamId = remaining[..slashIdx].ToString();
        var afterSlash = remaining[(slashIdx + 1)..];

        // 去掉 .flv 后缀
        var dotIdx = afterSlash.LastIndexOf('.');
        channel = (dotIdx >= 0 ? afterSlash[..dotIdx] : afterSlash).ToString();

        return !string.IsNullOrEmpty(streamId) && !string.IsNullOrEmpty(channel);
    }
}

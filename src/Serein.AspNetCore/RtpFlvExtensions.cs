using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serein.Media.Relay;

namespace Serein.AspNetCore;

/// <summary>
/// FLV 流媒体配置选项。
/// </summary>
public sealed class RtpFlvOptions
{
    /// <summary>HTTP-FLV 路径前缀，默认 <c>/live</c>。</summary>
    public string HttpFlvPath { get; set; } = "/live";

    /// <summary>WebSocket-FLV 路径前缀，默认 <c>/ws-live</c>。</summary>
    public string WebSocketFlvPath { get; set; } = "/ws-live";

    /// <summary>是否包含音频。</summary>
    public bool HasAudio { get; set; } = true;

    /// <summary>是否包含视频。</summary>
    public bool HasVideo { get; set; } = true;
}

/// <summary>
/// FLV 流媒体服务的 <see cref="IServiceCollection"/> 和 <see cref="IApplicationBuilder"/> 扩展方法。
/// <para>
/// 基于 <see cref="MediaStreamHub"/> 的 pub/sub 模式，协议无关：
/// PS/RTP、RTSP 等任何上游只要调用 <see cref="MediaStreamHub.Publish"/> 推送帧，
/// 下游 FLV 播放器即可通过 HTTP-FLV 或 WebSocket-FLV 实时消费。
/// </para>
/// </summary>
public static class RtpFlvExtensions
{
    /// <summary>
    /// 注册 <see cref="MediaStreamHub"/> 到 DI 容器（Singleton）。
    /// <para>
    /// 如果 <see cref="MediaStreamHub"/> 已由其他服务（如 RTSP 或 PS/RTP）注册，则无需重复调用。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>注册 到 DI 容器（Singleton）。 如果 已由其他服务（如 RTSP 或 PS/RTP）注册，则无需重复调用得到的IServiceCollection对象。</returns>
    public static IServiceCollection AddRtpMediaHub(this IServiceCollection services)
    {
        services.TryAddSingleton<MediaStreamHub>();
        return services;
    }

    /// <summary>
    /// 使用 HTTP-FLV 流媒体中间件。
    /// <para>
    /// 请求格式: <c>GET /live/{streamId}/{channel}.flv</c>
    /// </para>
    /// </summary>
    /// <param name="app">应用程序管道生成器。</param>
    /// <param name="configure">修改服务配置的委托。</param>
    /// <returns>使用 HTTP-FLV 流媒体中间件。 请求格式: GET /live/{streamId}/{channel}.flv得到的IApplicationBuilder对象。</returns>
    public static IApplicationBuilder UseHttpFlv(this IApplicationBuilder app, Action<RtpFlvOptions>? configure = null)
    {
        var options = new RtpFlvOptions();
        configure?.Invoke(options);

        return app.UseMiddleware<HttpFlvMiddleware>(options);
    }

    /// <summary>
    /// 使用 WebSocket-FLV 流媒体中间件。
    /// <para>
    /// 请求格式: <c>WebSocket /ws-live/{streamId}/{channel}</c>
    /// </para>
    /// </summary>
    /// <param name="app">应用程序管道生成器。</param>
    /// <param name="configure">修改服务配置的委托。</param>
    /// <returns>使用 WebSocket-FLV 流媒体中间件。 请求格式: WebSocket /ws-live/{streamId}/{channel}得到的IApplicationBuilder对象。</returns>
    public static IApplicationBuilder UseWebSocketFlv(this IApplicationBuilder app, Action<RtpFlvOptions>? configure = null)
    {
        var options = new RtpFlvOptions();
        configure?.Invoke(options);

        return app.UseMiddleware<WebSocketFlvMiddleware>(options);
    }
}

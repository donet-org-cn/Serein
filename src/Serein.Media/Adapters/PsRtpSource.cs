using Serein.Media.Relay;
using Serein.Media.Server;
using System.Runtime.CompilerServices;

namespace Serein.Media.Adapters;

/// <summary>
/// 将 PS/RTP 媒体接收器连接到统一媒体中心。
/// <para>
/// 将 <see cref="PsRtpReceiver"/> 接收到的媒体帧桥接到 <see cref="MediaStreamHub"/>，
/// 实现 PS/RTP 媒体源通过统一的 Hub 输出 FLV / RTSP。
/// 同时将 RTP 服务端的生命周期事件（连接/断开/丢包）转发到 Hub。
/// </para>
/// </summary>
public sealed class PsRtpSource : IAsyncDisposable
{
    /// <summary>RTP 接入服务器。</summary>
    private readonly PsRtpReceiver _rtpServer;
    /// <summary>媒体流注册与订阅中心。</summary>
    private readonly MediaStreamHub _hub;

    /// <summary>
    /// 创建 PS/RTP 适配器。
    /// </summary>
    /// <param name="rtpServer">RTP 接入服务器。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    public PsRtpSource(PsRtpReceiver rtpServer, MediaStreamHub hub)
    {
        _rtpServer = rtpServer;
        _hub = hub;
        _rtpServer.FrameReceivedAsync += OnFrameReceivedAsync;
        _rtpServer.StreamConnectedAsync += OnStreamConnectedAsync;
        _rtpServer.StreamDisconnectedAsync += OnStreamDisconnectedAsync;
        _rtpServer.PacketLossDetectedAsync += OnPacketLossDetectedAsync;
        _rtpServer.UnknownSsrcReceivedAsync += OnUnknownSsrcReceivedAsync;
    }

    /// <summary>处理媒体帧接收通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="frame">媒体帧。</param>
    /// <returns>处理媒体帧接收通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnFrameReceivedAsync(PsRtpReceiver sender, string streamId, string channelId, PooledMediaFrame frame)
    {
        _hub.Publish(frame, streamId, channelId);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理RTP 丢包检测通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="lostCount">丢失的包数量。</param>
    /// <returns>处理RTP 丢包检测通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnPacketLossDetectedAsync(PsRtpReceiver sender, string streamId, string channelId, int lostCount)
    {
        _hub.ReportPacketLoss(streamId, channelId, lostCount);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理未知同步源收包通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="remoteEP">远端网络端点。</param>
    /// <returns>处理未知同步源收包通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnUnknownSsrcReceivedAsync(PsRtpReceiver sender, uint ssrc, System.Net.IPEndPoint remoteEP)
    {
        _hub.ReportUnknownSsrc(ssrc, remoteEP);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理媒体流连接通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <returns>处理媒体流连接通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnStreamConnectedAsync(PsRtpReceiver sender, string streamId, string channelId)
    {
        _hub.MarkStreamConnected(streamId, channelId);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理媒体流断开通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="reason">状态变化或失败原因。</param>
    /// <returns>处理媒体流断开通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnStreamDisconnectedAsync(PsRtpReceiver sender, string streamId, string channelId, string reason)
    {
        _ = _hub.MarkStreamDisconnectedAsync(streamId, channelId, reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>启动 RTP 接收服务。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动 RTP 接收服务完成时结束的异步任务。</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _rtpServer.StartAsync(cancellationToken);

    /// <summary>停止 RTP 接收服务。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>停止 RTP 接收服务完成时结束的异步任务。</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _rtpServer.StopAsync(cancellationToken);

    /// <summary>注册一个媒体流会话。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    public void RegisterStream(string streamId, string channelId, uint ssrc)
    {
        _hub.RegisterStream(new StreamOptions { StreamId = streamId, Channel = channelId, Source = StreamSource.PsRtp });
        _rtpServer.RegisterStream(streamId, channelId, ssrc);
    }

    /// <summary>注册一个媒体流会话（含选项）。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="options">配置选项。</param>
    public void RegisterStream(string streamId, string channelId, uint ssrc, StreamOptions options)
    {
        _hub.RegisterStream(options);
        _rtpServer.RegisterStream(streamId, channelId, ssrc);
    }

    /// <summary>读取指定 SSRC 的实际入站流量和重排统计。</summary>
    /// <param name="ssrc">注册的同步源编号。</param>
    /// <returns>流量快照；会话不存在时为 null。</returns>
    public PsRtpIngressStatistics? GetIngressStatistics(uint ssrc)
        => _rtpServer.GetIngressStatistics(ssrc);

    /// <summary>注销一个媒体流会话。</summary>
    /// <param name="ssrc">RTP 同步源标识。</param>
    public void UnregisterStream(uint ssrc)
    {
        _rtpServer.UnregisterStream(ssrc);
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        _rtpServer.FrameReceivedAsync -= OnFrameReceivedAsync;
        _rtpServer.StreamConnectedAsync -= OnStreamConnectedAsync;
        _rtpServer.StreamDisconnectedAsync -= OnStreamDisconnectedAsync;
        _rtpServer.PacketLossDetectedAsync -= OnPacketLossDetectedAsync;
        _rtpServer.UnknownSsrcReceivedAsync -= OnUnknownSsrcReceivedAsync;
        await _rtpServer.DisposeAsync().ConfigureAwait(false);
    }
}

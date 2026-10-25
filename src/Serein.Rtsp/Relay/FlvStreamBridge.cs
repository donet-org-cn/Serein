using System.Buffers;
using Microsoft.Extensions.Logging;
using Serein.Media;
using Serein.Media.Relay;
using Serein.Rtsp.Mux;
using Serein.Rtsp.Server;

namespace Serein.Rtsp.Relay;

/// <summary>
/// FLV 流桥接器。
/// <para>
/// 将 <see cref="MediaStreamHub"/> 中的媒体帧通过 <see cref="FlvMuxer"/> 封装为 FLV 格式，
/// 供 <see cref="TcpFlvServer"/> 的播放客户端实时消费。
/// </para>
/// <para>
/// 典型链路：PS/RTP 媒体源 → RTP/PS → PsRtpReceiver → PsRtpSource → MediaStreamHub → FlvStreamBridge → TcpFlvServer → FLV 播放器
/// </para>
/// </summary>
public sealed class FlvStreamBridge : IAsyncDisposable
{
    /// <summary>媒体流注册与订阅中心。</summary>
    private readonly MediaStreamHub _hub;
    /// <summary>TCP FLV 分发服务器。</summary>
    private readonly TcpFlvServer _tcpFlvServer;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// 初始化 FlvStreamBridge 的新实例。
    /// </summary>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    /// <param name="tcpFlvServer">TCP FLV 分发服务器。</param>
    /// <param name="logger">日志记录器。</param>
    public FlvStreamBridge(MediaStreamHub hub, TcpFlvServer tcpFlvServer, ILogger? logger = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _tcpFlvServer = tcpFlvServer ?? throw new ArgumentNullException(nameof(tcpFlvServer));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FlvStreamBridge>.Instance;
    }

    /// <summary>
    /// 启动桥接：设置 TcpFlvServer 的 StreamFeed 回调，
    /// 当播放客户端请求某个流时，从 Hub 订阅帧并用 FlvMuxer 封装后推送。
    /// </summary>
    public void Start()
    {
        _tcpFlvServer.StreamFeed = OnStreamFeedAsync;
        _logger.LogInformation("FLV 流桥接已启动");
    }

    /// <summary>订阅媒体中心并向 TCP FLV 客户端发送封装数据。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="writeCallback">写入封装数据的回调。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>订阅媒体中心并向 TCP FLV 客户端发送封装数据完成时结束的异步任务。</returns>
    private async ValueTask OnStreamFeedAsync(
        string streamId, string channel,
        Func<ReadOnlyMemory<byte>, ValueTask> writeCallback,
        CancellationToken cancellationToken)
    {
        MediaStreamSubscription? subscription = null;
        FlvMuxer? muxer = null;
        byte[]? muxBuffer = null;

        try
        {
            // 从 Hub 订阅帧数据
            subscription = await _hub.SubscribeLiveAsync(streamId, channel,
                $"FLV-Player-{Environment.TickCount64 % 100000}").ConfigureAwait(false);

            muxer = new FlvMuxer();

            // FLV 文件头由 TcpFlvServer 负责；用流中心缓存的参数集初始化迟加入的订阅者，使其不必等待下一组 SPS/PPS。
            if (_hub.TryGetVideoInfo(streamId, channel, out var codec, out var videoConfig) &&
                videoConfig is { Length: > 0 })
            {
                var required = FlvMuxer.GetMaxVideoSequenceHeaderSize(codec, videoConfig.Length);
                if (required > 0)
                {
                    muxBuffer = ArrayPool<byte>.Shared.Rent(required);
                    var configWritten = muxer.WriteVideoSequenceHeaderTo(codec, videoConfig, muxBuffer);
                    if (configWritten > 0)
                        await writeCallback(muxBuffer.AsMemory(0, configWritten)).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("FLV 流订阅开始: {StreamId}/{Channel}", streamId, channel);

            // 持续读取帧并封装
            await foreach (var frame in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // 获取输出缓冲区
                    var required = muxer.GetMaxOutputSize(frame);
                    if (required <= 0) continue;

                    if (muxBuffer == null || muxBuffer.Length < required)
                    {
                        var largerBuffer = ArrayPool<byte>.Shared.Rent(required);
                        var previousBuffer = muxBuffer;
                        muxBuffer = largerBuffer;
                        if (previousBuffer != null)
                            ArrayPool<byte>.Shared.Return(previousBuffer);
                    }

                    var written = muxer.MuxPooledFrameTo(frame, muxBuffer);
                    if (written > 0)
                    {
                        await writeCallback(muxBuffer.AsMemory(0, written)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    frame.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FLV 流推送异常: {StreamId}/{Channel}", streamId, channel);
        }
        finally
        {
            _logger.LogInformation("FLV 流订阅结束: {StreamId}/{Channel}", streamId, channel);

            if (muxBuffer != null)
                ArrayPool<byte>.Shared.Return(muxBuffer);
            muxer?.Dispose();
            if (subscription != null)
                await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        _tcpFlvServer.StreamFeed = null;
        return ValueTask.CompletedTask;
    }
}

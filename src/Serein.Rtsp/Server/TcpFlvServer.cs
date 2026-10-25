using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Serein.Rtsp.Server;

/// <summary>
/// 高性能 TCP FLV 推送服务器。
/// <para>
/// 下游消费者通过 HTTP 或 TCP 连接并发送订阅请求（"streamId/channel\n"），
/// 服务器将对应的 FLV 流实时推送。使用 <see cref="ArrayPool{T}"/> 和直写 Span 实现零拷贝。
/// </para>
/// </summary>
public sealed class TcpFlvServer : BackgroundService, IAsyncDisposable
{
    /// <summary>当前 TCP 连接监听器。</summary>
    private Socket? _listener;
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>当前各客户端连接的处理任务索引。</summary>
    private readonly ConcurrentDictionary<string, Task> _clientTasks = new();
    /// <summary>当前异步执行上下文中的客户端回调作用域。</summary>
    private readonly AsyncLocal<ServerClientScope?> _clientScope = new();
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>FLV 数据来源委托。</summary>
    public Func<string, string, Func<ReadOnlyMemory<byte>, ValueTask>, CancellationToken, ValueTask>? StreamFeed { get; set; }

    /// <summary>监听端口。</summary>
    public IPEndPoint ListenEndPoint { get; set; } = new(IPAddress.Any, 1080);

    /// <summary>
    /// 初始化 TcpFlvServer 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public TcpFlvServer(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TcpFlvServer>.Instance;
    }

    /// <summary>
    /// 启动 TCP FLV 服务（供外部直接调用）。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动 TCP FLV 服务（供外部直接调用）完成时结束的异步任务。</returns>
    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        try { await _listening.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch when (_listening.Task.IsFaulted)
        {
            // Do not return a startup error while the failed worker still owns the listening socket.
            if (ExecuteTask is { } worker)
            {
                try { await worker.ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    /// <inheritdoc />
    /// <param name="stoppingToken">用于通知后台服务停止的令牌。</param>
    /// <summary>运行TcpFlvServer的后台处理循环，直到宿主请求停止。</summary>
    /// <returns>运行TcpFlvServer的后台处理循环，直到宿主请求停止完成时结束的异步任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            _listener = new Socket(ListenEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(ListenEndPoint);
            _listener.Listen(128);
            _listening.TrySetResult();
            _logger.LogInformation("TCP FLV 服务监听 {EndPoint}", ListenEndPoint);
            await AcceptLoopAsync(lifetime).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _listening.TrySetException(error);
            throw;
        }
        finally
        {
            lifetime.Cancel();
            _listener?.Dispose();
            await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
            _logger.LogInformation("TCP FLV 服务已停止");
        }
    }

    /// <summary>持续接受客户端连接并跟踪其处理任务。</summary>
    /// <param name="lifetime">此服务器运行代次的取消源。</param>
    /// <returns>持续接受客户端连接并跟踪其处理任务完成时结束的异步任务。</returns>
    private async Task AcceptLoopAsync(CancellationTokenSource lifetime)
    {
        var cancellationToken = lifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var clientId = Guid.NewGuid().ToString("N");
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _clientTasks[clientId] = completion.Task;
                _ = HandleTrackedClientAsync(client, clientId, completion, lifetime);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP FLV Accept 异常");
            }
        }
    }

    /// <summary>处理客户端连接并在结束时移除生命周期跟踪。</summary>
    /// <param name="client">客户端连接。</param>
    /// <param name="clientId">客户端标识。</param>
    /// <param name="completion">表示当前处理完成的异步信号。</param>
    /// <param name="lifetime">宿主应用生命周期控制器。</param>
    /// <returns>处理客户端连接并在结束时移除生命周期跟踪完成时结束的异步任务。</returns>
    private async Task HandleTrackedClientAsync(Socket client, string clientId,
        TaskCompletionSource completion, CancellationTokenSource lifetime)
    {
        var scope = new ServerClientScope(lifetime) { Active = false };
        var previous = _clientScope.Value;
        _clientScope.Value = scope;
        try { await HandleClientAsync(client, clientId, lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "TCP FLV client initialization or cleanup failed"); } catch { }
        }
        finally
        {
            scope.Active = false;
            _clientScope.Value = previous;
            try { client.Dispose(); } catch { }
            ((ICollection<KeyValuePair<string, Task>>)_clientTasks).Remove(new(clientId, completion.Task));
            completion.TrySetResult();
        }
    }

    /// <summary>
    /// 处理客户端，并异步完成操作。
    /// </summary>
    /// <param name="client">客户端连接。</param>
    /// <param name="clientId">客户端标识。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>处理客户端，并异步完成操作完成时结束的异步任务。</returns>
    private async Task HandleClientAsync(Socket client, string clientId, CancellationToken cancellationToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = connection.Token;
        Task? peerMonitor = null;
        try
        {
            client.NoDelay = true;
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, leaveOpen: true);

#if NET7_0_OR_GREATER
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
#else
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
            if (string.IsNullOrWhiteSpace(line))
            {
                _logger.LogWarning("TCP FLV 客户端未发送订阅信息");
                return;
            }

            var parts = line.Split('/');
            if (parts.Length < 2)
            {
                _logger.LogWarning("TCP FLV 订阅格式错误: {Line}", line);
                return;
            }

            string streamId = parts[0];
            string channel = parts[1];
            _logger.LogInformation("TCP FLV 客户端订阅 {StreamId}/{Channel}", streamId, channel);

            // 写入 FLV 头
            var headerBuffer = ArrayPool<byte>.Shared.Rent(13);
            var headerSpan = headerBuffer.AsSpan(0, 13);
            "FLV"u8.CopyTo(headerSpan);
            headerSpan[3] = 0x01;
            headerSpan[4] = 0x05;
            BinaryPrimitives.WriteUInt32BigEndian(headerSpan[5..], 9);
            BinaryPrimitives.WriteUInt32BigEndian(headerSpan[9..], 0);
            try
            {
#if NET7_0_OR_GREATER
                await stream.WriteAsync(headerBuffer.AsMemory(0, 13), cancellationToken).ConfigureAwait(false);
#else
                await stream.WriteAsync(headerBuffer, 0, 13, cancellationToken).ConfigureAwait(false);
#endif
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 通过回调获取数据
            var feed = StreamFeed;
            if (feed != null)
            {
                // 订阅行之后 FLV 为单向输出；继续读取仅为观察 EOF/RST，即使媒体源没有新帧也能发现对端关闭。
                peerMonitor = ObservePeerAsync(stream, connection);
                var owner = _clientScope.Value!;
                var scope = new ServerClientScope(owner.Lifetime);
                _clientScope.Value = scope;
                try
                {
                await feed(streamId, channel, async data =>
                {
#if NET7_0_OR_GREATER
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
#else
                    if (MemoryMarshal.TryGetArray(data, out var segment) && segment.Array != null)
                    {
                        await stream.WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
                        try
                        {
                            data.Span.CopyTo(buffer);
                            await stream.WriteAsync(buffer, 0, data.Length, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
#endif
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                }
                finally { scope.Active = false; _clientScope.Value = owner; }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP FLV 客户端异常");
        }
        finally
        {
            connection.Cancel();
            if (peerMonitor != null) await peerMonitor.ConfigureAwait(false);
        }
    }

    /// <summary>观察对端关闭或异常，以便及时结束闲置媒体订阅。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="connection">当前连接及其生命周期状态。</param>
    /// <returns>观察对端关闭或异常，以便及时结束闲置媒体订阅完成时结束的异步任务。</returns>
    private static async Task ObservePeerAsync(NetworkStream stream, CancellationTokenSource connection)
    {
        var buffer = new byte[512];
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(), connection.Token).ConfigureAwait(false) != 0) { }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { connection.Cancel(); }
    }

    /// <inheritdoc />
    /// <summary>停止TcpFlvServer并等待所管理的操作结束。</summary>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>停止TcpFlvServer并等待所管理的操作结束完成时结束的异步任务。</returns>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clientScope.Value is { Active: true } scope)
        {
            scope.Lifetime.Cancel();
            return Task.CompletedTask;
        }
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

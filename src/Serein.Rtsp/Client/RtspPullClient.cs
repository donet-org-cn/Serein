using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serein.Media;
using Serein.Media.Server;

namespace Serein.Rtsp.Client;

/// <summary>
/// RTSP 媒体传输方式。
/// </summary>
public enum RtspTransportMode
{
    /// <summary>RTP/AVP over UDP（默认）。局域网首选，延迟低。</summary>
    Udp,

    /// <summary>
    /// RTP/AVP/TCP interleaved（RFC 2326 §10.12）。
    /// RTP/RTCP 包以 $&lt;channel&gt;&lt;len_hi&gt;&lt;len_lo&gt;&lt;payload&gt; 帧封装，
    /// 复用 RTSP 信令 TCP 连接传输。公网/穿越 NAT/WireGuard 后必用。
    /// </summary>
    Tcp,
}

/// <summary>
/// RTSP 拉流客户端。
/// <para>
/// 用于从 RTSP 源 等设备主动拉取 RTSP 流。
/// 完成 RTSP 信令协商（OPTIONS → DESCRIBE → SETUP → PLAY），
/// 然后通过 <see cref="RtpClient"/> 接收 RTP 媒体数据。
/// </para>
/// </summary>
public sealed class RtspPullClient : IAsyncDisposable
{
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>当前控制连接关联的 RTP 接收器集合。</summary>
    private readonly List<RtpClient> _rtpClients = new(2);
    /// <summary>串行保护连接生命周期的异步锁。</summary>
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    /// <summary>保护媒体帧订阅者列表的互斥锁。</summary>
    private readonly object _frameHandlerLock = new();
    /// <summary>媒体帧事件的组合委托。</summary>
    private Func<RtspPullClient, PooledMediaFrame, ValueTask>? _frameReceivedAsync;
    /// <summary>用于分发的媒体帧订阅者快照。</summary>
    private Delegate[] _frameHandlers = Array.Empty<Delegate>();
    /// <summary>当前 TCP 网络套接字。</summary>
    private Socket? _tcpSocket;
    /// <summary>当前 RTSP 控制连接流。</summary>
    private NetworkStream? _networkStream;
    /// <summary>SIP 或 RTSP 请求序号计数器。</summary>
    private int _cSeq;
    /// <summary>当前会话。</summary>
    private string? _session;

    // Digest 认证状态
    /// <summary>服务端摘要认证域。</summary>
    private string? _authRealm;
    /// <summary>服务端摘要认证随机数。</summary>
    private string? _authNonce;
    /// <summary>服务端协商的摘要保护质量。</summary>
    private string? _authQop;
    /// <summary>服务端返回并要求回传的认证不透明值。</summary>
    private string? _authOpaque;
    /// <summary>当前认证随机数已使用的请求次数。</summary>
    private int _authNc;
    /// <summary>移除用户名和密码后的 RTSP 地址。</summary>
    private string _cleanUrl = ""; // 不含 userInfo 的 URL（用于 RTSP 请求行）

    // TCP interleaved 接收相关
    /// <summary>读取 RTSP 控制消息与交错 RTP 包的任务。</summary>
    private Task? _interleavedReceiveTask;
    /// <summary>控制交错数据接收生命周期的取消源。</summary>
    private CancellationTokenSource? _interleavedCts;
    // interleaved channel → RtpClient 映射（每个轨道占 2 个 channel：RTP + RTCP，只关心偶数 RTP）
    /// <summary>交错通道号对应的媒体接收器索引。</summary>
    private readonly Dictionary<int, RtpClient> _interleavedChannels = new(4);
    /// <summary>增量解析 RTSP 响应和交错媒体数据的读取器。</summary>
    private RtspResponseReader? _responseReader;
    /// <summary>保护当前连接归属的互斥锁。</summary>
    private readonly object _connectionSync = new();
    /// <summary>连接是否正在停止。</summary>
    private bool _connectionStopping;
    /// <summary>当前断连回调所属的执行作用域。</summary>
    private readonly AsyncLocal<DisconnectScope?> _disconnectScope = new();
    /// <summary>标记断连通知所属的控制连接及回调有效期。</summary>
    private sealed class DisconnectScope
    {
        /// <summary>回调关联的控制连接流。</summary>
        internal NetworkStream Stream = null!;
        /// <summary>回调作用域是否仍在执行。</summary>
        internal bool Active = true;
    }

    /// <summary>当前拉取的 RTSP 地址。</summary>
    public string Url { get; private set; } = "";

    /// <summary>TCP 连接及完整 RTSP 握手的总超时（毫秒）；必须大于零。</summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>是否使用 PS 解封装。</summary>
    public bool UsePsDemux { get; set; } = true;

    /// <summary>
    /// 媒体传输方式。默认 <see cref="RtspTransportMode.Udp"/>；公网穿越 NAT 时用 <see cref="RtspTransportMode.Tcp"/>。
    /// </summary>
    public RtspTransportMode Transport { get; set; } = RtspTransportMode.Udp;

    /// <summary>用户名。</summary>
    public string? Username { get; set; }

    /// <summary>密码。</summary>
    public string? Password { get; set; }

    /// <summary>收到完整媒体帧时触发；每个订阅者拥有一个引用，必须在 finally 中 Release。</summary>
    public event Func<RtspPullClient, PooledMediaFrame, ValueTask>? FrameReceivedAsync
    {
        add
        {
            lock (_frameHandlerLock)
            {
                _frameReceivedAsync += value;
                Volatile.Write(ref _frameHandlers, _frameReceivedAsync?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
        remove
        {
            lock (_frameHandlerLock)
            {
                _frameReceivedAsync -= value;
                Volatile.Write(ref _frameHandlers, _frameReceivedAsync?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
    }

    /// <summary>流连接成功。</summary>
    public event Func<RtspPullClient, ValueTask>? StreamConnectedAsync;

    /// <summary>流断开。</summary>
    public event Func<RtspPullClient, string, ValueTask>? StreamDisconnectedAsync;

    /// <summary>检测到丢包。</summary>
    public event Func<RtspPullClient, int, ValueTask>? PacketLossDetectedAsync;

    /// <summary>RTP 统计信息。</summary>
    public RtpStreamStats? Stats => _rtpClients.Count > 0 ? _rtpClients[0].Stats : null;

    /// <summary>是否已连接。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>本地 RTP 接收端口。</summary>
    public int LocalRtpPort => _rtpClients.Count > 0 ? _rtpClients[0].LocalPort : 0;

    /// <summary>
    /// 初始化 RtspPullClient 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public RtspPullClient(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RtspPullClient>.Instance;
    }

    /// <summary>
    /// 连接到 RTSP 服务器并开始拉流。
    /// </summary>
    /// <param name="url">目标资源地址。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>连接到 RTSP 服务器并开始拉流完成时结束的异步任务。</returns>
    public async Task ConnectAsync(string url, CancellationToken cancellationToken = default)
    {
        if (ConnectTimeout <= 0) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await ConnectCoreAsync(url, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                    throw new TimeoutException($"RTSP connection and handshake exceeded {ConnectTimeout} ms.", ex);
                throw;
            }
        }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>建立 RTSP 控制连接并完成媒体轨道握手。</summary>
    /// <param name="url">目标资源地址。</param>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>建立 RTSP 控制连接并完成媒体轨道握手完成时结束的异步任务。</returns>
    private async Task ConnectCoreAsync(string url, CancellationToken cancellationToken)
    {
        Url = url;

        var uri = new Uri(url);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 554;

        // 从 URL 提取用户名密码
        if (!string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(Username))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length >= 1) Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length >= 2) Password = Uri.UnescapeDataString(parts[1]);
        }

        // 构建不含 userInfo 的 URL（用于 RTSP 请求行）
        _cleanUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString().TrimEnd('/');

        _tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await _tcpSocket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        lock (_connectionSync)
        {
            _networkStream = new NetworkStream(_tcpSocket, ownsSocket: true);
            _responseReader = new RtspResponseReader();
            _connectionStopping = false;
        }

        _logger.LogInformation("RTSP 连接 {Host}:{Port}", host, port);

        // 先查询服务端支持的 RTSP 方法。
        await SendRtspRequestAsync("OPTIONS", _cleanUrl, "", cancellationToken).ConfigureAwait(false);
        var optionsResp = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);

        // DESCRIBE（可能需要认证重试）
        var describeResponse = await SendWithAuthRetryAsync("DESCRIBE", _cleanUrl,
            "Accept: application/sdp\r\n", optionsResp, cancellationToken).ConfigureAwait(false);

        // SETUP 所有有效媒体轨道。视频优先，音频其次，保证 HTTP-FLV 能同时输出画面和声音。
        var tracks = ExtractSetupTracks(describeResponse, _cleanUrl);
        if (tracks.Count == 0)
            tracks.Add(new RtspMediaTrack(_cleanUrl, MediaFrameKind.Video, MediaCodec.H264, 90000));

        string? setupResponse = null;
        var isTcp = Transport == RtspTransportMode.Tcp;
        // TCP interleaved: 每个轨道占 2 个 channel（RTP/RTCP）。第 i 个轨道的 RTP channel = i*2。
        var trackIndex = 0;
        foreach (var track in tracks)
        {
            // PS 解封装策略：
            // - 若 SDP 显式声明 PS/MP2T/MP2P，按用户配置（默认启用）走 PS 解封装
            // - 否则（H264/H265/PCMA 等裸 RTP/AVP）强制关闭 PS 解封装，
            //   避免把裸 H.264 RTP payload 误判为 PS 流导致 codec 错乱
            var usePsForThisTrack = track.Codec == MediaCodec.PS && UsePsDemux;

            var rtpClient = new RtpClient(_logger as ILogger<RtpClient> ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RtpClient>.Instance)
            {
                UsePsDemux = usePsForThisTrack,
                RawFrameKind = track.Kind,
                RawCodec = track.Codec == MediaCodec.Unknown
                    ? (track.Kind == MediaFrameKind.Video ? MediaCodec.H264 : MediaCodec.G711A)
                    : (track.Codec == MediaCodec.PS
                        ? (track.Kind == MediaFrameKind.Video ? MediaCodec.H264 : MediaCodec.G711A)
                        : track.Codec),
                RawClockRate = track.ClockRate > 0 ? track.ClockRate : (track.Kind == MediaFrameKind.Video ? 90000 : 8000),
            };

            rtpClient.FrameReceivedAsync += OnRtpFrameReceived;
            rtpClient.StreamConnectedAsync += OnRtpConnected;
            rtpClient.StreamDisconnectedAsync += OnRtpDisconnected;
            rtpClient.PacketLossDetectedAsync += OnRtpPacketLoss;
            // 任何 await 之前先登记套接字所有权，使 SETUP 失败也能回收它。
            lock (_connectionSync) _rtpClients.Add(rtpClient);

            string transportHeader;
            if (isTcp)
            {
                // TCP 交错传输中，RTP 通道为 trackIndex×2，RTCP 通道为 trackIndex×2+1。
                var rtpChannel = trackIndex * 2;
                var rtcpChannel = rtpChannel + 1;
                transportHeader = $"Transport: RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}\r\n";
                _interleavedChannels[rtpChannel] = rtpClient;
                _logger.LogInformation("RTSP SETUP {Kind}/{Codec}/{ClockRate} (TCP interleaved ch={Rtp}): {SetupUrl}",
                    track.Kind, rtpClient.RawCodec, rtpClient.RawClockRate, rtpChannel, track.SetupUrl);
            }
            else
            {
                // UDP: 必须先 StartAsync 绑定本地端口，才能拿到 client_port 写进 Transport 头
                await rtpClient.StartAsync(0).ConfigureAwait(false);
                transportHeader = $"Transport: RTP/AVP;unicast;client_port={rtpClient.LocalPort}-{rtpClient.LocalPort + 1}\r\n";
                _logger.LogInformation("RTSP SETUP {Kind}/{Codec}/{ClockRate}: {SetupUrl}",
                    track.Kind, rtpClient.RawCodec, rtpClient.RawClockRate, track.SetupUrl);
            }

            setupResponse = await SendWithAuthRetryAsync("SETUP", track.SetupUrl,
                transportHeader,
                describeResponse, cancellationToken).ConfigureAwait(false);

            if (ContainsError(setupResponse))
            {
                var statusLine = setupResponse.Split("\r\n", 2)[0];
                throw new InvalidOperationException($"RTSP SETUP 失败: {statusLine}");
            }

            // TCP interleaved 模式下，部分服务器会用 SETUP 响应中的 interleaved 覆盖我们请求的通道，
            // 需要重新建立 channel → rtpClient 映射
            if (isTcp)
            {
                var serverInterleaved = ParseInterleavedChannels(GetHeader(setupResponse, "Transport"));
                if (serverInterleaved.HasValue)
                {
                    // 移除旧的（用我们请求的 channel 注册的），用服务端实际响应的 channel 重建
                    _interleavedChannels.Remove(trackIndex * 2);
                    _interleavedChannels[serverInterleaved.Value.rtpChannel] = rtpClient;
                }
            }

            var session = GetHeader(setupResponse, "Session");
            if (!string.IsNullOrEmpty(session) && string.IsNullOrEmpty(_session))
            {
                var semiIdx = session.IndexOf(';');
                _session = semiIdx > 0 ? session[..semiIdx] : session;
            }

            trackIndex++;
        }

        // PLAY 使用 DESCRIBE 返回的聚合控制地址。部分设备严格区分末尾的 '/'，
        // 例如 Content-Base 为 rtsp://host/stream1/ 时，向 /stream1 发送 PLAY 会无响应并最终断开。
        var playUrl = GetHeader(describeResponse, "Content-Base");
        if (string.IsNullOrWhiteSpace(playUrl))
            playUrl = _cleanUrl;

        // SendRtspRequestAsync 会根据 _session 自动添加 Session 头，这里不能重复添加。
        var playResponse = await SendWithAuthRetryAsync("PLAY", playUrl,
            "Range: npt=0.000-\r\n",
            setupResponse, cancellationToken).ConfigureAwait(false);

        if (ContainsError(playResponse))
        {
            var statusLine = string.IsNullOrEmpty(playResponse)
                ? "服务端关闭连接，未返回响应"
                : playResponse.Split("\r\n", 2)[0];
            throw new InvalidOperationException($"RTSP PLAY 失败: {statusLine}");
        }

        lock (_connectionSync) IsConnected = true;
        _logger.LogInformation("RTSP 拉流开始: {Url}, 传输={Transport}, RTP 轨道数: {TrackCount}",
            _cleanUrl, isTcp ? "TCP interleaved" : "UDP", _rtpClients.Count);

        // UDP 与 TCP 模式都由控制连接决定会话生命周期。
        _interleavedCts = new CancellationTokenSource();
        _interleavedReceiveTask = ReceiveInterleavedLoopAsync(_interleavedCts.Token);
    }

    /// <summary>
    /// 从 SETUP 响应的 Transport 头解析服务端实际使用的 interleaved channel 对。
    /// 形如 "Transport: RTP/AVP/TCP;unicast;interleaved=0-1" → (0, 1)。
    /// </summary>
    /// <param name="transportHeader">RTSP Transport 头内容。</param>
    /// <returns>从 SETUP 响应的 Transport 头解析服务端实际使用的 interleaved channel 对。 形如 "Transport: RTP/AVP/TCP;unicast;interleaved=0-1" → (0, 1)得到的(int rtpChannel, int rtcpChannel)?对象。</returns>
    private static (int rtpChannel, int rtcpChannel)? ParseInterleavedChannels(string? transportHeader)
    {
        if (string.IsNullOrEmpty(transportHeader)) return null;
        var idx = transportHeader.IndexOf("interleaved=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = transportHeader[(idx + "interleaved=".Length)..];
        // 截断到下一个分号或空格
        var end = rest.IndexOfAny(new[] { ';', ' ', '\r', '\n' });
        if (end >= 0) rest = rest[..end];
        var parts = rest.Split('-', 2);
        if (parts.Length < 1) return null;
        if (!int.TryParse(parts[0].Trim(), out var rtp)) return null;
        var rtcp = parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var r2) ? r2 : rtp + 1;
        return (rtp, rtcp);
    }

    /// <summary>持续读取控制连接上的交错 RTP 数据和 RTSP 响应。</summary>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>持续读取控制连接上的交错 RTP 数据和 RTSP 响应完成时结束的异步任务。</returns>
    private async Task ReceiveInterleavedLoopAsync(CancellationToken cancellationToken)
    {
        var stream = _networkStream!;
        var reader = _responseReader!;
        var clients = _rtpClients.ToArray();
        var channels = new Dictionary<int, RtpClient>(_interleavedChannels);
        string reason = "RTSP control connection closed";
        try
        {
            while (await reader.ReadAsync(stream, (channel, packet) =>
            {
                if ((channel & 1) == 0 && channels.TryGetValue(channel, out var client))
                    client.PushRtpPacket(packet.Span);
            }, cancellationToken).ConfigureAwait(false) != null) { }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            reason = "RTSP control connection failed: " + ex.Message;
            try { _logger.LogWarning(ex, "RTSP control receive failed"); } catch { }
        }
        finally { reader.Dispose(); }

        bool notify;
        lock (_connectionSync)
        {
            notify = !_connectionStopping && !cancellationToken.IsCancellationRequested && ReferenceEquals(stream, _networkStream);
            if (notify)
            {
                // 关闭 UDP 接收器前先解除事件绑定，避免旧接收器对新 RTSP 连接发出连接或断连通知。
                foreach (var client in clients) DetachRtpClient(client);
                _connectionStopping = true;
                IsConnected = false;
            }
        }
        if (!notify) return;
        stream.Dispose();
        foreach (var client in clients) await client.DisposeAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(stream, _networkStream)) return;
        var handler = StreamDisconnectedAsync;
        if (handler != null)
        {
            var scope = new DisconnectScope { Stream = stream };
            var previous = _disconnectScope.Value;
            _disconnectScope.Value = scope;
            try
            {
                foreach (Func<RtspPullClient, string, ValueTask> subscriber in handler.GetInvocationList())
                {
                    // 前一个订阅者可能已重连此实例；不把已退役连接的通知发送给新连接的消费者。
                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(stream, _networkStream)) break;
                    try { await subscriber(this, reason).ConfigureAwait(false); }
                    catch (Exception ex) { try { _logger.LogError(ex, "RTSP disconnect handler failed"); } catch { } }
                }
            }
            finally { scope.Active = false; _disconnectScope.Value = previous; }
        }
    }

    /// <summary>解除 RTP 客户端事件绑定并交还旧实例。</summary>
    /// <param name="client">客户端连接。</param>
    private void DetachRtpClient(RtpClient client)
    {
        client.FrameReceivedAsync -= OnRtpFrameReceived;
        client.StreamConnectedAsync -= OnRtpConnected;
        client.StreamDisconnectedAsync -= OnRtpDisconnected;
        client.PacketLossDetectedAsync -= OnRtpPacketLoss;
    }

    /// <summary>
    /// 发送请求并在收到 401 时自动重试（支持 Digest 和 Basic 认证）。
    /// </summary>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="extraHeaders">附加的消息头。</param>
    /// <param name="previousResponse">此前收到的 RTSP 响应。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>异步操作的结果：发送请求并在收到 401 时自动重试（支持 Digest 和 Basic 认证）得到的文本。</returns>
    private async Task<string> SendWithAuthRetryAsync(string method, string url, string extraHeaders,
        string? previousResponse, CancellationToken cancellationToken)
    {
        // 首次尝试：发送带认证的请求
        var response = await SendAndReceiveAsync(method, url, extraHeaders, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("[RTSP] {Method} 首次响应: {Status}", method, response.Split("\r\n", 2)[0]);

        // 检查是否返回 401
        if (IsUnauthorized(response))
        {
            // 解析 WWW-Authenticate 头
            ParseAuthChallenge(response);

            if (_authNonce != null)
            {
                _logger.LogInformation("RTSP 收到 401，使用 Digest 认证重试 {Method}", method);
                // 重试：带 Digest 认证
                response = await SendAndReceiveAsync(method, url, extraHeaders, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("[RTSP] {Method} Digest 重试响应: {Status}", method, response.Split("\r\n", 2)[0]);

                if (IsUnauthorized(response))
                    throw new InvalidOperationException($"RTSP {method} 认证失败（401 Unauthorized），请检查用户名密码");
            }
            else
            {
                throw new InvalidOperationException($"RTSP {method} 认证失败（401 Unauthorized），请检查用户名密码");
            }
        }

        return response;
    }

    /// <summary>检查 RTSP 响应是否为 401。</summary>
    /// <param name="response">响应消息。</param>
    /// <returns> RTSP 响应是否为 401的判定结果。</returns>
    private static bool IsUnauthorized(string response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        var firstLine = response.Split("\r\n", 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return firstLine.Contains("401");
    }

    /// <summary>解析 WWW-Authenticate 头，支持 Digest 和 Basic。</summary>
    /// <param name="response">响应消息。</param>
    private void ParseAuthChallenge(string response)
    {
        var wwwAuth = GetHeader(response, "WWW-Authenticate");
        if (string.IsNullOrEmpty(wwwAuth)) return;

        _logger.LogDebug("WWW-Authenticate: {Auth}", wwwAuth);

        if (wwwAuth.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            _authRealm = ExtractAuthParam(wwwAuth, "realm");
            _authNonce = ExtractAuthParam(wwwAuth, "nonce");
            _authQop = ExtractAuthParam(wwwAuth, "qop");
            _authOpaque = ExtractAuthParam(wwwAuth, "opaque");
            _authNc = 0;
        }
        // Basic 认证不需要额外处理，SendRtspRequestAsync 已经处理
    }

    /// <summary>从摘要认证头中提取指定参数。</summary>
    /// <param name="challenge">服务端认证质询。</param>
    /// <param name="paramName">参数名（如 username, nonce, response, uri）。</param>
    /// <returns>从摘要认证头中提取指定参数对应的文本。</returns>
    private static string? ExtractAuthParam(string challenge, string paramName)
    {
        // 匹配 realm="xxx" (引号包裹的值)
        var pattern = paramName + "=\"([^\"]+)\"";
        var match = Regex.Match(challenge, pattern, RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        // 匹配 realm=xxx (无引号的值)
        pattern = paramName + "=([^,\\s]+)";
        match = Regex.Match(challenge, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>生成 Digest 认证的 Authorization 头值。</summary>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <returns>生成 Digest 认证的 Authorization 头值对应的文本。</returns>
    private string BuildDigestAuth(string method, string url)
    {
        _authNc++;
        var nc = _authNc.ToString("x8");
        var cnonce = Guid.NewGuid().ToString("N")[..16];
        var ha1 = Md5Hex($"{Username}:{_authRealm}:{Password}");
        var ha2 = Md5Hex($"{method}:{url}");

        string response;
        if (!string.IsNullOrEmpty(_authQop))
        {
            response = Md5Hex($"{ha1}:{_authNonce}:{nc}:{cnonce}:{_authQop}:{ha2}");
        }
        else
        {
            response = Md5Hex($"{ha1}:{_authNonce}:{ha2}");
        }

        var sb = new StringBuilder(512);
        sb.Append($"Digest username=\"{Username}\", realm=\"{_authRealm}\", nonce=\"{_authNonce}\", uri=\"{url}\", response=\"{response}\"");
        if (!string.IsNullOrEmpty(_authQop))
            sb.Append($", qop={_authQop}, nc={nc}, cnonce=\"{cnonce}\"");
        if (!string.IsNullOrEmpty(_authOpaque))
            sb.Append($", opaque=\"{_authOpaque}\"");
        return sb.ToString();
    }

    /// <summary>计算摘要认证所需的 MD5 十六进制文本。</summary>
    /// <param name="input">输入数据。</param>
    /// <returns>计算摘要认证所需的 MD5 十六进制文本得到的文本。</returns>
    private static string Md5Hex(string input)
    {
#if NET7_0_OR_GREATER
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
#else
        using var md5 = MD5.Create();
        return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(input)).Select(b => b.ToString("x2")));
#endif
    }

    /// <summary>从 SDP 中提取 SETUP 轨道，视频轨道优先。</summary>
    /// <param name="describeResponse">DESCRIBE 请求返回的响应。</param>
    /// <param name="baseUrl">解析相对地址所用的基准地址。</param>
    /// <returns>从 SDP 中提取 SETUP 轨道，视频轨道优先得到的条目集合。</returns>
    private static List<RtspMediaTrack> ExtractSetupTracks(string describeResponse, string baseUrl)
    {
        var tracks = new List<RtspMediaTrack>(2);
        var bodyStart = describeResponse.IndexOf("\r\n\r\n");
        if (bodyStart < 0) return tracks;

        var sdp = describeResponse[(bodyStart + 4)..];
        var contentBase = GetHeader(describeResponse, "Content-Base");
        var lines = sdp.Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? control = null;
        MediaFrameKind kind = MediaFrameKind.Video;
        MediaCodec codec = MediaCodec.Unknown;
        int clockRate = 90000;
        var inMedia = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrentTrack();
                inMedia = true;
                control = null;
                codec = MediaCodec.Unknown;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                kind = parts.Length > 0 && parts[0].Equals("m=audio", StringComparison.OrdinalIgnoreCase)
                    ? MediaFrameKind.Audio
                    : MediaFrameKind.Video;
                clockRate = kind == MediaFrameKind.Video ? 90000 : 8000;
                continue;
            }

            if (!inMedia) continue;

            if (line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
            {
                control = line["a=control:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                ParseRtpMap(line, kind, ref codec, ref clockRate);
            }
        }

        AddCurrentTrack();
        tracks.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        return tracks;

        // <summary>将当前解析出的 SDP 媒体轨道加入轨道集合。</summary>
        void AddCurrentTrack()
        {
            if (!inMedia || string.IsNullOrWhiteSpace(control) || control == "*") return;
            var setupUrl = BuildSetupUrl(control, contentBase, baseUrl);
            tracks.Add(new RtspMediaTrack(setupUrl, kind, codec, clockRate));
        }
    }

    /// <summary>
    /// 解析RTP映射。
    /// </summary>
    /// <param name="line">待解析的文本行。</param>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="clockRate">RTP 媒体时钟频率，单位为赫兹。</param>
    private static void ParseRtpMap(string line, MediaFrameKind kind, ref MediaCodec codec, ref int clockRate)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex < 0 || spaceIndex + 1 >= line.Length) return;

        var encoding = line[(spaceIndex + 1)..].Trim();
        var slashIndex = encoding.IndexOf('/');
        var name = slashIndex > 0 ? encoding[..slashIndex] : encoding;
        if (slashIndex > 0)
        {
            var nextSlash = encoding.IndexOf('/', slashIndex + 1);
            var rateText = nextSlash > slashIndex
                ? encoding[(slashIndex + 1)..nextSlash]
                : encoding[(slashIndex + 1)..];
            if (int.TryParse(rateText, out var parsedRate) && parsedRate > 0)
                clockRate = parsedRate;
        }

        codec = name.ToUpperInvariant() switch
        {
            "H264" => MediaCodec.H264,
            "H265" or "HEVC" => MediaCodec.H265,
            "PCMA" => MediaCodec.G711A,
            "PCMU" => MediaCodec.G711U,
            "MPEG4-GENERIC" => MediaCodec.AAC,
            "MP2P" or "MP2T" => MediaCodec.PS,
            _ => kind == MediaFrameKind.Video ? MediaCodec.H264 : MediaCodec.G711A,
        };
    }

    /// <summary>
    /// 构建Setup地址。
    /// </summary>
    /// <param name="mediaControl">SDP 中的媒体控制地址。</param>
    /// <param name="contentBase">RTSP 响应中的资源基准地址。</param>
    /// <param name="baseUrl">解析相对地址所用的基准地址。</param>
    /// <returns>构建Setup地址对应的文本。</returns>
    private static string BuildSetupUrl(string mediaControl, string? contentBase, string baseUrl)
    {
        if (mediaControl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            return mediaControl;

        if (!string.IsNullOrEmpty(contentBase))
            return contentBase.TrimEnd('/') + "/" + mediaControl.TrimStart('/');

        return baseUrl.TrimEnd('/') + "/" + mediaControl.TrimStart('/');
    }

    /// <summary>检查 RTSP 响应是否包含错误状态码（非 2xx）。</summary>
    /// <param name="response">响应消息。</param>
    /// <returns> RTSP 响应是否包含错误状态码（非 2xx）的判定结果。</returns>
    private static bool ContainsError(string response)
    {
        if (string.IsNullOrEmpty(response)) return true;
        var firstLine = response.Split("\r\n", 2, StringSplitOptions.RemoveEmptyEntries)[0];
        // RTSP/1.0 200 OK → 不包含错误
        // RTSP/1.0 401/415/461/etc → 包含错误
        return !firstLine.Contains(" 200 ");
    }

    /// <summary>
    /// 停止拉流并发送 TEARDOWN。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>停止拉流并发送 TEARDOWN完成时结束的异步任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>停止当前连接并清理其媒体与控制通道。</summary>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>停止当前连接并清理其媒体与控制通道完成时结束的异步任务。</returns>
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        bool sendTeardown;
        lock (_connectionSync)
        {
            sendTeardown = IsConnected;
            _connectionStopping = true;
            foreach (var client in _rtpClients) DetachRtpClient(client);
            IsConnected = false;
        }
        if (sendTeardown && _networkStream != null && !string.IsNullOrEmpty(_cleanUrl))
        {
            try
            {
                using var teardown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                teardown.CancelAfter(TimeSpan.FromSeconds(1));
                // SendRtspRequestAsync 会自动添加 Session 头。
                await SendRtspRequestAsync("TEARDOWN", _cleanUrl,
                    "", teardown.Token).ConfigureAwait(false);
            }
            catch { }
        }

        // 停止 TCP interleaved 接收循环（如果在运行）
        var receiveCancellation = _interleavedCts;
        receiveCancellation?.Cancel();
        _interleavedCts = null;
        _networkStream?.Dispose();
        _tcpSocket?.Dispose();
        if (_interleavedReceiveTask == null) _responseReader?.Dispose();
        _responseReader = null;
        if (_interleavedReceiveTask != null)
        {
            bool ownCallback = _disconnectScope.Value is { Active: true } scope && ReferenceEquals(scope.Stream, _networkStream);
            if (!ownCallback)
                try { await _interleavedReceiveTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            _interleavedReceiveTask = null;
        }
        receiveCancellation?.Dispose();

        foreach (var rtpClient in _rtpClients)
        {
            await rtpClient.DisposeAsync().ConfigureAwait(false);
        }
        lock (_connectionSync) _rtpClients.Clear();
        _interleavedChannels.Clear();

        _networkStream?.Dispose();
        _networkStream = null;
        _tcpSocket = null;
        _session = null;
        _cSeq = 0;
        _authRealm = _authNonce = _authQop = _authOpaque = null;
        _authNc = 0;

        _logger.LogInformation("RTSP 拉流停止: {Url}", _cleanUrl);
    }

    /// <summary>处理RTP 媒体帧接收通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="frame">媒体帧。</param>
    /// <returns>处理RTP 媒体帧接收通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnRtpFrameReceived(RtpClient sender, PooledMediaFrame frame)
    {
        var handlers = Volatile.Read(ref _frameHandlers);
        if (handlers.Length == 0)
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        int retained = 1;
        while (retained < handlers.Length && frame.TryRetain()) retained++;
        List<Task>? pending = null;
        for (int index = 0; index < retained; index++)
        {
            try
            {
                var result = ((Func<RtspPullClient, PooledMediaFrame, ValueTask>)handlers[index])(this, frame);
                if (result.IsCompletedSuccessfully) result.GetAwaiter().GetResult();
                else (pending ??= new List<Task>()).Add(ObserveFrameHandlerAsync(result));
            }
            catch (Exception ex)
            {
                // 消费者即使失败也拥有自身的引用，应在 finally 中释放。
                try { _logger.LogError(ex, "RTSP frame handler failed"); } catch { }
            }
        }
        return pending == null ? ValueTask.CompletedTask : new ValueTask(Task.WhenAll(pending));
    }

    /// <summary>观察异步帧回调完成并处理回调异常。</summary>
    /// <param name="pending">尚未完成的异步操作。</param>
    /// <returns>观察异步帧回调完成并处理回调异常完成时结束的异步任务。</returns>
    private async Task ObserveFrameHandlerAsync(ValueTask pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) { try { _logger.LogError(ex, "RTSP asynchronous frame handler failed"); } catch { } }
    }

    /// <summary>处理RTP 连接建立通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="remoteEp">远端网络端点。</param>
    /// <returns>处理RTP 连接建立通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnRtpConnected(RtpClient sender, IPEndPoint remoteEp)
    {
        lock (_connectionSync)
        {
            if (_connectionStopping || !_rtpClients.Contains(sender)) return ValueTask.CompletedTask;
            IsConnected = true;
        }
        var handler = StreamConnectedAsync;
        if (handler != null)
            return handler(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理RTP 连接断开通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="reason">状态变化或失败原因。</param>
    /// <returns>处理RTP 连接断开通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnRtpDisconnected(RtpClient sender, string reason)
    {
        lock (_connectionSync)
        {
            if (_connectionStopping || !_rtpClients.Contains(sender)) return ValueTask.CompletedTask;
            IsConnected = false;
        }
        var handler = StreamDisconnectedAsync;
        if (handler != null)
            return handler(this, reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>处理RTP 丢包通知，并更新关联状态或转发事件。</summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="lost">累计丢包数量。</param>
    /// <returns>处理RTP 丢包通知，并更新关联状态或转发事件完成时结束的异步任务。</returns>
    private ValueTask OnRtpPacketLoss(RtpClient sender, int lost)
    {
        var handler = PacketLossDetectedAsync;
        if (handler != null)
            return handler(this, lost);
        return ValueTask.CompletedTask;
    }

    #region RTSP 信令

    /// <summary>
    /// 发送Rtsp请求，并异步完成操作。
    /// </summary>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="extraHeaders">附加的消息头。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>发送Rtsp请求，并异步完成操作完成时结束的异步任务。</returns>
    private async Task SendRtspRequestAsync(string method, string url, string extraHeaders, CancellationToken cancellationToken)
    {
        if (_networkStream == null) return;

        var sb = new StringBuilder(512);
        sb.Append($"{method} {url} RTSP/1.0\r\n");
        sb.Append($"CSeq: {++_cSeq}\r\n");
        if (!string.IsNullOrEmpty(_session))
            sb.Append($"Session: {_session}\r\n");

        // 认证：优先 Digest，其次 Basic
        if (!string.IsNullOrEmpty(Username))
        {
            if (_authNonce != null)
            {
                sb.Append($"Authorization: {BuildDigestAuth(method, url)}\r\n");
            }
            else
            {
                sb.Append($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"))}\r\n");
            }
        }

        sb.Append(extraHeaders);
        sb.Append("\r\n");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
#if NET7_0_OR_GREATER
        await _networkStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
#else
        await _networkStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
#endif
        await _networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取响应，并异步完成操作。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>异步操作的结果：读取响应，并异步完成操作对应的文本。</returns>
    private async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var stream = _networkStream ?? throw new IOException("RTSP connection is not open.");
        var reader = _responseReader ?? throw new IOException("RTSP response reader is not available.");
        return await reader.ReadAsync(stream, (channel, packet) =>
        {
            if ((channel & 1) == 0 && _interleavedChannels.TryGetValue(channel, out var client))
                client.PushRtpPacket(packet.Span);
        }, cancellationToken).ConfigureAwait(false) ?? throw new EndOfStreamException("RTSP connection closed before the response.");
    }

    /// <summary>
    /// 发送AndReceive，并异步完成操作。
    /// </summary>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="extraHeaders">附加的消息头。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>异步操作的结果：发送AndReceive，并异步完成操作得到的文本。</returns>
    private async Task<string> SendAndReceiveAsync(string method, string url, string extraHeaders, CancellationToken cancellationToken)
    {
        await SendRtspRequestAsync(method, url, extraHeaders, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取头部。
    /// </summary>
    /// <param name="response">响应消息。</param>
    /// <param name="headerName">消息头名称。</param>
    /// <returns>获取头部对应的文本。</returns>
    private static string? GetHeader(string response, string headerName)
    {
        var lines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
                    return line[(colonIdx + 1)..].Trim();
            }
        }
        return null;
    }

    #endregion

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>SDP 媒体轨道的控制地址、编码及负载类型。</summary>
    /// <param name="SetupUrl">建立媒体轨道所用的 RTSP 地址。</param>
    /// <param name="Kind">媒体或消息类别。</param>
    /// <param name="Codec">媒体编码格式。</param>
    /// <param name="ClockRate">RTP 媒体时钟频率，单位为赫兹。</param>
    private readonly record struct RtspMediaTrack(string SetupUrl, MediaFrameKind Kind, MediaCodec Codec, int ClockRate);
}

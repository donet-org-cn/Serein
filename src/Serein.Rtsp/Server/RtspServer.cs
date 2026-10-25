using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Serein.Media;

namespace Serein.Rtsp.Server;

/// <summary>
/// 轻量级 RTSP 服务器。
/// <para>
/// 支持 RTSP DESCRIBE / SETUP / PLAY / TEARDOWN 信令，
/// 通过 UDP 传输 RTP 包（标准 RFC 3550 格式），兼容 VLC / FFmpeg 等播放器。
/// </para>
/// </summary>
public sealed class RtspServer : BackgroundService, IAsyncDisposable
{
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>当前 TCP 连接监听器。</summary>
    private Socket? _listener;
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>按会话标识保存的活动会话集合。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RtspSession>> _sessions = new();
    /// <summary>保护 RTSP 会话索引的互斥锁。</summary>
    private readonly object _sessionsSync = new();
    /// <summary>当前各客户端连接的处理任务索引。</summary>
    private readonly ConcurrentDictionary<string, Task> _clientTasks = new();
    /// <summary>当前异步执行上下文中的客户端回调作用域。</summary>
    private readonly AsyncLocal<ServerClientScope?> _clientScope = new();
    /// <summary>各媒体流已识别的视频编码。</summary>
    private readonly ConcurrentDictionary<string, MediaCodec> _streamVideoCodec = new();
    /// <summary>各媒体流缓存的视频参数集。</summary>
    private readonly ConcurrentDictionary<string, byte[]> _streamVideoConfig = new();

    /// <summary>已经归还且可再次分配的 RTP 端口。</summary>
    private readonly ConcurrentStack<int> _recycledPorts = new();
    /// <summary>当前 RTP 端口分配偏移。</summary>
    private int _rtpPortOffset;

    /// <summary>RTSP 监听端口。</summary>
    public IPEndPoint ListenEndPoint { get; set; } = new(IPAddress.Any, 8554);

    /// <summary>RTP 数据源端口范围起始。</summary>
    public int RtpPortStart { get; set; } = 10000;

    /// <summary>单个 RTSP 控制请求头的最大字节数。</summary>
    public int MaxRequestHeaderBytes { get; set; } = 32 * 1024;

    /// <summary>单个 RTSP 控制请求正文的最大字节数。</summary>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>从首字节到完整请求（含正文）的最长时间；不限制已播放会话的空闲时间。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>媒体帧到达回调。</summary>
    public Action<PooledMediaFrame, string, string>? OnFrameAvailable { get; set; }

    /// <summary>当 RTSP 客户端请求流时触发。</summary>
    public Func<string, string, CancellationToken, Task>? OnStreamRequested { get; set; }

    /// <summary>解析共享流元数据。</summary>
    public Func<string, string, (MediaCodec Codec, byte[]? VideoConfig)?>? ResolveVideoInfo { get; set; }

    /// <summary>RTSP 认证领域。</summary>
    public string AuthRealm { get; set; } = "RTSP Server";

    /// <summary>RTSP 认证回调。</summary>
    public Func<RtspAuthContext, bool>? AuthenticateRequest { get; set; }

    /// <summary>
    /// 初始化 RtspServer 的新实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public RtspServer(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RtspServer>.Instance;
    }

    /// <summary>Starts the server and waits until its listener is ready.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
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
    /// <summary>运行RtspServer的后台处理循环，直到宿主请求停止。</summary>
    /// <returns>运行RtspServer的后台处理循环，直到宿主请求停止完成时结束的异步任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            _listener = new Socket(ListenEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(ListenEndPoint);
            _listener.Listen(64);
            _listening.TrySetResult();
            _logger.LogInformation("RTSP 服务监听 {EndPoint}", ListenEndPoint);
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
            _logger.LogInformation("RTSP 服务已停止");
        }
    }

    /// <summary>向指定会话推送媒体帧。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    public void PushFrame(PooledMediaFrame frame, string streamId, string channel)
    {
        var key = $"{streamId}/{channel}";

        if (frame.Kind == MediaFrameKind.Video && frame.Codec != MediaCodec.Unknown)
        {
            _streamVideoCodec[key] = frame.Codec;

            if (frame.IsKeyFrame || !_streamVideoConfig.ContainsKey(key))
            {
                var videoConfig = ExtractVideoConfig(frame.GetSpan(), frame.Codec);
                if (videoConfig != null)
                {
                    _streamVideoConfig[key] = videoConfig;
                }
            }
        }

        if (_sessions.TryGetValue(key, out var sessions))
        {
            foreach (var entry in sessions)
            {
                var session = entry.Value;
                if (session.IsPlaying && session.Accepts(frame.Kind))
                {
                    session.QueueFrame(frame);
                }
            }
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
                _logger.LogError(ex, "RTSP Accept 异常");
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
            try { _logger.LogError(ex, "RTSP client initialization or cleanup failed"); } catch { }
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
        string? currentSessionId = null;
        string streamId = "";
        string channel = "0";
        var trackSetups = new Dictionary<string, (IPEndPoint ClientEndpoint, Socket RtpSocket, int ServerRtpPort)>(StringComparer.OrdinalIgnoreCase);
        var ownedSessions = new HashSet<(string StreamKey, string SessionId)>();

        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var requests = new RtspRequestReader(MaxRequestHeaderBytes, MaxRequestBodyBytes, RequestTimeout);
            var writeBuffer = new byte[4096];
            int cSeq = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var requestText = await requests.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestText == null) break;

                if (!TryParseRtspRequest(requestText, out var method, out var url, out var headers))
                    continue;

                if (headers.TryGetValue("CSeq", out var seqStr) && !int.TryParse(seqStr, out cSeq))
                    throw new InvalidDataException("Invalid RTSP CSeq.");

                if (method is "DESCRIBE" or "SETUP" or "PLAY")
                {
                    if (!await TryAuthenticateAsync(stream, writeBuffer, cSeq, method, url, headers, client, cancellationToken).ConfigureAwait(false))
                        continue;
                }

                switch (method)
                {
                    case "OPTIONS":
                        await WriteResponseAsync(stream, writeBuffer, "200 OK", cSeq,
                            "Public: DESCRIBE, SETUP, PLAY, TEARDOWN, OPTIONS\r\n", cancellationToken).ConfigureAwait(false);
                        break;

                    case "DESCRIBE":
                        // 新的描述请求只替代尚未播放的 SETUP 资源；已播放的会话仍归当前 TCP 连接所有。
                        foreach (var setup in trackSetups.Values) ReturnRtpSocket(setup.RtpSocket, setup.ServerRtpPort);
                        trackSetups.Clear();
                        ParseStreamFromUrl(url, ref streamId, ref channel);
                        currentSessionId = Guid.NewGuid().ToString("N");

                        if (OnStreamRequested != null)
                        {
                            await InvokeStreamRequestedAsync(OnStreamRequested, streamId, channel, cancellationToken).ConfigureAwait(false);
                            if (!await WaitForVideoCodecAsync(streamId, channel, cancellationToken).ConfigureAwait(false))
                            {
                                await WriteResponseAsync(stream, writeBuffer, "503 Service Unavailable", cSeq,
                                    "Retry-After: 1\r\n", cancellationToken).ConfigureAwait(false);
                                break;
                            }
                        }

                        var sdp = BuildSdp(streamId, channel);
                        await WriteResponseAsync(stream, writeBuffer, "200 OK", cSeq,
                            "Content-Type: application/sdp\r\n" +
                            $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}\r\n" +
                            $"Session: {currentSessionId}\r\n",
                            cancellationToken, sdp).ConfigureAwait(false);
                        break;

                    case "SETUP":
                        if (currentSessionId == null)
                        {
                            ParseStreamFromUrl(url, ref streamId, ref channel);
                            currentSessionId = Guid.NewGuid().ToString("N");
                        }
                        if (headers.TryGetValue("Transport", out var transport))
                        {
                            var clientRtpEndpoint = ParseTransportHeader(transport, client);
                            var rtpSocket = CreateRtpSocket(out var serverRtpPort);
                            var trackId = ParseTrackIdFromUrl(url);

                            if (clientRtpEndpoint != null)
                            {
                                if (trackSetups.TryGetValue(trackId, out var existing))
                                {
                                    ReturnRtpSocket(existing.RtpSocket, existing.ServerRtpPort);
                                }
                                trackSetups[trackId] = (clientRtpEndpoint, rtpSocket, serverRtpPort);
                            }
                            else
                            {
                                ReturnRtpSocket(rtpSocket, serverRtpPort);
                            }

                            var transportResponse = $"Transport: RTP/AVP;unicast;client_port={clientRtpEndpoint?.Port ?? 0}-{(clientRtpEndpoint?.Port ?? 0) + 1};server_port={serverRtpPort}-{serverRtpPort + 1}\r\n";
                            await WriteResponseAsync(stream, writeBuffer, "200 OK", cSeq,
                                transportResponse +
                                $"Session: {currentSessionId}\r\n", cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case "PLAY":
                        var sessionKey = $"{streamId}/{channel}";
                        if (trackSetups.Count > 0)
                        {
                            lock (_sessionsSync)
                            {
                                ownedSessions.Add((sessionKey, currentSessionId!));
                                _streamVideoConfig.TryGetValue(sessionKey, out var videoConfig);
                                var sessionGroup = _sessions.GetOrAdd(sessionKey, static _ => new ConcurrentDictionary<string, RtspSession>());

                                foreach (var trackSetup in trackSetups)
                                {
                                    var trackId = trackSetup.Key;
                                    var filter = trackId.Equals("track1", StringComparison.OrdinalIgnoreCase)
                                        ? MediaFrameKind.Audio
                                        : MediaFrameKind.Video;
                                    var trackConfig = filter == MediaFrameKind.Video ? videoConfig : null;
                                    var sessionId = $"{currentSessionId}:{trackId}";
                                    var session = new RtspSession(trackSetup.Value.RtpSocket, trackSetup.Value.ClientEndpoint, _logger, trackConfig, filter,
                                        ReturnRtpSocket, trackSetup.Value.ServerRtpPort);
                                    if (sessionGroup.TryRemove(sessionId, out var oldSession))
                                        oldSession.Dispose();

                                    sessionGroup[sessionId] = session;
                                    _logger.LogInformation("RTSP 会话已创建: key={Key}, track={Track}, 发送到 {ClientEP}", sessionKey, trackId, trackSetup.Value.ClientEndpoint);
                                }

                                trackSetups.Clear();
                            }
                        }

                        await WriteResponseAsync(stream, writeBuffer, "200 OK", cSeq,
                            $"Session: {currentSessionId}\r\n" +
                            "Range: npt=0.000-\r\n", cancellationToken).ConfigureAwait(false);
                        break;

                    case "TEARDOWN":
                        await WriteResponseAsync(stream, writeBuffer, "200 OK", cSeq,
                            $"Session: {currentSessionId}\r\n", cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(streamId))
                        {
                            var tk = $"{streamId}/{channel}";
                            RemoveClientSessions(tk, currentSessionId);
                        }
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTSP 客户端异常");
        }
        finally
        {
            foreach (var setup in trackSetups.Values)
            {
                ReturnRtpSocket(setup.RtpSocket, setup.ServerRtpPort);
            }

            foreach (var owned in ownedSessions) RemoveClientSessions(owned.StreamKey, owned.SessionId);

        }
    }

    /// <summary>
    /// 创建RTP套接字。
    /// </summary>
    /// <param name="serverRtpPort">服务端 RTP 端口。</param>
    /// <returns>创建RTP套接字得到的Socket对象。</returns>
    private Socket CreateRtpSocket(out int serverRtpPort)
    {
        while (true)
        {
            if (!_recycledPorts.TryPop(out serverRtpPort))
            {
                long port = (long)RtpPortStart + (long)Interlocked.Increment(ref _rtpPortOffset) * 2;
                if (port < 1 || port > 65534)
                    throw new InvalidOperationException("RTSP RTP port range exhausted or invalid.");
                serverRtpPort = (int)port;
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, serverRtpPort));
                return socket;
            }
            catch
            {
                try { socket.Dispose(); } catch { }
            }
        }
    }

    /// <summary>释放 RTP 套接字并归还可复用端口。</summary>
    /// <param name="socket">当前接收代次独占的套接字。</param>
    /// <param name="serverRtpPort">服务端 RTP 端口。</param>
    private void ReturnRtpSocket(Socket socket, int serverRtpPort)
    {
        try { socket.Dispose(); } catch { }
        if (serverRtpPort > 0)
            _recycledPorts.Push(serverRtpPort);
    }

    /// <summary>验证客户端认证，必要时发送认证质询。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="writeBuffer">写入响应的缓冲区。</param>
    /// <param name="cSeq">SIP 或 RTSP 请求序号。</param>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="headers">消息头集合。</param>
    /// <param name="client">客户端连接。</param>
    /// <param name="cancellationToken">连接的取消令牌。</param>
    /// <returns>异步操作的结果：成功完成验证客户端认证，必要时发送认证质询时为 true，否则为 false。</returns>
    private async ValueTask<bool> TryAuthenticateAsync(NetworkStream stream, byte[] writeBuffer,
        int cSeq, string method, string url, Dictionary<string, string> headers, Socket client, CancellationToken cancellationToken)
    {
        var authHandler = AuthenticateRequest;
        if (authHandler == null) return true;

        RtspAuthContext? authCtx = null;
        if (headers.TryGetValue("Authorization", out var authHeader))
        {
            authCtx = ParseAuthorizationHeader(authHeader, method, url, client);
        }

        if (authCtx != null && InvokeClientCallback(() => authHandler(authCtx)))
        {
            _logger.LogDebug("RTSP 认证通过: {Username} ({Method} {Url})", authCtx.Username, method, url);
            return true;
        }

        _logger.LogWarning("RTSP 认证失败: {Method} {Url} from {RemoteEP}", method, url,
            (client.RemoteEndPoint as IPEndPoint)?.ToString());

        var nonce = GenerateNonce();
        await WriteResponseAsync(stream, writeBuffer, "401 Unauthorized", cSeq,
            $"WWW-Authenticate: Digest realm=\"{AuthRealm}\", nonce=\"{nonce}\"\r\n" +
            $"WWW-Authenticate: Basic realm=\"{AuthRealm}\"\r\n", cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// 解析授权头部。
    /// </summary>
    /// <param name="authHeader">客户端提供的认证头。</param>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="client">客户端连接。</param>
    /// <returns>解析授权头部得到的RtspAuthContext对象。</returns>
    private RtspAuthContext ParseAuthorizationHeader(string authHeader, string method, string url, Socket client)
    {
        var remoteEP = client.RemoteEndPoint as IPEndPoint;

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader[6..].Trim();
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIdx = decoded.IndexOf(':');
                var username = colonIdx > 0 ? decoded[..colonIdx] : decoded;
                var password = colonIdx > 0 ? decoded[(colonIdx + 1)..] : "";
                return new RtspAuthContext
                {
                    Scheme = "Basic",
                    Username = username,
                    Password = password,
                    Method = method,
                    Url = url,
                    RemoteEndPoint = remoteEP,
                    DigestRealm = AuthRealm,
                };
            }
            catch
            {
                return new RtspAuthContext { Scheme = "Basic", Method = method, Url = url, RemoteEndPoint = remoteEP, DigestRealm = AuthRealm };
            }
        }

        if (authHeader.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = ParseDigestParameters(authHeader[7..]);
            return new RtspAuthContext
            {
                Scheme = "Digest",
                Username = parameters.GetValueOrDefault("username", ""),
                Nonce = parameters.GetValueOrDefault("nonce"),
                NonceCount = parameters.GetValueOrDefault("nc"),
                Cnonce = parameters.GetValueOrDefault("cnonce"),
                Qop = parameters.GetValueOrDefault("qop"),
                DigestUri = parameters.GetValueOrDefault("uri"),
                DigestResponse = parameters.GetValueOrDefault("response"),
                Method = method,
                Url = url,
                RemoteEndPoint = remoteEP,
                DigestRealm = AuthRealm,
            };
        }

        return new RtspAuthContext { Scheme = "", Method = method, Url = url, RemoteEndPoint = remoteEP, DigestRealm = AuthRealm };
    }

    /// <summary>
    /// 解析Digest参数集合。
    /// </summary>
    /// <param name="input">输入数据。</param>
    /// <returns>解析Digest参数集合得到的条目集合。</returns>
    private static Dictionary<string, string> ParseDigestParameters(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in input.Split(','))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = pair[..eqIdx].Trim();
                var value = pair[(eqIdx + 1)..].Trim().Trim('"');
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>生成新的摘要认证随机数。</summary>
    /// <returns>生成新的摘要认证随机数对应的文本。</returns>
    private static string GenerateNonce()
    {
        Span<byte> buf = stackalloc byte[16];
        Random.Shared.NextBytes(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    /// <summary>
    /// 尝试解析Rtsp请求。
    /// </summary>
    /// <param name="request">请求消息。</param>
    /// <param name="method">协议方法名称。</param>
    /// <param name="url">目标资源地址。</param>
    /// <param name="headers">消息头集合。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryParseRtspRequest(string request, out string method, out string url,
        out Dictionary<string, string> headers)
    {
        method = "";
        url = "";
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        var firstLine = lines[0].Split(' ');
        if (firstLine.Length < 2) return false;

        method = firstLine[0];
        url = firstLine[1];

        for (int i = 1; i < lines.Length; i++)
        {
            var colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                var key = lines[i][..colonIdx].Trim();
                var value = lines[i][(colonIdx + 1)..].Trim();
                headers[key] = value;
            }
        }

        return true;
    }

    /// <summary>
    /// 解析流来源地址。
    /// </summary>
    /// <param name="url">目标资源地址。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    private static void ParseStreamFromUrl(string url, ref string streamId, ref string channel)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.Segments;
            if (segments.Length >= 3)
            {
                streamId = segments[1].Trim('/');
                channel = segments[2].Trim('/');
            }
            else if (segments.Length >= 2)
            {
                streamId = segments[1].Trim('/');
                channel = "0";
            }
        }
        catch { }
    }

    /// <summary>
    /// 解析Track标识来源地址。
    /// </summary>
    /// <param name="url">目标资源地址。</param>
    /// <returns>解析Track标识来源地址得到的文本。</returns>
    private static string ParseTrackIdFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.Segments;
            if (segments.Length >= 4)
            {
                return segments[3].Trim('/');
            }
        }
        catch { }

        return "track0";
    }

    /// <summary>
    /// 解析Transport头部。
    /// </summary>
    /// <param name="transport">传输协议。</param>
    /// <param name="client">客户端连接。</param>
    /// <returns>解析Transport头部得到的IPEndPoint?对象。</returns>
    private static IPEndPoint? ParseTransportHeader(string transport, Socket client)
    {
        var idx = transport.IndexOf("client_port=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var portStr = transport[(idx + 12)..];
        var dashIdx = portStr.IndexOf('-');
        if (dashIdx > 0)
            portStr = portStr[..dashIdx];

        if (int.TryParse(portStr, out var port))
        {
            var remoteEp = client.RemoteEndPoint as IPEndPoint;
            if (remoteEp != null)
                return new IPEndPoint(remoteEp.Address, port);
        }

        return null;
    }

    /// <summary>
    /// 构建Sdp。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>构建Sdp对应的文本。</returns>
    private string BuildSdp(string streamId, string channel)
    {
        var (codec, videoConfig) = GetVideoInfo(streamId, channel);
        if (codec == MediaCodec.Unknown)
            throw new InvalidOperationException($"流 {streamId}/{channel} 尚未探测到视频编码");

        var videoPayloadType = GetVideoPayloadType(codec);
        var (rtpmap, fmtp) = codec switch
        {
            MediaCodec.H265 => BuildH265Sdp(videoConfig),
            _ => BuildH264Sdp(videoConfig),
        };

        return
            "v=0\r\n" +
            $"o=- 0 0 IN IP4 0.0.0.0\r\n" +
            $"s={streamId}/{channel}\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "t=0 0\r\n" +
            $"m=video 0 RTP/AVP {videoPayloadType}\r\n" +
            $"a=rtpmap:{videoPayloadType} {rtpmap}\r\n" +
            fmtp.Replace(":96 ", $":{videoPayloadType} ") +
            "a=control:track0\r\n" +
            "m=audio 0 RTP/AVP 8\r\n" +
            "a=rtpmap:8 PCMA/8000\r\n" +
            "a=control:track1\r\n";
    }

    /// <summary>
    /// 构建H264Sdp。
    /// </summary>
    /// <param name="videoConfig">视频编码参数集。</param>
    /// <returns>构建H264Sdp得到的(string RtpMap, string Fmtp)对象。</returns>
    private static (string RtpMap, string Fmtp) BuildH264Sdp(byte[]? videoConfig)
    {
        const string defaultFmtp = "a=fmtp:96 profile-level-id=420020;packetization-mode=1\r\n";
        if (videoConfig == null)
            return ("H264/90000", defaultFmtp);

        string? sps = null;
        string? pps = null;
        foreach (var nalu in SplitAnnexBNalus(videoConfig))
        {
            if (nalu.Length == 0)
                continue;

            var naluSpan = videoConfig.AsSpan(nalu.Start, nalu.Length);

            switch (naluSpan[0] & 0x1F)
            {
                case 7:
                    sps = Convert.ToBase64String(naluSpan);
                    break;
                case 8:
                    pps = Convert.ToBase64String(naluSpan);
                    break;
            }
        }

        if (string.IsNullOrEmpty(sps) || string.IsNullOrEmpty(pps))
            return ("H264/90000", defaultFmtp);

        return ("H264/90000", $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={sps},{pps}\r\n");
    }

    /// <summary>
    /// 构建H265Sdp。
    /// </summary>
    /// <param name="videoConfig">视频编码参数集。</param>
    /// <returns>构建H265Sdp得到的(string RtpMap, string Fmtp)对象。</returns>
    private static (string RtpMap, string Fmtp) BuildH265Sdp(byte[]? videoConfig)
    {
        const string defaultFmtp = "a=fmtp:96 profile-id=1\r\n";
        if (videoConfig == null)
            return ("H265/90000", defaultFmtp);

        string? vps = null;
        string? sps = null;
        string? pps = null;
        foreach (var nalu in SplitAnnexBNalus(videoConfig))
        {
            if (nalu.Length < 2)
                continue;

            var naluSpan = videoConfig.AsSpan(nalu.Start, nalu.Length);

            switch ((naluSpan[0] >> 1) & 0x3F)
            {
                case 32:
                    vps = Convert.ToBase64String(naluSpan);
                    break;
                case 33:
                    sps = Convert.ToBase64String(naluSpan);
                    break;
                case 34:
                    pps = Convert.ToBase64String(naluSpan);
                    break;
            }
        }

        if (string.IsNullOrEmpty(vps) || string.IsNullOrEmpty(sps) || string.IsNullOrEmpty(pps))
            return ("H265/90000", defaultFmtp);

        return ("H265/90000", $"a=fmtp:96 profile-id=1;sprop-vps={vps};sprop-sps={sps};sprop-pps={pps}\r\n");
    }

    /// <summary>
    /// 获取视频负载类型。
    /// </summary>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>获取视频负载类型计算得到的数值。</returns>
    private static byte GetVideoPayloadType(MediaCodec codec)
    {
        return codec switch
        {
            MediaCodec.H265 => 97,
            _ => 96,
        };
    }

    /// <summary>从视频帧中提取解码所需的参数集。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>从视频帧中提取解码所需的参数集得到的byte[]?对象。</returns>
    private static byte[]? ExtractVideoConfig(ReadOnlySpan<byte> data, MediaCodec codec)
    {
        if (codec is not (MediaCodec.H264 or MediaCodec.H265))
            return null;

        var nalus = SplitAnnexBNalus(data);
        if (nalus.Count == 0)
            return null;

        var selected = new List<(int Start, int Length)>(3);
        foreach (var nalu in nalus)
        {
            if (IsParameterSet(data.Slice(nalu.Start, nalu.Length), codec))
            {
                selected.Add(nalu);
            }
        }

        if (selected.Count == 0)
            return null;

        var totalLength = 0;
        foreach (var nalu in selected)
        {
            totalLength += 4 + nalu.Length;
        }

        var buffer = new byte[totalLength];
        var offset = 0;
        foreach (var nalu in selected)
        {
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 1;
            data.Slice(nalu.Start, nalu.Length).CopyTo(buffer.AsSpan(offset));
            offset += nalu.Length;
        }

        return buffer;
    }

    /// <summary>等待媒体流公布视频编码格式。</summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>异步操作的结果：等待媒体流公布视频编码格式是否成立。</returns>
    private async ValueTask<bool> WaitForVideoCodecAsync(string streamId, string channel, CancellationToken cancellationToken)
    {
        if (GetVideoInfo(streamId, channel).Codec != MediaCodec.Unknown)
            return true;

        for (var i = 0; i < 100; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetVideoInfo(streamId, channel).Codec != MediaCodec.Unknown)
                return true;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 获取视频Info。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <returns>获取视频Info得到的(MediaCodec Codec, byte[]? VideoConfig)对象。</returns>
    private (MediaCodec Codec, byte[]? VideoConfig) GetVideoInfo(string streamId, string channel)
    {
        if (InvokeClientCallback(() => ResolveVideoInfo?.Invoke(streamId, channel)) is { } resolved && resolved.Codec != MediaCodec.Unknown)
        {
            return resolved;
        }

        var key = $"{streamId}/{channel}";
        var codec = _streamVideoCodec.TryGetValue(key, out var localCodec) ? localCodec : MediaCodec.Unknown;
        _streamVideoConfig.TryGetValue(key, out var videoConfig);
        return (codec, videoConfig);
    }

    /// <summary>
    /// 判断是否参数Set。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否参数Set的判定结果。</returns>
    private static bool IsParameterSet(ReadOnlySpan<byte> nalu, MediaCodec codec)
    {
        if (codec == MediaCodec.H264)
            return nalu.Length > 0 && ((nalu[0] & 0x1F) is 7 or 8);

        return nalu.Length > 1 && (((nalu[0] >> 1) & 0x3F) is 32 or 33 or 34);
    }

    /// <summary>按起始码把 Annex-B 数据拆分为 NAL 单元。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>按起始码把 Annex-B 数据拆分为 NAL 单元得到的条目集合。</returns>
    private static List<(int Start, int Length)> SplitAnnexBNalus(ReadOnlySpan<byte> data)
    {
        var result = new List<(int Start, int Length)>();
        var i = 0;

        while (i < data.Length)
        {
            if (i + 3 <= data.Length && data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    i += 3;
                }
                else if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    i += 4;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }

            var naluStart = i;
            i = AnnexBStartCode.Find(data, i, out _);
            if (i < 0) i = data.Length;

            if (i > naluStart)
            {
                result.Add((naluStart, i - naluStart));
            }
        }

        return result;
    }

    /// <summary>
    /// 写入响应，并异步完成操作。
    /// </summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="buffer">数据缓冲区。</param>
    /// <param name="status">当前状态。</param>
    /// <param name="cSeq">SIP 或 RTSP 请求序号。</param>
    /// <param name="extraHeaders">附加的消息头。</param>
    /// <param name="body">消息正文。</param>
    /// <param name="cancellationToken">连接的取消令牌。</param>
    /// <returns>写入响应，并异步完成操作完成时结束的异步任务。</returns>
    private static async ValueTask WriteResponseAsync(NetworkStream stream, byte[] buffer,
        string status, int cSeq, string extraHeaders, CancellationToken cancellationToken, string? body = null)
    {
        var sb = new StringBuilder(256);
        sb.Append("RTSP/1.0 ").Append(status).Append("\r\n");
        sb.Append("CSeq: ").Append(cSeq).Append("\r\n");
        sb.Append(extraHeaders);
        sb.Append("\r\n");

        if (body != null)
            sb.Append(body);

        var response = sb.ToString();
        var bytes = Encoding.ASCII.GetBytes(response);
#if NET7_0_OR_GREATER
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
#else
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
#endif
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 移除客户端Sessions。
    /// </summary>
    /// <param name="streamKey">媒体流的组合标识。</param>
    /// <param name="sessionId">会话标识。</param>
    private void RemoveClientSessions(string streamKey, string? sessionId)
    {
        lock (_sessionsSync)
            RemoveClientSessionsCore(streamKey, sessionId);
    }

    /// <summary>
    /// 移除客户端SessionsCore。
    /// </summary>
    /// <param name="streamKey">媒体流的组合标识。</param>
    /// <param name="sessionId">会话标识。</param>
    private void RemoveClientSessionsCore(string streamKey, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            if (_sessions.TryRemove(streamKey, out var removedSessions))
            {
                foreach (var session in removedSessions.Values)
                    session.Dispose();
            }

            return;
        }

        if (!_sessions.TryGetValue(streamKey, out var sessions))
            return;

        foreach (var item in sessions)
        {
            if (item.Key.StartsWith(sessionId + ":", StringComparison.Ordinal))
            {
                if (sessions.TryRemove(item.Key, out var session))
                    session.Dispose();
            }
        }

        if (sessions.IsEmpty)
        {
            _sessions.TryRemove(streamKey, out _);
            _streamVideoCodec.TryRemove(streamKey, out _);
            _streamVideoConfig.TryRemove(streamKey, out _);
        }
    }

    /// <inheritdoc />
    /// <summary>停止RtspServer并等待所管理的操作结束。</summary>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>停止RtspServer并等待所管理的操作结束完成时结束的异步任务。</returns>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clientScope.Value is { Active: true } scope)
        {
            // 当前任务属于 ExecuteAsync 的子任务。请求取消后让回调自行退出，避免等待自身。
            scope.Lifetime.Cancel();
            return Task.CompletedTask;
        }
        return base.StopAsync(cancellationToken);
    }

    /// <summary>在有效的客户端回调作用域内调用订阅者。</summary>
    /// <param name="callback">需要调用的回调。</param>
    /// <typeparam name="T">读取或解析得到的值类型。</typeparam>
    /// <returns>在有效的客户端回调作用域内调用订阅者得到的T对象。</returns>
    private T InvokeClientCallback<T>(Func<T> callback)
    {
        var owner = _clientScope.Value;
        if (owner == null) return callback();
        var scope = new ServerClientScope(owner.Lifetime);
        _clientScope.Value = scope;
        try { return callback(); }
        finally { scope.Active = false; _clientScope.Value = owner; }
    }

    /// <summary>通知媒体流请求事件并等待订阅者处理完成。</summary>
    /// <param name="callback">需要调用的回调。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>通知媒体流请求事件并等待订阅者处理完成完成时结束的异步任务。</returns>
    private async Task InvokeStreamRequestedAsync(Func<string, string, CancellationToken, Task> callback,
        string streamId, string channel, CancellationToken cancellationToken)
    {
        var owner = _clientScope.Value!;
        var scope = new ServerClientScope(owner.Lifetime);
        _clientScope.Value = scope;
        try { await callback(streamId, channel, cancellationToken).ConfigureAwait(false); }
        finally { scope.Active = false; _clientScope.Value = owner; }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var sessionGroup in _sessions.Values)
        {
            foreach (var session in sessionGroup.Values)
                session.Dispose();
        }
        _sessions.Clear();
    }
}

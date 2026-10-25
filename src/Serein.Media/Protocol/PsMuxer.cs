using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Serein.Media.Protocol;

/// <summary>
/// GB/T 28181 PS over RTP 发送器。
/// 支持 UDP、TCP-ACTIVE、TCP-PASSIVE 三种媒体发送模式。
/// PS 封装遵循 ISO 13818-1 (MPEG-2 Systems) 和 GB/T 28181 标准。
/// 自动缓存 H.264 SPS/PPS 或 H.265 VPS/SPS/PPS，与 IDR 帧合并封装。
/// </summary>
public sealed class PsMuxer : IAsyncDisposable
{
    /// <summary>构造网络媒体包使用的默认最大长度。</summary>
    private const int DefaultMtu = 1400;
    /// <summary>媒体传输模式。</summary>
    private readonly string _transportMode;
    /// <summary>远端网络端点。</summary>
    private readonly IPEndPoint? _remoteEndPoint;
    /// <summary>本地绑定端点。</summary>
    private readonly IPEndPoint _bindEndPoint;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>RTP 同步源标识。</summary>
    private readonly uint _ssrc;
    /// <summary>可复用的 PS 封装缓冲区。</summary>
    private byte[] _psBuffer;
    /// <summary>串行保护媒体封装状态的互斥锁。</summary>
    private readonly object _muxLock = new();
    /// <summary>串行保护网络发送的异步锁。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    /// <summary>当前 UDP 媒体套接字。</summary>
    private Socket? _udpSocket;
    /// <summary>接受媒体连接的 TCP 监听器。</summary>
    private Socket? _tcpListener;
    /// <summary>当前 TCP 媒体连接。</summary>
    private Socket? _tcpClient;
    /// <summary>接受 TCP 连接的后台任务。</summary>
    private Task? _acceptTask;
    /// <summary>下一次发送或期望接收的 RTP 包序列号。</summary>
    private ushort _sequenceNumber;
    /// <summary>首个媒体帧的毫秒时间戳。</summary>
    private long _firstTimestampMs = -1;
    /// <summary>归一化媒体时间戳使用的基准值。</summary>
    private uint _baseTimestamp;
    /// <summary>
    /// 实例是否已释放的状态标记。
    /// </summary>
    private bool _disposed;

    /// <summary>PES 包头固定部分的字节数。</summary>
    private const int PesHeaderLength = 14;

    // 缓存参数帧（H.264 SPS/PPS 或 H.265 VPS/SPS/PPS），等 IDR 到达后合并发送
    /// <summary>拼接后缓存的视频参数集。</summary>
    private byte[]? _cachedParams;
    /// <summary>缓存参数集的有效字节数。</summary>
    private int _cachedParamsLength;

    /// <summary>
    /// 初始化 PsMuxer 的新实例。
    /// </summary>
    /// <param name="transportMode">媒体传输模式。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="remoteEndPoint">远端网络端点。</param>
    /// <param name="bindEndPoint">本地绑定端点。</param>
    /// <param name="payloadType">RTP 负载类型。</param>
    /// <param name="clockRate">RTP 媒体时钟频率，单位为赫兹。</param>
    /// <param name="logger">日志记录器。</param>
    public PsMuxer(
        string transportMode,
        uint ssrc,
        IPEndPoint? remoteEndPoint = null,
        IPEndPoint? bindEndPoint = null,
        byte payloadType = 96,
        int clockRate = 90000,
        ILogger? logger = null)
    {
        _transportMode = string.IsNullOrWhiteSpace(transportMode) ? "UDP" : transportMode.ToUpperInvariant();
        _remoteEndPoint = remoteEndPoint;
        _bindEndPoint = bindEndPoint ?? new IPEndPoint(IPAddress.Any, 0);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PsMuxer>.Instance;
        _ssrc = ssrc;
        PayloadType = payloadType;
        ClockRate = clockRate;
        _psBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        _cachedParams = ArrayPool<byte>.Shared.Rent(1024);
    }

    /// <summary>
    /// 获取 负载类型。
    /// </summary>
    public byte PayloadType { get; }

    /// <summary>
    /// 获取 ClockRate。
    /// </summary>
    public int ClockRate { get; }

    /// <summary>单个 PS 封装帧允许的最大字节数。</summary>
    public int MaxPsFrameSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// 获取或设置 本地结束Point。
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>启动当前接收或处理流程。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动当前接收或处理流程完成时结束的异步任务。</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        switch (_transportMode)
        {
            case "UDP":
                await StartUdpAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "TCP-ACTIVE":
                await StartTcpActiveAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "TCP-PASSIVE":
                StartTcpPassive(cancellationToken);
                break;
            default:
                throw new NotSupportedException($"不支持的媒体传输模式: {_transportMode}");
        }
    }

    /// <summary>
    /// 写入视频帧。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="timestampMs">时间戳，单位为毫秒。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="codec">媒体编码格式。</param>
    public void WriteVideoFrame(ReadOnlySpan<byte> frame, long timestampMs, bool isKeyFrame, MediaCodec codec)
    {
        lock (_muxLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PsMuxer));
            WriteVideoFrameCore(frame, timestampMs, isKeyFrame, codec);
        }
    }

    /// <summary>
    /// 写入视频帧Core。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="timestampMs">时间戳，单位为毫秒。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="codec">媒体编码格式。</param>
    private void WriteVideoFrameCore(ReadOnlySpan<byte> frame, long timestampMs, bool isKeyFrame, MediaCodec codec)
    {
        if (frame.IsEmpty)
            return;

        if (_firstTimestampMs < 0)
        {
            _firstTimestampMs = timestampMs > 0 ? timestampMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _baseTimestamp = (uint)Random.Shared.Next();
        }

        var normalizedTimestamp = timestampMs > 0 ? timestampMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rtpTimestamp = _baseTimestamp + (uint)Math.Max(0, (normalizedTimestamp - _firstTimestampMs) * ClockRate / 1000);
        var pts90kHz = (ulong)Math.Max(0, normalizedTimestamp) * 90UL;

        var accessUnit = VideoNalUnitHelper.Analyze(frame, codec);
        if (accessUnit.HasParameterSet && !accessUnit.HasNonParameterSet)
        {
            // 缓存参数集 NAL（VPS/SPS/PPS 或 SPS/PPS），等待 IDR 合并发送
            int annexBLength = checked(frame.Length + (StartsWithStartCode(frame) ? 0 : 4));
            if ((long)_cachedParamsLength + annexBLength > Math.Max(64 * 1024, MaxPsFrameSize))
            {
                _logger.LogWarning("PS muxer dropped oversized video parameter sets ({Length} bytes)",
                    (long)_cachedParamsLength + annexBLength);
                _cachedParamsLength = 0;
                return;
            }

            EnsureCachedParamsCapacity(annexBLength);
            _cachedParamsLength += CopyAnnexB(frame, _cachedParams.AsSpan(_cachedParamsLength));
            return;
        }

        // 仅在访问单元确实包含视频切片时信任调用方提供的 I 帧提示。Annex-B 检查覆盖 HEVC BLA、IDR、CRA 类型 16～21，而非仅两个 IDR 类型。
        bool isIdr = accessUnit.IsRandomAccess || (isKeyFrame && accessUnit.HasVcl);

        long required = 14L + PesHeaderLength + frame.Length + 4 + _cachedParamsLength;
        if (isIdr)
            required += 18 + 20;
        int maximum = Math.Max(64 * 1024, MaxPsFrameSize);
        if (required > maximum || required > int.MaxValue)
        {
            _logger.LogWarning("PS muxer dropped oversized video access unit ({Length} bytes, max {Max})",
                required, maximum);
            return;
        }

        EnsurePsBufferCapacity((int)required);

        // 构建 PS 包
        var dst = _psBuffer.AsSpan();
        var offset = 0;

        // 第一部分为 14 字节 PS 包头。
        offset += WritePackHeader(dst[offset..], pts90kHz);

        // 2. System Header — 仅 IDR 帧时包含
        if (isIdr)
        {
            offset += WriteSystemHeader(dst[offset..]);
            offset += WriteProgramStreamMap(dst[offset..], codec);
        }

        // 3. 如果有缓存的参数集，合并写入一个 PES（参数集 + IDR）
        if (isIdr && _cachedParamsLength > 0)
        {
            // 合并: 缓存的 VPS+SPS+PPS + 当前 IDR 帧
            var esSpan = dst.Slice(offset + PesHeaderLength);
            _cachedParams.AsSpan(0, _cachedParamsLength).CopyTo(esSpan);
            var totalEsLength = _cachedParamsLength + CopyAnnexB(frame, esSpan[_cachedParamsLength..]);
            offset += WriteVideoPes(dst[offset..], esSpan.Slice(0, totalEsLength), pts90kHz, true);
            _cachedParamsLength = 0;
        }
        else
        {
            // 普通 P 或非 IDR I 帧
            var esSpan = dst.Slice(offset + PesHeaderLength);
            var esLength = CopyAnnexB(frame, esSpan);
            offset += WriteVideoPes(dst[offset..], esSpan.Slice(0, esLength), pts90kHz, isKeyFrame);
        }

        if (offset <= 0)
            return;

        SendPsAsRtp(_psBuffer.AsSpan(0, offset), rtpTimestamp);
    }

    /// <summary>
    /// 检测 NAL 类型（H.264 和 H.265）
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>检测 NAL 类型（H.264 和 H.265）计算得到的数值。</returns>
    private static int DetectNalType(ReadOnlySpan<byte> frame, MediaCodec codec)
    {
        // 查找起始码
        for (int i = 0; i < Math.Min(frame.Length - 4, 64); i++)
        {
            if (frame[i] == 0 && frame[i + 1] == 0 && (frame[i + 2] == 1 || (frame[i + 2] == 0 && i + 3 < frame.Length && frame[i + 3] == 1)))
            {
                int offset = frame[i + 2] == 1 ? i + 3 : i + 4;
                if (offset >= frame.Length) return -1;
                byte nalByte = frame[offset];

                if (codec == MediaCodec.H265)
                    return (nalByte >> 1) & 0x3F;
                else
                    return nalByte & 0x1F;
            }
        }
        // 没有起始码，直接检查第一个字节
        if (frame.Length > 0)
        {
            if (codec == MediaCodec.H265)
                return (frame[0] >> 1) & 0x3F;
            else
                return frame[0] & 0x1F;
        }
        return -1;
    }

    /// <summary>是否是参数集 NAL（SPS/PPS 或 VPS/SPS/PPS）</summary>
    /// <param name="nalType">NAL 单元类型编号。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否是参数集 NAL（SPS/PPS 或 VPS/SPS/PPS）的判定结果。</returns>
    private static bool IsParameterSetNal(int nalType, MediaCodec codec)
    {
        if (nalType < 0) return false;
        if (codec == MediaCodec.H265)
            return nalType is 32 or 33 or 34; // 视频、序列和图像参数集：VPS、SPS、PPS。
        else
            return nalType is 7 or 8; // 序列和图像参数集：SPS、PPS。
    }

    /// <summary>是否是 IDR NAL</summary>
    /// <param name="nalType">NAL 单元类型编号。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否是 IDR NAL的判定结果。</returns>
    private static bool IsIdrNal(int nalType, MediaCodec codec)
    {
        if (nalType < 0) return false;
        if (codec == MediaCodec.H265)
            return nalType is >= 16 and <= 21; // BLA、IDR 或 CRA 随机接入帧。
        else
            return nalType == 5; // 即时解码刷新帧 IDR。
    }

    /// <summary>把视频 NAL 数据复制为 Annex-B 格式。</summary>
    /// <param name="source">源数据。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>把视频 NAL 数据复制为 Annex-B 格式计算得到的数值。</returns>
    private static int CopyAnnexB(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (StartsWithStartCode(source))
        {
            source.CopyTo(destination);
            return source.Length;
        }

        destination[0] = 0x00;
        destination[1] = 0x00;
        destination[2] = 0x00;
        destination[3] = 0x01;
        source.CopyTo(destination[4..]);
        return source.Length + 4;
    }

    /// <summary>
    /// 启动s使用起始Code。
    /// </summary>
    /// <param name="source">源数据。</param>
    /// <returns>启动s使用起始Code是否成立。</returns>
    private static bool StartsWithStartCode(ReadOnlySpan<byte> source)
    {
        return source.Length >= 3 && source[0] == 0x00 && source[1] == 0x00 && source[2] == 0x01 ||
            source.Length >= 4 && source[0] == 0x00 && source[1] == 0x00 && source[2] == 0x00 && source[3] == 0x01;
    }

    /// <summary>确保参数集缓存具有本次写入需要的容量。</summary>
    /// <param name="additional">本次需要追加的容量。</param>
    private void EnsureCachedParamsCapacity(int additional)
    {
        int required = checked(_cachedParamsLength + additional);
        if (_cachedParams != null && _cachedParams.Length >= required)
            return;

        int currentLength = _cachedParams?.Length ?? 0;
        int newSize = Math.Max(required, Math.Max(1024, currentLength * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(newSize);
        if (_cachedParams != null)
        {
            _cachedParams.AsSpan(0, _cachedParamsLength).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_cachedParams);
        }
        _cachedParams = replacement;
    }

    /// <summary>确保 PS 封装缓冲区具有本次写入需要的容量。</summary>
    /// <param name="required">本次操作所需的最小容量。</param>
    private void EnsurePsBufferCapacity(int required)
    {
        if (_psBuffer.Length >= required)
            return;

        int newSize = Math.Max(required, Math.Min(Math.Max(64 * 1024, MaxPsFrameSize), _psBuffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(newSize);
        ArrayPool<byte>.Shared.Return(_psBuffer);
        _psBuffer = replacement;
    }

    /// <summary>
    /// 启动UDP，并异步完成操作。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动UDP，并异步完成操作完成时结束的异步任务。</returns>
    private async Task StartUdpAsync(CancellationToken cancellationToken)
    {
        if (_remoteEndPoint == null)
            throw new InvalidOperationException("UDP 模式需要远端媒体地址。");

        _udpSocket = new Socket(_remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpSocket.Bind(_bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, _bindEndPoint.Port)
            : new IPEndPoint(IPAddress.Any, _bindEndPoint.Port));
        await _udpSocket.ConnectAsync(_remoteEndPoint, cancellationToken).ConfigureAwait(false);
        LocalEndPoint = (IPEndPoint?)_udpSocket.LocalEndPoint;
    }

    /// <summary>
    /// 启动TCP活动，并异步完成操作。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>启动TCP活动，并异步完成操作完成时结束的异步任务。</returns>
    private async Task StartTcpActiveAsync(CancellationToken cancellationToken)
    {
        if (_remoteEndPoint == null)
            throw new InvalidOperationException("TCP-ACTIVE 模式需要远端媒体地址。");

        _tcpClient = new Socket(_remoteEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _tcpClient.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcpClient.Bind(_bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, _bindEndPoint.Port)
            : new IPEndPoint(IPAddress.Any, _bindEndPoint.Port));
        await _tcpClient.ConnectAsync(_remoteEndPoint, cancellationToken).ConfigureAwait(false);
        LocalEndPoint = (IPEndPoint?)_tcpClient.LocalEndPoint;
    }

    /// <summary>
    /// 启动TCPPassive。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    private void StartTcpPassive(CancellationToken cancellationToken)
    {
        var listenEndPoint = _bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, _bindEndPoint.Port)
            : new IPEndPoint(IPAddress.Any, _bindEndPoint.Port);

        _tcpListener = new Socket(listenEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _tcpListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcpListener.Bind(listenEndPoint);
        _tcpListener.Listen(16);
        LocalEndPoint = (IPEndPoint?)_tcpListener.LocalEndPoint;

        _acceptTask = Task.Run(async () =>
        {
            try
            {
                _tcpClient = await _tcpListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("PS/RTP TCP-PASSIVE 已接入: {RemoteEndPoint}", _tcpClient.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PS/RTP TCP-PASSIVE 等待连接失败");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 发送PsAsRTP。
    /// </summary>
    /// <param name="psData">MPEG-PS 封装数据。</param>
    /// <param name="rtpTimestamp">按当前媒体时钟频率计数的 RTP 时间戳。</param>
    private void SendPsAsRtp(ReadOnlySpan<byte> psData, uint rtpTimestamp)
    {
        var offset = 0;
        while (offset < psData.Length)
        {
            var payloadLength = Math.Min(DefaultMtu, psData.Length - offset);
            var isLastPacket = offset + payloadLength >= psData.Length;
            var header = RtpPacketBuilder.CreateHeader(PayloadType, _sequenceNumber++, rtpTimestamp, _ssrc, isLastPacket);
            var rtpPacketLength = StandardRtpHeader.HeaderLength + payloadLength;

            if (_transportMode == "UDP")
            {
                var buffer = ArrayPool<byte>.Shared.Rent(rtpPacketLength);
                try
                {
                    RtpPacketBuilder.WritePacket(buffer.AsSpan(0, rtpPacketLength), header, psData.Slice(offset, payloadLength));
                    SendUdp(buffer, rtpPacketLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                // TCP: 2字节大端长度前缀 + RTP包（GB/T 28181 附录K）
                var totalLength = 2 + rtpPacketLength;
                var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
                try
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)rtpPacketLength);
                    RtpPacketBuilder.WritePacket(buffer.AsSpan(2, rtpPacketLength), header, psData.Slice(offset, payloadLength));
                    SendTcp(buffer, totalLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            offset += payloadLength;
        }
    }

    /// <summary>
    /// 发送UDP。
    /// </summary>
    /// <param name="buffer">数据缓冲区。</param>
    /// <param name="packetLength">当前包的字节数。</param>
    private void SendUdp(byte[] buffer, int packetLength)
    {
        _sendLock.Wait();
        try
        {
            _udpSocket?.Send(buffer, 0, packetLength, SocketFlags.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 发送TCP。
    /// </summary>
    /// <param name="buffer">数据缓冲区。</param>
    /// <param name="totalLength">完整数据的总字节数。</param>
    private void SendTcp(byte[] buffer, int totalLength)
    {
        _sendLock.Wait();
        try
        {
            if (_tcpClient == null || !_tcpClient.Connected)
                return;

            int sent = 0;
            while (sent < totalLength)
            {
                int count = _tcpClient.Send(buffer, sent, totalLength - sent, SocketFlags.None);
                if (count <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                sent += count;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    #region PS Pack Header (ISO 13818-1 Table 2-2, 14 bytes)

    /// <summary>
    /// 写入Pack头部。
    /// </summary>
    /// <param name="dst">写入结果的目标缓冲区。</param>
    /// <param name="scr90kHz">按 90 kHz 计数的系统时钟参考值。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int WritePackHeader(Span<byte> dst, ulong scr90kHz)
    {
        // PS 包起始码为 0x000001BA。
        dst[0] = 0x00;
        dst[1] = 0x00;
        dst[2] = 0x01;
        dst[3] = 0xBA;

        ulong scr = scr90kHz & 0x1FFFFFFFFUL; // 33 位系统时钟参考基值。
        const uint scrExt = 0;

        dst[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        dst[5] = (byte)(scr >> 20);
        dst[6] = (byte)((((scr >> 15) & 0x1F) << 3) | 0x04 | ((scr >> 13) & 0x03));
        dst[7] = (byte)(scr >> 5);
        dst[8] = (byte)(((scr & 0x1F) << 3) | 0x04 | ((scrExt >> 7) & 0x03));
        dst[9] = (byte)(((scrExt & 0x7F) << 1) | 0x01);

        const uint muxRate = 10080;
        dst[10] = (byte)((muxRate >> 14) & 0xFF);
        dst[11] = (byte)((muxRate >> 6) & 0xFF);
        dst[12] = (byte)(((muxRate & 0x3F) << 2) | 0x03);
        dst[13] = 0xF8; // 保留位，以及值为零的填充长度。

        return 14;
    }

    #endregion

    #region System Header (ISO 13818-1 Table 2-3)

    /// <summary>
    /// 写入System头部。
    /// </summary>
    /// <param name="dst">写入结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int WriteSystemHeader(Span<byte> dst)
    {
        dst[0] = 0x00;
        dst[1] = 0x00;
        dst[2] = 0x01;
        dst[3] = 0xBB;

        // 系统头长度为 12 字节。
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], 12);

        // 22 位码率上限和标记位。
        const uint rateBound = 10080;
        dst[6] = (byte)(0x80 | ((rateBound >> 15) & 0x7F));
        dst[7] = (byte)((rateBound >> 7) & 0xFF);
        dst[8] = (byte)(((rateBound & 0x7F) << 1) | 0x01);

        // 音频流数量上限、固定码率标志和 CSPS 标志均为零。
        dst[9] = 0x00;

        // 音视频时钟锁定与标记位均为 1，视频流数量上限为 1。
        dst[10] = 0xC1;

        // 包速率限制为零，保留位为 0x7F。
        dst[11] = 0x7F;

        // 视频流条目：stream_id=0xE0，前缀为 11，尺度为 1，缓冲区大小为 0x23E0。
        dst[12] = 0xE0;
        dst[13] = (byte)(0xC0 | 0x23);
        dst[14] = 0xE0;

        // 填充
        dst[15] = 0xFF;
        dst[16] = 0xFF;
        dst[17] = 0xFF;

        return 18;
    }

    #endregion

    #region Program Stream Map (ISO 13818-1 Table 2-33)

    /// <summary>
    /// 写入Program流映射。
    /// </summary>
    /// <param name="dst">写入结果的目标缓冲区。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int WriteProgramStreamMap(Span<byte> dst, MediaCodec codec)
    {
        dst[0] = 0x00;
        dst[1] = 0x00;
        dst[2] = 0x01;
        dst[3] = 0xBC;

        // 一个基本流条目：10 字节映射数据与 4 字节 CRC。
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], 14);
        dst[6] = 0xE0; // 当前映射有效，保留位为 3，版本为零。
        dst[7] = 0xFF; // 保留位为 0x7F，标记位为 1。
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..], 0); // 节目流描述信息长度。
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], 4); // 基本流映射长度。
        dst[12] = codec == MediaCodec.H265 ? (byte)0x24 : (byte)0x1B;
        dst[13] = 0xE0; // 基本流标识，选用第零路视频流。
        BinaryPrimitives.WriteUInt16BigEndian(dst[14..], 0); // 基本流描述信息长度。

        BinaryPrimitives.WriteUInt32BigEndian(dst[16..], CalculateCrc32(dst.Slice(6, 10)));
        return 20;
    }

    /// <summary>按 MPEG-PS 流映射表规则计算 CRC32。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>按 MPEG-PS 流映射表规则计算 CRC32计算得到的数值。</returns>
    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0x04C11DB7;
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ polynomial : crc << 1;
            }
        }
        return crc;
    }

    #endregion

    #region PES Packet (ISO 13818-1 Table 2-17)

    /// <summary>
    /// 写入视频Pes。
    /// </summary>
    /// <param name="dst">写入结果的目标缓冲区。</param>
    /// <param name="esData">基本媒体流数据。</param>
    /// <param name="pts90kHz">按 90 kHz 计数的显示时间戳。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int WriteVideoPes(Span<byte> dst, ReadOnlySpan<byte> esData, ulong pts90kHz, bool isKeyFrame)
    {
        dst[0] = 0x00;
        dst[1] = 0x00;
        dst[2] = 0x01;
        dst[3] = 0xE0; // 视频流标识。

        const int pesHeaderDataLength = 5;
        var pesPacketLength = 3 + pesHeaderDataLength + esData.Length;
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..],
            pesPacketLength > 65535 ? (ushort)0 : (ushort)pesPacketLength);

        // 第一个 PES 标志字节。
        dst[6] = (byte)(0x80 | (isKeyFrame ? 0x08 : 0x00));

        // 第二个 PES 标志字节，仅包含 PTS。
        dst[7] = 0x80;

        // PES 可选头数据长度。
        dst[8] = pesHeaderDataLength;

        // 5 字节显示时间戳 PTS。
        WritePts(dst[9..], pts90kHz);

        // 基本媒体流数据。
        esData.CopyTo(dst[14..]);

        return 14 + esData.Length;
    }

    /// <summary>
    /// 写入Pts。
    /// </summary>
    /// <param name="dst">写入结果的目标缓冲区。</param>
    /// <param name="pts">显示时间戳。</param>
    private static void WritePts(Span<byte> dst, ulong pts)
    {
        dst[0] = (byte)(0x21 | ((pts >> 29) & 0x0E) | 0x01);
        dst[1] = (byte)((pts >> 22) & 0xFF);
        dst[2] = (byte)(((pts >> 14) & 0xFE) | 0x01);
        dst[3] = (byte)((pts >> 7) & 0xFF);
        dst[4] = (byte)(((pts << 1) & 0xFE) | 0x01);
    }

    #endregion

    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        lock (_muxLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _udpSocket?.Dispose(); } catch { }
            try { _tcpClient?.Dispose(); } catch { }
            try { _tcpListener?.Dispose(); } catch { }
        }

        try
        {
            if (_acceptTask != null)
                await _acceptTask.ConfigureAwait(false);
        }
        catch { }

        lock (_muxLock)
        {
            // 第一次关闭期间，Accept 仍可能并发完成。
            try { _tcpClient?.Dispose(); } catch { }
            _sendLock.Dispose();

            if (_psBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_psBuffer);
                _psBuffer = Array.Empty<byte>();
            }
            if (_cachedParams != null)
            {
                ArrayPool<byte>.Shared.Return(_cachedParams);
                _cachedParams = null;
                _cachedParamsLength = 0;
            }
        }
    }
}

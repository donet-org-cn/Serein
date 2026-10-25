using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Serein.Media;
using Serein.Media.Relay;

namespace Serein.Rtsp.Server;

/// <summary>
/// RTSP 会话。管理 RTP 包的 UDP 发送。
/// </summary>
internal sealed class RtspSession : IDisposable
{
    /// <summary>RTP 数据套接字。</summary>
    private readonly Socket _rtpSocket;
    /// <summary>客户端网络端点。</summary>
    private readonly IPEndPoint _clientEndpoint;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>订阅建立时已缓存的视频参数集。</summary>
    private readonly byte[]? _initialVideoConfig;
    /// <summary>媒体类别。</summary>
    private readonly MediaFrameKind _mediaKind;
    /// <summary>是否将 RTP 套接字归还端口池。</summary>
    private readonly Action<Socket, int>? _returnRtpSocket;
    /// <summary>服务端 RTP 端口。</summary>
    private readonly int _serverRtpPort;
    /// <summary>下一次发送或期望接收的 RTP 包序列号。</summary>
    private ushort _sequenceNumber;
    /// <summary>按当前媒体时钟频率计数的 RTP 时间戳。</summary>
    private uint _rtpTimestamp;
    /// <summary>RTP 同步源标识。</summary>
    private readonly uint _ssrc;
    /// <summary>
    /// 实例是否已释放的状态标记。
    /// </summary>
    private bool _disposed;
    /// <summary>上一媒体帧的毫秒时间戳。</summary>
    private long _lastTimestampMs = -1;
    /// <summary>是否已经发送建立视频解码所需的同步数据。</summary>
    private bool _videoSyncSent;
    /// <summary>保护发送队列与套接字状态的互斥锁。</summary>
    private readonly object _sendSync = new();
    /// <summary>等待分发的有界媒体帧队列。</summary>
    private readonly MediaStreamSubscription _pendingFrames = new(_ => { }, 16, dropStaleFrames: true);

    /// <summary>网络媒体发送时复用的缓冲区。</summary>
    private byte[]? _sendBuffer;
    /// <summary>发送缓冲区的字节容量。</summary>
    private const int SendBufferSize = 1500;

    /// <summary>
    /// 获取或设置 是否Playing。
    /// </summary>
    public bool IsPlaying { get; private set; } = true;

    /// <summary>
    /// 初始化 RtspSession 的新实例。
    /// </summary>
    /// <param name="rtpSocket">RTP 数据套接字。</param>
    /// <param name="clientEndpoint">客户端网络端点。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="initialVideoConfig">订阅建立时已缓存的视频参数集。</param>
    /// <param name="mediaKind">媒体类别。</param>
    /// <param name="returnRtpSocket">是否将 RTP 套接字归还端口池。</param>
    /// <param name="serverRtpPort">服务端 RTP 端口。</param>
    public RtspSession(Socket rtpSocket, IPEndPoint clientEndpoint, ILogger logger, byte[]? initialVideoConfig,
        MediaFrameKind mediaKind, Action<Socket, int>? returnRtpSocket = null, int serverRtpPort = 0)
    {
        _rtpSocket = rtpSocket;
        _clientEndpoint = clientEndpoint;
        _logger = logger;
        _initialVideoConfig = initialVideoConfig;
        _mediaKind = mediaKind;
        _returnRtpSocket = returnRtpSocket;
        _serverRtpPort = serverRtpPort;
        _ssrc = (uint)Random.Shared.Next();
        // 拥塞的 UDP 套接字不得阻塞媒体发布者或会话拆除。
        _rtpSocket.Blocking = false;
        _ = Task.Run(SendQueuedFramesAsync);
    }

    /// <summary>把媒体帧排入会话的有界发送队列。</summary>
    /// <param name="frame">媒体帧。</param>
    public void QueueFrame(PooledMediaFrame frame)
    {
        if (frame.TryRetain() && !_pendingFrames.TryWrite(frame))
            frame.Release();
    }

    /// <summary>
    /// 发送QueuedFrames，并异步完成操作。
    /// </summary>
    /// <returns>发送QueuedFrames，并异步完成操作完成时结束的异步任务。</returns>
    private async Task SendQueuedFramesAsync()
    {
        await foreach (var frame in _pendingFrames.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { SendFrame(frame); }
            finally { frame.Release(); }
        }
    }

    /// <summary>判断当前会话是否接收指定类别的媒体帧。</summary>
    /// <param name="kind">媒体或消息类别。</param>
    /// <returns>当前会话是否接收指定类别的媒体帧的判定结果。</returns>
    public bool Accepts(MediaFrameKind kind) => kind == _mediaKind;

    /// <summary>
    /// 发送帧。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    public void SendFrame(PooledMediaFrame frame)
    {
        // 媒体帧回调与 RTSP 连接清理运行在不同线程上。
        lock (_sendSync)
            SendFrameCore(frame);
    }

    /// <summary>
    /// 发送帧Core。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    private void SendFrameCore(PooledMediaFrame frame)
    {
        if (_disposed || !IsPlaying) return;

        try
        {
            var data = frame.GetSpan();
            if (data.Length == 0) return;

            _sendBuffer ??= ArrayPool<byte>.Shared.Rent(SendBufferSize);

            uint clockRate = frame.Kind == MediaFrameKind.Video ? 90000u : 8000u;
            _rtpTimestamp = (uint)(frame.TimestampMs * clockRate / 1000);
            _lastTimestampMs = frame.TimestampMs;

            var pt = GetPayloadType(frame);

            if (frame.Kind == MediaFrameKind.Video && (frame.Codec == MediaCodec.H264 || frame.Codec == MediaCodec.H265))
            {
                if (!_videoSyncSent)
                {
                    if (!frame.IsKeyFrame)
                        return;

                    if (_initialVideoConfig != null)
                    {
                        SendVideoNalus(_initialVideoConfig, pt, frame.Codec);
                    }

                    _videoSyncSent = true;
                }

                SendVideoNalus(data, pt, frame.Codec);
            }
            else if (frame.Kind == MediaFrameKind.Audio && frame.Codec == MediaCodec.G711A)
            {
                SendSinglePacket(data, pt, marker: true);
            }
            else
            {
                SendSinglePacket(data, pt, marker: true);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTP 发送异常，停止会话");
            IsPlaying = false;
        }
    }

    /// <summary>
    /// 发送视频Nalus。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="codec">媒体编码格式。</param>
    private void SendVideoNalus(ReadOnlySpan<byte> data, byte pt, MediaCodec codec)
    {
        if (!TryReadNextNalu(data, 0, out var nalu, out var nextOffset))
        {
            if (data.Length > 0)
                SendNalu(data, pt, codec, isLast: true);
            return;
        }

        while (true)
        {
            var hasNext = TryReadNextNalu(data, nextOffset, out var nextNalu, out var nextNextOffset);
            SendNalu(nalu, pt, codec, isLast: !hasNext);

            if (!hasNext)
                break;

            nalu = nextNalu;
            nextOffset = nextNextOffset;
        }
    }

    /// <summary>
    /// 发送Nalu。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="isLast">是否为本批数据中的最后一个包。</param>
    private void SendNalu(ReadOnlySpan<byte> nalu, byte pt, MediaCodec codec, bool isLast)
    {
        const int maxPacketSize = 1400;

        if (nalu.Length <= maxPacketSize)
        {
            SendSinglePacket(nalu, pt, marker: isLast);
        }
        else
        {
            if (codec == MediaCodec.H264)
                SendH264FuA(nalu, pt, isLast);
            else if (codec == MediaCodec.H265)
                SendH265Fu(nalu, pt, isLast);
            else
                SendH264FuA(nalu, pt, isLast);
        }
    }

    /// <summary>
    /// 发送Single数据包。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendSinglePacket(ReadOnlySpan<byte> payload, byte pt, bool marker)
    {
        int packetLen = 12 + payload.Length;
        byte[] packet = packetLen <= _sendBuffer!.Length
            ? _sendBuffer
            : ArrayPool<byte>.Shared.Rent(packetLen);

        try
        {
            WriteRtpHeader(packet, pt, marker);
            payload.CopyTo(packet.AsSpan(12));
            _rtpSocket.SendTo(packet.AsSpan(0, packetLen), SocketFlags.None, _clientEndpoint);
        }
        finally
        {
            if (packet != _sendBuffer)
                ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <summary>
    /// 发送H264FuA。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="isLast">是否为本批数据中的最后一个包。</param>
    private void SendH264FuA(ReadOnlySpan<byte> nalu, byte pt, bool isLast)
    {
        const int maxPayload = 1400;
        byte naluType = (byte)(nalu[0] & 0x1F);
        byte nri = (byte)(nalu[0] & 0x60);

        int offset = 1;
        bool isFirst = true;

        while (offset < nalu.Length)
        {
            int chunkLen = Math.Min(maxPayload - 2, nalu.Length - offset);
            bool isEnd = (offset + chunkLen) >= nalu.Length;
            int packetLen = 12 + 2 + chunkLen;

            byte[] packet = packetLen <= _sendBuffer!.Length
                ? _sendBuffer
                : ArrayPool<byte>.Shared.Rent(packetLen);

            try
            {
                WriteRtpHeader(packet, pt, marker: isLast && isEnd);

                packet[12] = (byte)(nri | 28);
                byte fuHeader = (byte)naluType;
                if (isFirst) fuHeader |= 0x80;
                if (isEnd) fuHeader |= 0x40;
                packet[13] = fuHeader;

                nalu.Slice(offset, chunkLen).CopyTo(packet.AsSpan(14));
                _rtpSocket.SendTo(packet.AsSpan(0, packetLen), SocketFlags.None, _clientEndpoint);
            }
            finally
            {
                if (packet != _sendBuffer)
                    ArrayPool<byte>.Shared.Return(packet);
            }

            offset += chunkLen;
            isFirst = false;
        }
    }

    /// <summary>
    /// 发送H265Fu。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="isLast">是否为本批数据中的最后一个包。</param>
    private void SendH265Fu(ReadOnlySpan<byte> nalu, byte pt, bool isLast)
    {
        const int maxPayload = 1400;
        int naluType = (nalu[0] >> 1) & 0x3F;

        int offset = 2;
        bool isFirst = true;

        while (offset < nalu.Length)
        {
            int chunkLen = Math.Min(maxPayload - 3, nalu.Length - offset);
            bool isEnd = (offset + chunkLen) >= nalu.Length;
            int packetLen = 12 + 3 + chunkLen;

            byte[] packet = packetLen <= _sendBuffer!.Length
                ? _sendBuffer
                : ArrayPool<byte>.Shared.Rent(packetLen);

            try
            {
                WriteRtpHeader(packet, pt, marker: isLast && isEnd);

                packet[12] = (byte)((nalu[0] & 0x81) | (49 << 1));
                packet[13] = nalu[1];

                byte fuHeader = (byte)naluType;
                if (isFirst) fuHeader |= 0x80;
                if (isEnd) fuHeader |= 0x40;
                packet[14] = fuHeader;

                nalu.Slice(offset, chunkLen).CopyTo(packet.AsSpan(15));
                _rtpSocket.SendTo(packet.AsSpan(0, packetLen), SocketFlags.None, _clientEndpoint);
            }
            finally
            {
                if (packet != _sendBuffer)
                    ArrayPool<byte>.Shared.Return(packet);
            }

            offset += chunkLen;
            isFirst = false;
        }
    }

    /// <summary>
    /// 写入RTP头部。
    /// </summary>
    /// <param name="packet">协议数据包。</param>
    /// <param name="pt">RTP 负载类型编号。</param>
    /// <param name="marker">当前 RTP 包的帧结束标记。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRtpHeader(byte[] packet, byte pt, bool marker)
    {
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0x00) | (pt & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), _sequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), _rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), _ssrc);
    }

    /// <summary>尝试取得下一个 NAL 单元及后续偏移量。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="nextOffset">下一段数据的起始偏移量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryReadNextNalu(ReadOnlySpan<byte> data, int offset, out ReadOnlySpan<byte> nalu, out int nextOffset)
    {
        return AnnexBStartCode.TryReadNext(data, offset, out nalu, out nextOffset);
    }

    /// <summary>
    /// 获取负载类型。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>获取负载类型计算得到的数值。</returns>
    private static byte GetPayloadType(PooledMediaFrame frame)
    {
        return frame.Codec switch
        {
            MediaCodec.H264 => 96,
            MediaCodec.H265 => 97,
            MediaCodec.G711A => 8,
            MediaCodec.G711U => 0,
            MediaCodec.AAC => 96,
            _ => 96
        };
    }

    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        lock (_sendSync)
            DisposeCore();
    }

    /// <summary>释放连接、队列及套接字所持有的资源。</summary>
    private void DisposeCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            IsPlaying = false;
            _pendingFrames.DisposeAsync().GetAwaiter().GetResult();
            if (_returnRtpSocket != null)
                _returnRtpSocket(_rtpSocket, _serverRtpPort);
            else
                try { _rtpSocket.Close(); } catch { }

            if (_sendBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_sendBuffer);
                _sendBuffer = null;
            }
        }
    }
}

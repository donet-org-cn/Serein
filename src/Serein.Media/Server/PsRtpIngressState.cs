using System.Net;

namespace Serein.Media.Server;

/// <summary>已登记PS/RTP 流的 RTP 入站统计快照；字节包含 RTP 头，不含 UDP/IP 头。</summary>
/// <param name="Packets">RTP 包集合或累计包数。</param>
/// <param name="Bytes">本次处理或预留的字节数。</param>
/// <param name="Bitrate">码率配置或统计值。</param>
/// <param name="BufferedPackets">进入重排缓存的包数量。</param>
/// <param name="RecoveredPackets">通过重排恢复顺序的包数量。</param>
/// <param name="LatePackets">超过重排窗口的迟到包数量。</param>
/// <param name="LostPackets">丢失的包数量。</param>
/// <param name="LastPacketUtc">最近收包的 UTC 时间。</param>
/// <param name="RemoteEndPoint">远端网络端点。</param>
public sealed record PsRtpIngressStatistics(long Packets, long Bytes, long Bitrate,
    long BufferedPackets, long RecoveredPackets, long LatePackets, long LostPackets,
    DateTime? LastPacketUtc, string? RemoteEndPoint);

/// <summary>每 SSRC 的有界乱序队列和独立入站计数，由 ReceiveSync 串行访问。</summary>
internal sealed class PsRtpIngressState
{
    /// <summary>重排缓存中保存的包数据、来源端点及接收时刻。</summary>
    /// <param name="Data">待处理的数据。</param>
    /// <param name="Remote">远端网络端点。</param>
    /// <param name="Tick">收包时的单调时钟计数。</param>
    internal sealed record Packet(byte[] Data, IPEndPoint Remote, long Tick);
    /// <summary>等待序列补齐的 RTP 包索引。</summary>
    internal readonly Dictionary<ushort, Packet> Pending = new();
    /// <summary>下一个期望的 RTP 序列号。</summary>
    internal ushort? Next;
    /// <summary>重排缓存占用的字节数。</summary>
    internal int PendingBytes;
    /// <summary>累计入缓冲、恢复及迟到的 RTP 包数量。</summary>
    internal long BufferedPackets, RecoveredPackets, LatePackets;
    /// <summary>累计收包数、字节数和首次收包的单调时间。</summary>
    private long _packets, _bytes, _firstTick;
    /// <summary>最近一次收包的 UTC 时间。</summary>
    private DateTime? _lastUtc;
    /// <summary>最近一次收包的远端端点。</summary>
    private string? _remote;
    /// <summary>当前码率统计桶的时间起点。</summary>
    private readonly long[] _bucketEpoch = new long[20];
    /// <summary>当前码率统计桶累计的字节数。</summary>
    private readonly long[] _bucketBytes = new long[20];

    /// <summary>记录收包数量、字节数、端点与时间。</summary>
    /// <param name="bytes">本次处理或预留的字节数。</param>
    /// <param name="remote">远端网络端点。</param>
    /// <param name="now">当前时间或单调时钟计数。</param>
    internal void Received(int bytes, IPEndPoint remote, long now)
    {
        if (_packets == 0) _firstTick = now;
        _packets++;
        _bytes += bytes;
        _lastUtc = DateTime.UtcNow;
        _remote = remote.ToString();
        long epoch = now / 100;
        int index = (int)(epoch % 20);
        if (_bucketEpoch[index] != epoch)
        {
            _bucketEpoch[index] = epoch;
            _bucketBytes[index] = 0;
        }
        _bucketBytes[index] += bytes;
    }

    /// <summary>读取当前接入计数并生成统计快照。</summary>
    /// <param name="lost">累计丢包数量。</param>
    /// <returns>读取当前接入计数并生成统计快照得到的PsRtpIngressStatistics对象。</returns>
    internal PsRtpIngressStatistics Snapshot(long lost)
    {
        long now = Environment.TickCount64, epoch = now / 100, bytes = 0;
        for (int i = 0; i < 20; i++)
            if (epoch - _bucketEpoch[i] < 20) bytes += _bucketBytes[i];
        long rate = _packets == 0 ? 0 : (long)(bytes * 8000.0 / Math.Clamp(now - _firstTick, 1, 2000));
        return new(_packets, _bytes, rate, BufferedPackets, RecoveredPackets,
            LatePackets, lost, _lastUtc, _remote);
    }

    /// <summary>清除当前缓存的重组或接收状态。</summary>
    internal void Clear()
    {
        Pending.Clear();
        PendingBytes = 0;
    }
}

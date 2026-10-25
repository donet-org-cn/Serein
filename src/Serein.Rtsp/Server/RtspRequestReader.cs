using System.Buffers;
using System.Globalization;
using System.Text;

namespace Serein.Rtsp.Server;

/// <summary>按消息长度与接收期限增量解析 RTSP 请求。</summary>
internal sealed class RtspRequestReader : IDisposable
{
    /// <summary>请求消息头允许的最大字节数。</summary>
    private readonly int _maxHeaderBytes;
    /// <summary>请求正文允许的最大字节数。</summary>
    private readonly int _maxBodyBytes;
    /// <summary>收到首字节后完成整条请求的期限。</summary>
    private readonly TimeSpan _requestTimeout;
    /// <summary>保存尚未解析网络数据的缓冲区。</summary>
    private byte[] _buffer;
    /// <summary>缓冲区中当前保存的有效字节数。</summary>
    private int _count;
    /// <summary>缓冲区中下一段有效数据的起始偏移。</summary>
    private int _offset;

    /// <summary>按消息长度与接收期限增量解析 RTSP 请求。</summary>
    /// <param name="maxHeaderBytes">允许的消息头字节数上限。</param>
    /// <param name="maxBodyBytes">允许的消息体字节数上限。</param>
    /// <param name="requestTimeout">完整请求的接收期限。</param>
    internal RtspRequestReader(int maxHeaderBytes, int maxBodyBytes, TimeSpan requestTimeout)
    {
        if (maxHeaderBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
        if (maxBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _maxHeaderBytes = maxHeaderBytes;
        _maxBodyBytes = maxBodyBytes;
        _requestTimeout = requestTimeout;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxHeaderBytes, 4096));
    }

    /// <summary>在长度与接收期限内增量读取完整 RTSP 请求。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>完整请求文本；连接正常结束且没有剩余数据时为 null。</returns>
    internal async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_offset > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count - _offset);
            _count -= _offset;
            _offset = 0;
        }
        // 已经播放的会话可以长期没有控制报文；仅在新请求首字节到达后开始计算完整报文接收期限。
        if (_count == 0)
        {
            _count = await stream.ReadAsync(_buffer.AsMemory(0, Math.Min(_buffer.Length, _maxHeaderBytes)), cancellationToken).ConfigureAwait(false);
            if (_count == 0) return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        int scan = 0;
        int headerLength;
        while (true)
        {
            headerLength = FindHeaderEnd(ref scan);
            if (headerLength > 0) break;
            if (_count >= _maxHeaderBytes) throw new InvalidDataException("RTSP request header exceeds the configured limit.");
            if (_count == _buffer.Length)
            {
                var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(_maxHeaderBytes, (long)_buffer.Length * 2));
                Buffer.BlockCopy(_buffer, 0, larger, 0, _count);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
            }
            int read = await stream.ReadAsync(_buffer.AsMemory(_count, Math.Min(_buffer.Length, _maxHeaderBytes) - _count), deadline.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Incomplete RTSP request header.");
            _count += read;
        }

        string header = Encoding.ASCII.GetString(_buffer, 0, headerLength);
        int bodyLength = ParseContentLength(header);
        int bufferedBody = Math.Min(bodyLength, _count - headerLength);
        _offset = headerLength + bufferedBody;
        int remaining = bodyLength - bufferedBody;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(_buffer.AsMemory(0, Math.Min(_buffer.Length, remaining)), deadline.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Incomplete RTSP request body.");
            remaining -= read;
            _offset = _count = read;
        }
        return header;
    }

    /// <summary>查找 RTSP 消息头结束的空行边界。</summary>
    /// <param name="scan">本次 NAL 扫描结果。</param>
    /// <returns>找到的位置索引；未找到时返回 -1。</returns>
    private int FindHeaderEnd(ref int scan)
    {
        for (; scan + 3 < _count; scan++)
            if (_buffer[scan] == 13 && _buffer[scan + 1] == 10 && _buffer[scan + 2] == 13 && _buffer[scan + 3] == 10)
                return scan + 4;
        return 0;
    }

    /// <summary>解析并验证 RTSP 正文长度头。</summary>
    /// <param name="header">协议头数据。</param>
    /// <returns>声明的正文长度，单位为字节。</returns>
    private int ParseContentLength(string header)
    {
        int length = 0;
        bool found = false;
        foreach (string line in header.Split("\r\n", StringSplitOptions.None))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (found || !int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                length > _maxBodyBytes)
                throw new InvalidDataException("Invalid or oversized RTSP Content-Length.");
            found = true;
        }
        return length;
    }

    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}

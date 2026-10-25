using System.Buffers;
using System.Globalization;
using System.Text;

namespace Serein.Rtsp.Client;

/// <summary>在有界缓冲区内增量读取 RTSP 响应与交错 RTP 包。</summary>
internal sealed class RtspResponseReader : IDisposable
{
    /// <summary>允许的 RTSP 消息头字节数上限。</summary>
    internal const int MaxHeaderBytes = 64 * 1024;
    /// <summary>允许的 RTSP 消息体字节数上限。</summary>
    internal const int MaxBodyBytes = 4 * 1024 * 1024;
    /// <summary>保存尚未解析网络数据的缓冲区。</summary>
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
    /// <summary>缓冲区中下一段有效数据的起始偏移。</summary>
    private int _offset;
    /// <summary>缓冲区中当前保存的有效字节数。</summary>
    private int _count;

    /// <summary>增量读取 RTSP 响应，遇到交错媒体包时先调用包回调。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="onPacket">收到完整交错 RTP 包时调用的委托。</param>
    /// <param name="cancellationToken">取消当前操作的令牌。</param>
    /// <returns>完整 RTSP 响应文本；新消息开始前连接已关闭时为 null。</returns>
    internal async Task<string?> ReadAsync(Stream stream, Action<int, ReadOnlyMemory<byte>> onPacket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await EnsureAsync(stream, 1, cancellationToken).ConfigureAwait(false)) return null;
            if (_buffer[_offset] == 0x24)
            {
                await RequireAsync(stream, 4, cancellationToken).ConfigureAwait(false);
                int channel = _buffer[_offset + 1];
                int length = (_buffer[_offset + 2] << 8) | _buffer[_offset + 3];
                await RequireAsync(stream, 4 + length, cancellationToken).ConfigureAwait(false);
                onPacket(channel, _buffer.AsMemory(_offset + 4, length));
                _offset += 4 + length;
                continue;
            }

            int headerLength = 0;
            int scan = 0;
            while (headerLength == 0)
            {
                int available = _count - _offset;
                for (; scan + 3 < available; scan++)
                    if (_buffer[_offset + scan] == 13 && _buffer[_offset + scan + 1] == 10 &&
                        _buffer[_offset + scan + 2] == 13 && _buffer[_offset + scan + 3] == 10)
                    { headerLength = scan + 4; break; }
                if (headerLength != 0) break;
                if (available >= MaxHeaderBytes) throw new InvalidDataException("RTSP response header exceeds 64 KiB.");
                await RequireAsync(stream, available + 1, cancellationToken).ConfigureAwait(false);
            }
            if (headerLength > MaxHeaderBytes) throw new InvalidDataException("RTSP response header exceeds 64 KiB.");
            string header = Encoding.ASCII.GetString(_buffer, _offset, headerLength);
            if (!header.StartsWith("RTSP/1.0 ", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid RTSP response status line.");
            int bodyLength = ContentLength(header);
            await RequireAsync(stream, headerLength + bodyLength, cancellationToken).ConfigureAwait(false);
            string response = bodyLength == 0 ? header : Encoding.ASCII.GetString(_buffer, _offset, headerLength + bodyLength);
            _offset += headerLength + bodyLength;
            return response;
        }
    }

    /// <summary>读取并验证响应中声明的正文长度。</summary>
    /// <param name="header">协议头数据。</param>
    /// <returns>正文的字节数；没有长度头时为零。</returns>
    private static int ContentLength(string header)
    {
        int length = 0;
        bool found = false;
        foreach (string line in header.Split("\r\n", StringSplitOptions.None))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (found || !int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxBodyBytes)
                throw new InvalidDataException("Invalid or oversized RTSP response Content-Length.");
            found = true;
        }
        return length;
    }

    /// <summary>继续读取网络数据，直到缓存满足本次解析所需的长度。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="length">本次处理的数据长度。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>继续读取网络数据，直到缓存满足本次解析所需的长度完成时结束的异步任务。</returns>
    private async Task RequireAsync(Stream stream, int length, CancellationToken token)
    {
        if (!await EnsureAsync(stream, length, token).ConfigureAwait(false))
            throw new EndOfStreamException("RTSP connection closed during a response or interleaved packet.");
    }

    /// <summary>按需扩展缓冲区并读取至指定的有效数据长度。</summary>
    /// <param name="stream">读取或写入协议数据的网络流。</param>
    /// <param name="length">本次处理的数据长度。</param>
    /// <param name="token">取消当前操作的令牌。</param>
    /// <returns>缓冲区已满足长度时为 true；连接结束前未能读够时为 false。</returns>
    private async Task<bool> EnsureAsync(Stream stream, int length, CancellationToken token)
    {
        if (_count - _offset >= length) return true;
        if (_offset > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count - _offset);
            _count -= _offset;
            _offset = 0;
        }
        if (_buffer.Length < length)
        {
            var larger = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(_buffer, 0, larger, 0, _count);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }
        while (_count < length)
        {
            int read = await stream.ReadAsync(_buffer.AsMemory(_count), token).ConfigureAwait(false);
            if (read == 0) return false;
            _count += read;
        }
        return true;
    }

    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = Array.Empty<byte>();
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
    }
}

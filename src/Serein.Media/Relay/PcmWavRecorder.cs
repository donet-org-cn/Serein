using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Serein.Media.Relay;

/// <summary>
/// PCM16 WAV 录音器。
/// </summary>
public sealed class PcmWavRecorder : IAsyncDisposable
{
    /// <summary>文件路径。</summary>
    private readonly string _filePath;
    /// <summary>音频采样率，单位为赫兹。</summary>
    private readonly int _sampleRate;
    /// <summary>终端实时媒体通道索引。</summary>
    private readonly int _channels;
    /// <summary>当前流或对应的处理状态。</summary>
    private FileStream? _stream;
    /// <summary>当前数据的字节数。</summary>
    private long _dataBytes;

    /// <summary>创建 WAV 录音器。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="sampleRate">音频采样率，单位为赫兹。</param>
    /// <param name="channels">交错 PCM 数据的声道数。</param>
    public PcmWavRecorder(string filePath, int sampleRate, int channels)
    {
        _filePath = filePath;
        _sampleRate = sampleRate;
        _channels = channels;
    }

    /// <summary>录音文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>写入 PCM16 音频。</summary>
    /// <param name="pcm">PCM 音频样本。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>写入 PCM16 音频完成时结束的异步任务。</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<short> pcm, CancellationToken cancellationToken = default)
    {
        if (pcm.IsEmpty) return;

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var byteLength = pcm.Length * sizeof(short);
        var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            MemoryMarshal.AsBytes(pcm.Span).CopyTo(buffer);
            await _stream!.WriteAsync(buffer.AsMemory(0, byteLength), cancellationToken).ConfigureAwait(false);
            _dataBytes += byteLength;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>在首次写入时打开音频录制文件。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>在首次写入时打开音频录制文件完成时结束的异步任务。</returns>
    private async ValueTask EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_stream != null) return;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[44];
        WriteHeader(header, 0, _sampleRate, _channels);
        await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入头部。
    /// </summary>
    /// <param name="header">协议头数据。</param>
    /// <param name="dataBytes">当前数据的字节数。</param>
    /// <param name="sampleRate">音频采样率，单位为赫兹。</param>
    /// <param name="channels">交错 PCM 数据的声道数。</param>
    private static void WriteHeader(Span<byte> header, long dataBytes, int sampleRate, int channels)
    {
        const ushort bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_stream == null) return;

        try
        {
            var header = new byte[44];
            WriteHeader(header, _dataBytes, _sampleRate, _channels);
            _stream.Seek(0, SeekOrigin.Begin);
            await _stream.WriteAsync(header).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }
}

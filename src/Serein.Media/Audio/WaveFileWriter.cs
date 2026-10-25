using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Serein.Media.Audio;

/// <summary>
/// 简单的 PCM16 WAV 写入器。
/// </summary>
public sealed class WaveFileWriter : IDisposable
{
    /// <summary>当前流或对应的处理状态。</summary>
    private readonly FileStream stream;
    /// <summary>保护实例可变数据的互斥锁。</summary>
    private readonly object syncRoot = new();
    /// <summary>已写入音频数据的字节数。</summary>
    private int dataLength;
    /// <summary>
    /// 实例是否已释放的状态标记。
    /// </summary>
    private bool disposed;

    /// <summary>
    /// 初始化 <see cref="WaveFileWriter"/> 的新实例。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="sampleRate">音频采样率，单位为赫兹。</param>
    /// <param name="channels">交错 PCM 数据的声道数。</param>
    public WaveFileWriter(string filePath, int sampleRate = 8000, short channels = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        SampleRate = sampleRate;
        Channels = channels;
        WriteHeaderPlaceholder();
    }

    /// <summary>
    /// 采样率。
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// 声道数。
    /// </summary>
    public short Channels { get; }

    /// <summary>
    /// 写入 PCM16 样本。
    /// <para>小端平台直接写入样本字节；大端平台使用固定栈缓冲转换。</para>
    /// </summary>
    /// <param name="samples">PCM 音频样本。</param>
    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (syncRoot)
        {
            ThrowIfDisposed();
            var byteCount = checked(samples.Length * 2);
            if (BitConverter.IsLittleEndian)
            {
                stream.Write(MemoryMarshal.AsBytes(samples));
            }
            else
            {
                Span<byte> buffer = stackalloc byte[512];
                for (int offset = 0; offset < samples.Length; offset += 256)
                {
                    int count = Math.Min(256, samples.Length - offset);
                    for (int i = 0; i < count; i++)
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i * 2, 2), samples[offset + i]);
                    stream.Write(buffer[..(count * 2)]);
                }
            }
            dataLength += byteCount;
        }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try { UpdateHeader(); }
            finally { stream.Dispose(); }
        }
    }

    /// <summary>在实例已释放时抛出对象释放异常。</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    /// <summary>
    /// 写入头部Placeholder。
    /// </summary>
    private void WriteHeaderPlaceholder()
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        stream.Write(new byte[4]);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        WriteInt32(16);
        WriteInt16(1);
        WriteInt16(Channels);
        WriteInt32(SampleRate);
        WriteInt32(SampleRate * Channels * 2);
        WriteInt16((short)(Channels * 2));
        WriteInt16(16);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        stream.Write(new byte[4]);
    }

    /// <summary>
    /// 更新头部。
    /// </summary>
    private void UpdateHeader()
    {
        stream.Seek(4, SeekOrigin.Begin);
        WriteInt32(36 + dataLength);
        stream.Seek(40, SeekOrigin.Begin);
        WriteInt32(dataLength);
    }

    /// <summary>
    /// 写入Int16。
    /// </summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    private void WriteInt16(short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>
    /// 写入Int32。
    /// </summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    private void WriteInt32(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}

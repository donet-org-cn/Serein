using NLayer;
using System.Buffers.Binary;
using System.Text;

namespace Serein.Media.Audio;

/// <summary>
/// 将 WAV/MP3 文件解码为 8 kHz 单声道 PCM16。
/// </summary>
public static class AudioFileDecoder
{
    /// <summary>
    /// 从音频文件加载 8k 单声道 PCM16 数据。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    public static short[] DecodeToMono8kPcm(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"音频文件不存在: {filePath}", filePath);
        }

        if (IsMp3File(filePath))
        {
            return DecodeMp3(filePath);
        }

        if (IsWavFile(filePath))
        {
            return DecodeWav(filePath);
        }

        var extension = Path.GetExtension(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => DecodeMp3(filePath),
            ".wav" => DecodeWav(filePath),
            _ => throw new NotSupportedException($"暂不支持的音频文件类型: {extension}"),
        };
    }

    /// <summary>
    /// 检测文件是否为 MP3 格式。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>检测文件是否为 MP3 格式是否成立。</returns>
    public static bool IsMp3File(string filePath)
    {
        Span<byte> header = stackalloc byte[10];
        using var fileStream = File.OpenRead(filePath);
        var read = fileStream.Read(header);
        return read >= 3 && header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33
            || read >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
    }

    /// <summary>
    /// 检测文件是否为 RIFF WAV。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>检测文件是否为 RIFF WAV是否成立。</returns>
    public static bool IsWavFile(string filePath)
    {
        Span<byte> header = stackalloc byte[12];
        using var fileStream = File.OpenRead(filePath);
        var read = fileStream.Read(header);
        return read >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';
    }

    /// <summary>把 MP3 文件解码为 PCM16 音频样本。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    private static short[] DecodeMp3(string filePath)
    {
        using var mpegFile = new MpegFile(filePath);
        var channels = Math.Max(1, mpegFile.Channels);
        var sampleRate = Math.Max(1, mpegFile.SampleRate);
        var buffer = new float[sampleRate * channels];
        var mono = new List<short>(sampleRate * 8);

        int read;
        while ((read = mpegFile.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += buffer[(frame * channels) + channel];
                }

                var sample = Math.Clamp(sum / channels, -1f, 1f);
                mono.Add((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
            }
        }

        if (mono.Count == 0)
        {
            throw new InvalidDataException("MP3 文件未解码出有效音频样本。");
        }

        return PcmAudioConverter.ConvertToMono8k(mono.ToArray(), sampleRate, 1);
    }

    /// <summary>读取 WAV 格式并转换为 PCM16 音频样本。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    private static short[] DecodeWav(string filePath)
    {
        using var fileStream = File.OpenRead(filePath);
        using var reader = new BinaryReader(fileStream, Encoding.ASCII, leaveOpen: false);

        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a WAV file");
        }

        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAV file");
        }

        var sampleRate = 0;
        var bitsPerSample = 0;
        var channels = 1;
        var dataSize = 0;
        while (fileStream.Position < fileStream.Length)
        {
            if (fileStream.Length - fileStream.Position < 8)
            {
                break;
            }

            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            if (chunkSize < 0 || fileStream.Position + chunkSize > fileStream.Length)
            {
                throw new InvalidDataException("Invalid WAV chunk size");
            }

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (audioFormat != 1)
                {
                    throw new InvalidDataException("Only PCM WAV is supported");
                }

                if (chunkSize > 16)
                {
                    reader.ReadBytes(chunkSize - 16);
                }
            }
            else if (chunkId == "data")
            {
                dataSize = chunkSize;
                break;
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }

            if ((chunkSize & 1) == 1 && fileStream.Position < fileStream.Length)
            {
                fileStream.Position++;
            }
        }

        if (sampleRate <= 0 || dataSize <= 0)
        {
            throw new InvalidDataException("Invalid WAV format");
        }

        var raw = reader.ReadBytes(dataSize);
        var bytesPerSample = bitsPerSample / 8;
        var sourceSamples = dataSize / bytesPerSample / channels;
        var mono = new short[sourceSamples];

        for (var i = 0; i < sourceSamples; i++)
        {
            var sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var frameOffset = ((i * channels) + channel) * bytesPerSample;
                sum += bitsPerSample switch
                {
                    16 => BitConverter.ToInt16(raw, frameOffset),
                    8 => (short)((raw[frameOffset] - 128) << 8),
                    _ => 0,
                };
            }

            mono[i] = (short)(sum / Math.Max(1, channels));
        }

        return PcmAudioConverter.ConvertToMono8k(mono, sampleRate, 1);
    }
}

using System.Runtime.CompilerServices;
using Serein.Media.Audio;

namespace Serein.Media.Audio;

/// <summary>
/// G.711 A-law / μ-law 编解码器。
/// <para>
/// 软电话常用 8kHz/16bit PCM 与 RTP 负载 PCMA(PT=8)、PCMU(PT=0) 之间的实时转换。
/// 所有方法均基于 Span/ReadOnlySpan，调用方可复用缓冲区避免额外分配。
/// </para>
/// </summary>
public static class G711Codec
{
    /// <summary>G.711 μ 律编码使用的偏置值。</summary>
    private const int Bias = 0x84;
    /// <summary>G.711 μ 律编码的幅度裁剪上限。</summary>
    private const int Clip = 32635;

    /// <summary>完整的 G.711 A 律字节到 PCM16 解码表。</summary>
    private static readonly short[] s_alawDecodeTable = BuildALawDecodeTable();
    /// <summary>完整的 G.711 μ 律字节到 PCM16 解码表。</summary>
    private static readonly short[] s_mulawDecodeTable = BuildMuLawDecodeTable();

    /// <summary>
    /// 将 16bit little-endian PCM 编码为 G.711 A-law。
    /// </summary>
    /// <returns>写入的字节数。</returns>
    /// <param name="pcm">PCM 音频样本。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    public static int EncodeALaw(ReadOnlySpan<short> pcm, Span<byte> destination)
    {
        if (destination.Length < pcm.Length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));

        for (int i = 0; i < pcm.Length; i++)
            destination[i] = LinearToALawSample(pcm[i]);

        return pcm.Length;
    }

    /// <summary>
    /// 将 16bit little-endian PCM 编码为 G.711 μ-law。
    /// </summary>
    /// <returns>写入的字节数。</returns>
    /// <param name="pcm">PCM 音频样本。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    public static int EncodeMuLaw(ReadOnlySpan<short> pcm, Span<byte> destination)
    {
        if (destination.Length < pcm.Length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));

        for (int i = 0; i < pcm.Length; i++)
            destination[i] = LinearToMuLawSample(pcm[i]);

        return pcm.Length;
    }

    /// <summary>
    /// 将 G.711 A-law 解码为 16bit PCM。
    /// </summary>
    /// <returns>写入的采样数。</returns>
    /// <param name="alaw">G.711 A 律编码数据。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    public static int DecodeALaw(ReadOnlySpan<byte> alaw, Span<short> destination)
    {
        if (destination.Length < alaw.Length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));

        for (int i = 0; i < alaw.Length; i++)
            destination[i] = s_alawDecodeTable[alaw[i]];

        return alaw.Length;
    }

    /// <summary>
    /// 将 G.711 μ-law 解码为 16bit PCM。
    /// </summary>
    /// <returns>写入的采样数。</returns>
    /// <param name="mulaw">G.711 μ 律编码数据。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    public static int DecodeMuLaw(ReadOnlySpan<byte> mulaw, Span<short> destination)
    {
        if (destination.Length < mulaw.Length)
            throw new ArgumentException("目标缓冲区长度不足。", nameof(destination));

        for (int i = 0; i < mulaw.Length; i++)
            destination[i] = s_mulawDecodeTable[mulaw[i]];

        return mulaw.Length;
    }

    /// <summary>
    /// 将 16bit PCM 编码为 G.711。
    /// </summary>
    /// <param name="pcmSamples">PCM 音频样本。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>将 16bit PCM 编码为 G.711后的字节数据。</returns>
    public static byte[] Encode(ReadOnlySpan<short> pcmSamples, MediaCodec codec)
    {
        var buffer = new byte[pcmSamples.Length];
        switch (codec)
        {
            case MediaCodec.G711A:
                EncodeALaw(pcmSamples, buffer);
                break;
            case MediaCodec.G711U:
                EncodeMuLaw(pcmSamples, buffer);
                break;
            default:
                Array.Fill(buffer, (byte)0);
                break;
        }

        return buffer;
    }

    /// <summary>
    /// 将 G.711 负载解码为 16bit PCM。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    public static short[] Decode(ReadOnlySpan<byte> payload, MediaCodec codec)
    {
        var samples = new short[payload.Length];
        switch (codec)
        {
            case MediaCodec.G711A:
                DecodeALaw(payload, samples);
                break;
            case MediaCodec.G711U:
                DecodeMuLaw(payload, samples);
                break;
            default:
                Array.Fill(samples, (short)0);
                break;
        }

        return samples;
    }

    /// <summary>
    /// 将 16bit PCM 编码为 G.711（VoIP 编解码类型）。
    /// </summary>
    /// <param name="pcmSamples">PCM 音频样本。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>将 16bit PCM 编码为 G.711（VoIP 编解码类型）后的字节数据。</returns>
    public static byte[] Encode(ReadOnlySpan<short> pcmSamples, AudioCodec codec)
    {
        var buffer = new byte[pcmSamples.Length];
        switch (codec)
        {
            case AudioCodec.Pcma:
                EncodeALaw(pcmSamples, buffer);
                break;
            case AudioCodec.Pcmu:
                EncodeMuLaw(pcmSamples, buffer);
                break;
            default:
                Array.Fill(buffer, (byte)0);
                break;
        }

        return buffer;
    }

    /// <summary>
    /// 将 G.711 负载解码为 16bit PCM（VoIP 编解码类型）。
    /// </summary>
    /// <param name="payload">媒体或协议消息的有效负载。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    public static short[] Decode(ReadOnlySpan<byte> payload, AudioCodec codec)
    {
        var samples = new short[payload.Length];
        switch (codec)
        {
            case AudioCodec.Pcma:
                DecodeALaw(payload, samples);
                break;
            case AudioCodec.Pcmu:
                DecodeMuLaw(payload, samples);
                break;
            default:
                Array.Fill(samples, (short)0);
                break;
        }

        return samples;
    }

    /// <summary>
    /// 将单个 16bit PCM 采样编码为 A-law 字节。
    /// </summary>
    /// <param name="sample">单个音频样本。</param>
    /// <returns>将单个 16bit PCM 采样编码为 A-law 字节计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToALawSample(short sample)
    {
        int pcm = sample;
        int mask;
        if (pcm >= 0)
        {
            mask = 0xD5;
        }
        else
        {
            mask = 0x55;
            pcm = -pcm - 8;
        }

        pcm = Math.Clamp(pcm, 0, Clip);
        int value;
        if (pcm < 256)
        {
            value = pcm >> 4;
        }
        else
        {
            int exponent = SearchSegment(pcm, s_aLawPcmSegmentEnd);
            value = (exponent << 4) | ((pcm >> (exponent + 3)) & 0x0F);
        }
        return (byte)(value ^ mask);
    }

    /// <summary>
    /// 将单个 16bit PCM 采样编码为 μ-law 字节。
    /// </summary>
    /// <param name="sample">单个音频样本。</param>
    /// <returns>将单个 16bit PCM 采样编码为 μ-law 字节计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToMuLawSample(short sample)
    {
        int pcm = sample;
        int mask;
        int segment;
        byte uval;

        if (pcm < 0)
        {
            pcm = Bias - pcm;
            mask = 0x7F;
        }
        else
        {
            pcm += Bias;
            mask = 0xFF;
        }

        if (pcm > Clip)
            pcm = Clip;

        segment = SearchSegment(pcm, s_uLawSegmentEnd);
        uval = (byte)((segment << 4) | ((pcm >> (segment + 3)) & 0x0F));
        return (byte)(uval ^ mask);
    }

    /// <summary>G.711 A 律幅度分段的末端阈值。</summary>
    private static readonly int[] s_aLawPcmSegmentEnd = [0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF, 0x3FFF, 0x7FFF];
    /// <summary>G.711 μ 律幅度分段的末端阈值。</summary>
    private static readonly int[] s_uLawSegmentEnd = [0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF, 0x3FFF, 0x7FFF];

    /// <summary>根据样本幅度查找 G.711 分段编号。</summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <param name="table">编解码映射表。</param>
    /// <returns>根据样本幅度查找 G.711 分段编号计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SearchSegment(int value, int[] table)
    {
        for (int i = 0; i < table.Length; i++)
        {
            if (value <= table[i])
                return i;
        }
        return table.Length - 1;
    }

    /// <summary>
    /// 构建ALawDecodeTable。
    /// </summary>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    private static short[] BuildALawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < table.Length; i++)
        {
            int value = i ^ 0x55;
            int sign = value & 0x80;
            int exponent = (value & 0x70) >> 4;
            int data = value & 0x0F;
            data <<= 4;
            data += 8;
            if (exponent != 0)
                data += 0x100;
            if (exponent > 1)
                data <<= exponent - 1;
            table[i] = (short)(sign == 0 ? -data : data);
        }
        return table;
    }

    /// <summary>
    /// 构建MuLawDecodeTable。
    /// </summary>
    /// <returns>按样本顺序排列的 PCM16 音频数据。</returns>
    private static short[] BuildMuLawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < table.Length; i++)
        {
            int value = ~i & 0xFF;
            int sign = value & 0x80;
            int exponent = (value >> 4) & 0x07;
            int mantissa = value & 0x0F;
            int sample = ((mantissa << 3) + Bias) << exponent;
            sample -= Bias;
            table[i] = (short)(sign != 0 ? -sample : sample);
        }
        return table;
    }
}

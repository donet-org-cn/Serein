using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Serein.Media.Audio;

/// <summary>
/// PCM 音频重采样与声道转换工具。
/// </summary>
public static class PcmAudioConverter
{
    /// <summary>
    /// 将任意采样率和声道数的 PCM16 转为 8k 单声道。
    /// </summary>
    /// <param name="samples">PCM 音频样本。</param>
    /// <param name="sampleRate">音频采样率，单位为赫兹。</param>
    /// <param name="channels">交错 PCM 数据的声道数。</param>
    /// <returns>按 8 kHz 单声道排列的 PCM16 样本；没有完整输入样本时为空数组。</returns>
    public static short[] ConvertToMono8k(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        if (samples.IsEmpty)
        {
            return [];
        }

        channels = Math.Max(channels, 1);
        if (sampleRate <= 0)
        {
            throw new InvalidDataException("Invalid sample rate");
        }

        var monoSampleCount = samples.Length / channels;
        if (monoSampleCount == 0)
            return [];
        var mono = new short[monoSampleCount];
        for (var i = 0; i < monoSampleCount; i++)
        {
            var sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                sum += samples[(i * channels) + ch];
            }

            mono[i] = (short)(sum / channels);
        }

        if (sampleRate == 8000)
        {
            return mono;
        }

        var targetLength = (int)Math.Max(1, Math.Round(mono.Length * 8000d / sampleRate));
        var resampled = new short[Math.Max(targetLength, 1)];
        var sampleRateDouble = (double)sampleRate;
        var downsampling = sampleRate > 8000;
        var cutoff = 0.45 * Math.Min(1d, 8000d / sampleRate);
        const int filterRadius = 32;
        // 常见采样率的相位周期较短，例如 44.1 kHz 对应 80 个相位。缓存只在本次调用内使用且限制容量，避免任意采样率留下无界表，也避免短音频的预计算成本超过滤波本身。
        int phaseCount = 8000 / GreatestCommonDivisor(sampleRate, 8000);
        double[][]? phases = null;
        double[]? phaseSums = null;
        if (downsampling && phaseCount <= 128 && resampled.Length >= phaseCount * 2)
        {
            phases = new double[phaseCount][];
            phaseSums = new double[phaseCount];
            for (int phase = 0; phase < phaseCount; phase++)
            {
                var weights = phases[phase] = new double[filterRadius * 2];
                double fraction = ((long)phase * sampleRate % 8000) / 8000d;
                for (int tap = 0; tap < weights.Length; tap++)
                {
                    weights[tap] = FilterWeight(tap - filterRadius + 1 - fraction, cutoff);
                    phaseSums[phase] += weights[tap];
                }
            }
        }
        for (var i = 0; i < resampled.Length; i++)
        {
            var unclampedPosition = i * sampleRateDouble / 8000d;
            var sourcePosition = Math.Clamp(unclampedPosition, 0d, mono.Length - 1d);
            if (!downsampling)
            {
                var left = (int)Math.Floor(sourcePosition);
                var right = Math.Min(mono.Length - 1, left + 1);
                var fraction = sourcePosition - left;
                resampled[i] = (short)Math.Clamp(
                    mono[left] + (mono[right] - mono[left]) * fraction,
                    short.MinValue,
                    short.MaxValue);
                continue;
            }

            // 窗函数 sinc 低通滤波先抑制高于目标奈奎斯特频率的成分；目标采样率 8 kHz 的界限为 4 kHz，避免高频混叠进语音频带。
            var center = (int)Math.Floor(sourcePosition);
            double sum = 0;
            double weightSum = 0;
            if (phases != null && sourcePosition == unclampedPosition)
            {
                var weights = phases[i % phaseCount];
                int start = center - filterRadius + 1;
                int firstTap = Math.Max(0, -start);
                int lastTap = Math.Min(weights.Length, mono.Length - start);
                sum = WeightedSum(mono.AsSpan(start + firstTap, lastTap - firstTap),
                    weights.AsSpan(firstTap, lastTap - firstTap));
                if (firstTap == 0 && lastTap == weights.Length)
                    weightSum = phaseSums![i % phaseCount];
                else
                    for (int tap = firstTap; tap < lastTap; tap++) weightSum += weights[tap];
            }
            else
            {
                for (var sourceIndex = center - filterRadius + 1;
                     sourceIndex <= center + filterRadius;
                     sourceIndex++)
                {
                    if ((uint)sourceIndex >= (uint)mono.Length) continue;
                    var weight = FilterWeight(sourceIndex - sourcePosition, cutoff);
                    sum += mono[sourceIndex] * weight;
                    weightSum += weight;
                }
            }

            var filtered = weightSum == 0 ? 0 : sum / weightSum;
            resampled[i] = (short)Math.Clamp(filtered, short.MinValue, short.MaxValue);
        }

        return resampled;
    }

    /// <summary>计算两个采样率整数的最大公约数。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>两个输入整数的最大公约数。</returns>
    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }

    /// <summary>计算窗函数修正的低通 sinc 滤波权重。</summary>
    /// <param name="distance">样本位置到滤波中心的距离。</param>
    /// <param name="cutoff">相对于输入采样率归一化的低通截止频率。</param>
    /// <returns>当前采样位置的窗函数低通滤波权重。</returns>
    private static double FilterWeight(double distance, double cutoff)
    {
        var normalizedDistance = distance / 32;
        var window = 0.5 * (1 + Math.Cos(Math.PI * normalizedDistance));
        var argument = 2 * Math.PI * cutoff * distance;
        var sinc = Math.Abs(argument) < 1e-12 ? 1d : Math.Sin(argument) / argument;
        return 2 * cutoff * sinc * window;
    }

    /// <summary>计算重采样窗口内样本与相位权重的点积。</summary>
    /// <param name="samples">PCM 音频样本。</param>
    /// <param name="weights">当前重采样相位对应的滤波权重。</param>
    /// <returns>当前滤波窗口的样本加权和。</returns>
    private static double WeightedSum(ReadOnlySpan<short> samples, ReadOnlySpan<double> weights)
    {
        int i = 0;
        double sum = 0;
        ref double coefficients = ref MemoryMarshal.GetReference(weights);
        if (Vector256.IsHardwareAccelerated)
        {
            var accumulator = Vector256<double>.Zero;
            for (; i <= samples.Length - 4; i += 4)
            {
                var values = Vector256.Create((double)samples[i], samples[i + 1], samples[i + 2], samples[i + 3]);
                accumulator += values * Vector256.LoadUnsafe(ref coefficients, (nuint)i);
            }
            sum = Vector256.Sum(accumulator);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var accumulator = Vector128<double>.Zero;
            for (; i <= samples.Length - 2; i += 2)
            {
                var values = Vector128.Create((double)samples[i], samples[i + 1]);
                accumulator += values * Vector128.LoadUnsafe(ref coefficients, (nuint)i);
            }
            sum = Vector128.Sum(accumulator);
        }
        for (; i < samples.Length; i++) sum += samples[i] * weights[i];
        return sum;
    }
}

using Serein.Media.Audio;

internal static class AudioTests
{
    public static readonly (string Name, Func<Task> Body)[] Cases =
    [
        ("G.711A matches reference vectors", () => Run(G711)),
        ("PCM downsampling rejects out-of-band energy", () => Run(Resample)),
        ("WAV recording and managed decoding preserve PCM samples", () => Run(Wav)),
    ];

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private static void G711()
    {
        short[] pcm = [-32768, -16000, -4000, -1000, -1, 0, 1, 1000, 4000, 16000, 32767];
        byte[] expected = [0x2A, 0x3A, 0x1A, 0x7A, 0x55, 0xD5, 0xD5, 0xFA, 0x9A, 0xBA, 0xAA];
        byte[] actual = new byte[pcm.Length];
        G711Codec.EncodeALaw(pcm, actual);
        Assert.SequenceEqual(expected, actual, "A-law reference vector");
    }

    private static void Resample()
    {
        const int rate = 48000;
        short[] high = Enumerable.Range(0, rate).Select(i => (short)(Math.Sin(2 * Math.PI * 10000 * i / rate) * 20000)).ToArray();
        short[] low = Enumerable.Range(0, rate).Select(i => (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 20000)).ToArray();
        var highOutput = PcmAudioConverter.ConvertToMono8k(high, rate, 1);
        var lowOutput = PcmAudioConverter.ConvertToMono8k(low, rate, 1);
        double Energy(short[] samples) => samples.Skip(100).Take(samples.Length - 200).Average(s => (double)s * s);
        Assert.Equal(8000, highOutput.Length, "resampled sample count");
        Assert.True(Energy(highOutput) < Energy(lowOutput) * 0.01, "Aliased high frequency escaped the low-pass filter.");
        Assert.Equal(0, PcmAudioConverter.ConvertToMono8k([1], 16000, 2).Length, "partial stereo sample");
    }

    private static void Wav()
    {
        string file = Path.Combine(Path.GetTempPath(), $"serein-audio-{Guid.NewGuid():N}.wav");
        short[] samples = [0, 1, -1, 1000, -1000, short.MinValue, short.MaxValue];
        try
        {
            using (var writer = new WaveFileWriter(file)) writer.Write(samples);
            var decoded = AudioFileDecoder.DecodeToMono8kPcm(file);
            Assert.True(samples.SequenceEqual(decoded), "WAV PCM samples changed.");
            Assert.Equal(44 + samples.Length * 2, (int)new FileInfo(file).Length, "WAV header length");
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}

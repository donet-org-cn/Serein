using Microsoft.Extensions.Logging;
using Serein.Media.Audio;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Serein.Media.Relay;

/// <summary>
/// RTP 服务端音频后台处理器。
/// <para>转写和录音分别使用独立有界队列，主发布链路只做非阻塞投递。</para>
/// </summary>
public sealed class RtpServerAudioProcessor : IAsyncDisposable
{
    /// <summary>查找或分组所用的键。</summary>
    private readonly MediaStreamKey _key;
    /// <summary>服务配置。</summary>
    private readonly RtpServerAudioProcessingOptions _options;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>等待语音转写的有界音频通道。</summary>
    private readonly Channel<PooledMediaFrame>? _transcriptionChannel;
    /// <summary>等待音频录制的有界处理通道。</summary>
    private readonly Channel<PooledMediaFrame>? _recordingChannel;
    /// <summary>等待业务处理的有界音频通道。</summary>
    private readonly Channel<PooledMediaFrame>? _processedChannel;
    /// <summary>
    /// 用于取消后台操作的令牌源。
    /// </summary>
    private readonly CancellationTokenSource _cts = new();
    /// <summary>处理音频分支的后台任务集合。</summary>
    private readonly List<Task> _workers = [];
    /// <summary>媒体录制器。</summary>
    private PcmWavRecorder? _recorder;

    /// <summary>创建处理器。</summary>
    /// <param name="key">查找或分组所用的键。</param>
    /// <param name="options">配置选项。</param>
    /// <param name="logger">日志记录器。</param>
    public RtpServerAudioProcessor(MediaStreamKey key, RtpServerAudioProcessingOptions options, ILogger logger)
    {
        _key = key;
        _options = options;
        _logger = logger;

        if (options.EnableTranscription && options.Transcriber != null)
        {
            _transcriptionChannel = CreateChannel(options);
            _workers.Add(Task.Run(() => RunTranscriptionAsync(_cts.Token)));
        }

        if (options.EnableAudioRecording)
        {
            _recordingChannel = CreateChannel(options);
            _workers.Add(Task.Run(() => RunRecordingAsync(_cts.Token)));
        }

        if (options.ProcessedAudioSink != null)
        {
            _processedChannel = CreateChannel(options);
            _workers.Add(Task.Run(() => RunProcessedAsync(_cts.Token)));
        }
    }

    /// <summary>非阻塞投递媒体帧。各功能队列相互独立，拥塞时只丢自己的队列。</summary>
    /// <param name="frame">媒体帧。</param>
    public void TryEnqueue(PooledMediaFrame frame)
    {
        if (!_options.Enabled || frame.Kind != MediaFrameKind.Audio)
            return;

        TryWriteRetained(_transcriptionChannel, frame);
        TryWriteRetained(_recordingChannel, frame);
        TryWriteRetained(_processedChannel, frame);
    }

    /// <summary>
    /// 创建Channel。
    /// </summary>
    /// <param name="options">配置选项。</param>
    /// <returns>创建Channel得到的Channel&lt;PooledMediaFrame&gt;对象。</returns>
    private static Channel<PooledMediaFrame> CreateChannel(RtpServerAudioProcessingOptions options)
    {
        return Channel.CreateBounded<PooledMediaFrame>(new BoundedChannelOptions(Math.Max(options.QueueCapacity, 8))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>为音频处理队列保留帧引用并尝试入队。</summary>
    /// <param name="channel">媒体通道。</param>
    /// <param name="frame">媒体帧。</param>
    private void TryWriteRetained(Channel<PooledMediaFrame>? channel, PooledMediaFrame frame)
    {
        if (channel == null || !frame.TryRetain())
            return;

        if (channel.Writer.TryWrite(frame))
            return;

        if (_options.DropOldestWhenBusy && channel.Reader.TryRead(out var dropped))
        {
            dropped.Release();
            if (channel.Writer.TryWrite(frame))
                return;
        }

        frame.Release();
    }

    /// <summary>持续消费需要语音转写的音频帧。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>持续消费需要语音转写的音频帧完成时结束的异步任务。</returns>
    private async Task RunTranscriptionAsync(CancellationToken cancellationToken)
    {
        await RunFrameLoopAsync(_transcriptionChannel!, ProcessTranscriptionFrameAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>持续消费需要写入录音文件的音频帧。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>持续消费需要写入录音文件的音频帧完成时结束的异步任务。</returns>
    private async Task RunRecordingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunFrameLoopAsync(_recordingChannel!, ProcessRecordingFrameAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_recorder != null)
            {
                await _recorder.DisposeAsync().ConfigureAwait(false);
                _recorder = null;
            }
        }
    }

    /// <summary>持续调用业务音频处理器。</summary>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>持续调用业务音频处理器完成时结束的异步任务。</returns>
    private async Task RunProcessedAsync(CancellationToken cancellationToken)
    {
        await RunFrameLoopAsync(_processedChannel!, ProcessFrameAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>消费指定音频队列并在处理后归还帧引用。</summary>
    /// <param name="channel">媒体通道。</param>
    /// <param name="process">处理媒体数据的委托。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>消费指定音频队列并在处理后归还帧引用完成时结束的异步任务。</returns>
    private async Task RunFrameLoopAsync(
        Channel<PooledMediaFrame> channel,
        Func<PooledMediaFrame, CancellationToken, ValueTask> process,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await process(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RTP 服务端音频后台处理失败 [{Key}]", _key);
                }
                finally
                {
                    frame.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (channel.Reader.TryRead(out var frame))
                frame.Release();
        }
    }

    /// <summary>
    /// 处理Transcription帧，并异步完成操作。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>处理Transcription帧，并异步完成操作完成时结束的异步任务。</returns>
    private async ValueTask ProcessTranscriptionFrameAsync(PooledMediaFrame frame, CancellationToken cancellationToken)
    {
        var decoded = DecodeFrame(frame);
        if (decoded.Length == 0) return;

        try
        {
            var context = CreateContext(frame);
            await _options.Transcriber!.PushPcmAsync(context, decoded.Memory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// 处理录制帧，并异步完成操作。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>处理录制帧，并异步完成操作完成时结束的异步任务。</returns>
    private async ValueTask ProcessRecordingFrameAsync(PooledMediaFrame frame, CancellationToken cancellationToken)
    {
        var decoded = DecodeFrame(frame);
        if (decoded.Length == 0) return;

        try
        {
            await GetRecorder().WriteAsync(decoded.Memory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// 处理帧，并异步完成操作。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>处理帧，并异步完成操作完成时结束的异步任务。</returns>
    private async ValueTask ProcessFrameAsync(PooledMediaFrame frame, CancellationToken cancellationToken)
    {
        var decoded = DecodeFrame(frame);
        if (decoded.Length == 0) return;

        try
        {
            var context = CreateContext(frame);
            if (_options.ProcessedAudioSink != null)
                await _options.ProcessedAudioSink.WriteAsync(context, decoded.Memory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// 获取Recorder。
    /// </summary>
    /// <returns>获取Recorder得到的PcmWavRecorder对象。</returns>
    private PcmWavRecorder GetRecorder()
    {
        if (_recorder != null) return _recorder;

        _recorder = new PcmWavRecorder(ResolveAudioRecordPath(), _options.SampleRate, _options.Channels);
        _logger.LogInformation("RTP 服务端音频录音开始 [{Key}] -> {Path}", _key, _recorder.FilePath);
        return _recorder;
    }

    /// <summary>根据流标识与配置生成音频录制路径。</summary>
    /// <returns>根据流标识与配置生成音频录制路径对应的文本。</returns>
    private string ResolveAudioRecordPath()
    {
        var now = DateTimeOffset.Now;
        var path = _options.AudioRecordPath
            .Replace("{StreamId}", _key.StreamId)
            .Replace("{Channel}", _key.Channel)
            .Replace("{Date}", now.ToString("yyyyMMdd"))
            .Replace("{Time}", now.ToString("HHmmss"))
            .Replace("{DateTime}", now.ToString("yyyyMMdd_HHmmss"));

        return path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? path : path + ".wav";
    }

    /// <summary>
    /// 创建上下文。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>创建上下文得到的RtpAudioProcessingContext对象。</returns>
    private RtpAudioProcessingContext CreateContext(PooledMediaFrame frame)
    {
        return new RtpAudioProcessingContext(
            _key.StreamId,
            _key.Channel,
            frame.Codec,
            frame.TimestampMs,
            _options.SampleRate,
            _options.Channels);
    }

    /// <summary>把媒体帧中的音频解码为 PCM 样本。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>把媒体帧中的音频解码为 PCM 样本得到的PooledPcmFrame对象。</returns>
    private static PooledPcmFrame DecodeFrame(PooledMediaFrame frame)
    {
        var maxSamples = GetMaxSampleCount(frame);
        if (maxSamples <= 0) return default;

        var buffer = ArrayPool<short>.Shared.Rent(maxSamples);
        var samples = Decode(frame, buffer);
        if (samples <= 0)
        {
            ArrayPool<short>.Shared.Return(buffer);
            return default;
        }

        return new PooledPcmFrame(buffer, samples);
    }

    /// <summary>
    /// 获取MaxSample数量。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>获取MaxSample数量计算得到的数值。</returns>
    private static int GetMaxSampleCount(PooledMediaFrame frame)
    {
        return frame.Codec switch
        {
            MediaCodec.G711A or MediaCodec.G711U => frame.Length,
            MediaCodec.PCM => frame.Length / sizeof(short),
            _ => 0,
        };
    }

    /// <summary>把压缩音频负载解码为 PCM 样本。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>实际写入的元素数量。</returns>
    private static int Decode(PooledMediaFrame frame, short[] destination)
    {
        var span = frame.GetSpan();
        return frame.Codec switch
        {
            MediaCodec.G711A => G711Codec.DecodeALaw(span, destination),
            MediaCodec.G711U => G711Codec.DecodeMuLaw(span, destination),
            MediaCodec.PCM => CopyPcm(span, destination),
            _ => 0,
        };
    }

    /// <summary>把 PCM 字节负载复制转换为短整型样本。</summary>
    /// <param name="source">源数据。</param>
    /// <param name="destination">接收转换结果的目标缓冲区。</param>
    /// <returns>把 PCM 字节负载复制转换为短整型样本计算得到的数值。</returns>
    private static int CopyPcm(ReadOnlySpan<byte> source, Span<short> destination)
    {
        var samples = MemoryMarshal.Cast<byte, short>(source);
        samples.CopyTo(destination);
        return samples.Length;
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _transcriptionChannel?.Writer.TryComplete();
        _recordingChannel?.Writer.TryComplete();
        _processedChannel?.Writer.TryComplete();

        foreach (var worker in _workers)
        {
            try { await worker.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
    }

    /// <summary>在多个音频处理分支间引用计数共享的池化 PCM 帧。</summary>
    private readonly struct PooledPcmFrame : IDisposable
    {
        /// <summary>读写字节数据的缓冲区。</summary>
        private readonly short[]? _buffer;

        /// <summary>
        /// 初始化 PooledPcmFrame 的新实例。
        /// </summary>
        /// <param name="buffer">数据缓冲区。</param>
        /// <param name="length">数据长度或数量。</param>
        public PooledPcmFrame(short[] buffer, int length)
        {
            _buffer = buffer;
            Length = length;
        }

        /// <summary>
        /// 获取 长度。
        /// </summary>
        public int Length { get; }
        /// <summary>
        /// 获取 Memory。
        /// </summary>
        public ReadOnlyMemory<short> Memory => _buffer == null ? ReadOnlyMemory<short>.Empty : _buffer.AsMemory(0, Length);

        /// <summary>释放当前实例拥有的资源。</summary>
        public void Dispose()
        {
            if (_buffer != null)
                ArrayPool<short>.Shared.Return(_buffer);
        }
    }
}

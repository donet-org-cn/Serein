using System.Threading.Channels;

namespace Serein.Media.Relay;

/// <summary>
/// 媒体流订阅。持有该对象期间持续接收数据，Dispose 后自动取消订阅。
/// <para>
/// 内部使用有界 Channel；消费者处理完帧后需调用 Release。
/// 如果消费者过慢导致缓冲满，本次写入会失败，由发布方释放本次 Retain 的引用。
/// </para>
/// </summary>
public sealed class MediaStreamSubscription : IAsyncDisposable
{
    /// <summary>承载订阅数据的异步通道。</summary>
    private readonly Channel<PooledMediaFrame> _channel;
    /// <summary>资源释放时执行的回调。</summary>
    private readonly Action<MediaStreamSubscription> _onDispose;
    /// <summary>是否在积压时丢弃过时帧。</summary>
    private readonly bool _dropStaleFrames;
    /// <summary>保护媒体帧入队与队列恢复的互斥锁。</summary>
    private readonly object _writeSync = new();
    /// <summary>是否等待可恢复解码的关键视频帧。</summary>
    private bool _awaitingVideoKeyFrame;
    /// <summary>当前订阅是否已经结束。</summary>
    private int _completed;
    /// <summary>
    /// 实例是否已释放的状态标记。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化 MediaStreamSubscription 的新实例。
    /// </summary>
    /// <param name="onDispose">资源释放时执行的回调。</param>
    /// <param name="capacity">订阅缓冲区容量。</param>
    /// <param name="dropStaleFrames">是否在积压时丢弃过时帧。</param>
    internal MediaStreamSubscription(Action<MediaStreamSubscription> onDispose, int capacity = 256,
        bool dropStaleFrames = false)
    {
        _onDispose = onDispose;
        _dropStaleFrames = dropStaleFrames;
        _channel = Channel.CreateBounded<PooledMediaFrame>(new BoundedChannelOptions(Math.Max(capacity, 16))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            // 释放操作可能与消费者并发排空队列中的引用。
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>流标识。</summary>
    public MediaStreamKey Key { get; internal init; }

    /// <summary>订阅 ID。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>订阅者描述（如 IP 地址、HTTP 路径等）。</summary>
    public string? Description { get; internal init; }

    /// <summary>读取端。</summary>
    public ChannelReader<PooledMediaFrame> Reader => _channel.Reader;

    /// <summary>向此订阅推送一个帧。返回 false 时调用方必须释放本次 Retain。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    internal bool TryWrite(PooledMediaFrame frame)
    {
        if (!_dropStaleFrames)
            return Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _completed) == 0 &&
                _channel.Writer.TryWrite(frame);

        lock (_writeSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _completed) != 0)
                return false;

            bool currentIsVideoConfiguration = false;
            if (_awaitingVideoKeyFrame && frame.Kind == MediaFrameKind.Video &&
                !frame.IsKeyFrame)
            {
                currentIsVideoConfiguration = IsVideoConfigurationOnly(frame, out _);
                if (!currentIsVideoConfiguration)
                    return false;
            }

            if (_channel.Writer.TryWrite(frame))
            {
                if (frame.Kind == MediaFrameKind.Video && frame.IsKeyFrame)
                    _awaitingVideoKeyFrame = false;
                return true;
            }

            // 实时消费者已落后。保留旧队列会增加数秒延迟，并可能从 GOP 中间恢复，因此丢弃过时积压。
            var droppedVideo = false;
            List<(PooledMediaFrame Frame, byte Mask)>? configurations = null;
            while (_channel.Reader.TryRead(out var staleFrame))
            {
                droppedVideo |= staleFrame.Kind == MediaFrameKind.Video;
                if (IsVideoConfigurationOnly(staleFrame, out byte parameterSetMask))
                {
                    configurations ??= new List<(PooledMediaFrame, byte)>();
                    configurations.Add((staleFrame, parameterSetMask));
                }
                else
                {
                    staleFrame.Release();
                }
            }

            if (droppedVideo)
                _awaitingVideoKeyFrame = true;

            if (!currentIsVideoConfiguration && frame.Kind == MediaFrameKind.Video && !frame.IsKeyFrame)
                currentIsVideoConfiguration = IsVideoConfigurationOnly(frame, out _);

            // 在恢复 I 帧之前保留最新的 VPS/SPS/PPS；它们接管从队列移出的引用，因此无需再次 Retain。
            if (configurations != null)
            {
                byte coveredParameterSets = 0;
                for (int i = configurations.Count - 1; i >= 0; i--)
                {
                    if ((configurations[i].Mask & ~coveredParameterSets) == 0)
                    {
                        configurations[i].Frame.Release();
                        configurations.RemoveAt(i);
                    }
                    else
                    {
                        coveredParameterSets |= configurations[i].Mask;
                    }
                }

                foreach (var configuration in configurations)
                {
                    if (!_channel.Writer.TryWrite(configuration.Frame))
                        configuration.Frame.Release();
                }
            }

            if (_awaitingVideoKeyFrame && frame.Kind == MediaFrameKind.Video &&
                !frame.IsKeyFrame && !currentIsVideoConfiguration)
                return false;

            if (!_channel.Writer.TryWrite(frame))
                return false;

            if (frame.Kind == MediaFrameKind.Video && frame.IsKeyFrame)
                _awaitingVideoKeyFrame = false;
            return true;
        }
    }

    /// <summary>
    /// 判断是否视频ConfigurationOnly。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    /// <returns>是否视频ConfigurationOnly的判定结果。</returns>
    private static bool IsVideoConfigurationOnly(PooledMediaFrame frame, out byte parameterSetMask)
    {
        parameterSetMask = 0;
        if (frame.Kind != MediaFrameKind.Video ||
            frame.Codec is not (MediaCodec.H264 or MediaCodec.H265))
            return false;

        var info = VideoNalUnitHelper.Analyze(frame.GetSpan(), frame.Codec);
        parameterSetMask = info.ParameterSetMask;
        return info.HasParameterSet && !info.HasVcl;
    }

    /// <summary>标记写入端完成。</summary>
    internal void Complete()
    {
        lock (_writeSync)
        {
            Volatile.Write(ref _completed, 1);
            _channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    /// <returns>释放当前实例拥有的资源完成时结束的异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            lock (_writeSync)
            {
                Volatile.Write(ref _completed, 1);
                _channel.Writer.TryComplete();

                while (_channel.Reader.TryRead(out var frame))
                {
                    frame.Release();
                }
            }

            _onDispose(this);
        }
        return ValueTask.CompletedTask;
    }
}

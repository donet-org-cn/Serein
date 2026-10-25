namespace Serein.Media.Relay;

// 将用户回调调度到写入线程之外，第三方录制器也遵守这一规则。
/// <summary>为内部录制器补充录制开始和停止的生命周期通知。</summary>
internal sealed class NotifyingStreamRecorder : IStreamRecorder
{
    /// <summary>内部使用的媒体流中心。</summary>
    private readonly IStreamRecorder _inner;
    /// <summary>媒体流生命周期控制器。</summary>
    private readonly IStreamRecorderLifecycle _lifecycle;
    /// <summary>媒体流注册与订阅中心。</summary>
    private readonly MediaStreamHub _hub;
    /// <summary>查找或分组所用的键。</summary>
    private readonly MediaStreamKey _key;
    /// <summary>保护录制生命周期通知的互斥锁。</summary>
    private readonly object _notificationSync = new();
    /// <summary>录制开始通知是否已经发出。</summary>
    private bool _notified;

    /// <summary>
    /// 初始化 NotifyingStreamRecorder 的新实例。
    /// </summary>
    /// <param name="inner">负责实际写入媒体文件的内部录制器。</param>
    /// <param name="lifecycle">内部录制器提供的生命周期完成信号。</param>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    /// <param name="key">查找或分组所用的键。</param>
    public NotifyingStreamRecorder(IStreamRecorder inner, IStreamRecorderLifecycle lifecycle, MediaStreamHub hub, MediaStreamKey key)
    {
        _inner = inner; _lifecycle = lifecycle; _hub = hub; _key = key;
        _ = ObserveStartAsync();
    }

    /// <summary>观察录制开始回调的异步完成。</summary>
    /// <returns>观察录制开始回调的异步完成完成时结束的异步任务。</returns>
    private async Task ObserveStartAsync()
    {
        try
        {
            await _lifecycle.RecordingStarted.ConfigureAwait(false);
            await Task.Yield();
            NotifyStart();
        }
        catch (Exception ex) { _hub.OnRecorderLifecycleError(ex); }
    }

    /// <summary>在首次实际写入时通知录制开始。</summary>
    private void NotifyStart()
    {
        lock (_notificationSync)
        {
            if (_notified || !_lifecycle.RecordingStarted.IsCompletedSuccessfully) return;
            _notified = true;
            if (_lifecycle.RecordingStarted.Result is { Length: > 0 } path)
                _ = _hub.OnRecordingStarted(new() { StreamId = _key.StreamId, Channel = _key.Channel, FilePath = path });
        }
    }

    /// <summary>处理输入媒体帧并按录制器约定持有或复制数据。</summary>
    /// <param name="frame">媒体帧。</param>
    public void OnFrame(PooledMediaFrame frame) => _inner.OnFrame(frame);
    /// <summary>结束当前录制并返回文件路径、字节数和时长。</summary>
    /// <returns>内部录制器返回的文件路径、文件字节数和录制时长。</returns>
    public (string FilePath, long FileSize, TimeSpan Duration) Stop()
    {
        var result = _inner.Stop();
        NotifyStart();
        return result;
    }
    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose() => _inner.Dispose();
}

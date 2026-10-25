using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Threading.Channels;
using Serein.Rtsp.Mux;
using Serein.Media;
using Serein.Media.Relay;

namespace Serein.Rtsp.Relay;

/// <summary>
/// 流录制器。将媒体帧写入 FLV 文件或裸 ES 文件。
/// <para>
/// 使用 ArrayPool 缓冲区进行 FLV 封装，尽量减少分配。
/// </para>
/// </summary>
public sealed class StreamRecorder : IStreamRecorder, IStreamRecorderLifecycle
{
    /// <summary>重复警告的最小输出间隔，单位为毫秒。</summary>
    private const long WarningIntervalMs = 5000;
    /// <summary>服务配置。</summary>
    private readonly StreamOptions _options;
    /// <summary>
    /// 用于记录运行日志的日志记录器。
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>把媒体帧写成 FLV 标签的封装器。</summary>
    private readonly FlvMuxer _flvMuxer;
    /// <summary>保护实例可变状态的互斥锁。</summary>
    private readonly object _lock = new();
    /// <summary>保护录制队列与恢复状态的互斥锁。</summary>
    private readonly object _queueLock = new();
    /// <summary>等待处理的媒体帧队列。</summary>
    private readonly Channel<PooledMediaFrame> _frameQueue;
    /// <summary>串行写入录制文件的后台任务。</summary>
    private readonly Task _writerTask;
    /// <summary>录制开始事件的通知委托。</summary>
    private readonly TaskCompletionSource<string?> _recordingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    /// <summary>当前流是否已经开始录制。</summary>
    public Task<string?> RecordingStarted => _recordingStarted.Task;
    /// <summary>当前录制文件的输出流。</summary>
    private FileStream? _fileStream;
    /// <summary>当前录制文件的路径。</summary>
    private string? _currentFilePath;
    /// <summary>当前已写入的文件字节数。</summary>
    private long _fileSize;
    /// <summary>累计媒体帧数量。</summary>
    private long _frameCount;
    /// <summary>起始时间。</summary>
    private DateTimeOffset _startTime;
    /// <summary>当前录制器是否正在录制。</summary>
    private volatile bool _isRecording;
    /// <summary>当前录制器是否接受新帧。</summary>
    private volatile bool _acceptingFrames;
    /// <summary>是否等待可恢复解码的关键视频帧。</summary>
    private bool _awaitingVideoKeyFrame;
    /// <summary>写入器是否等待可解码的恢复关键帧。</summary>
    private bool _writerAwaitingVideoKeyFrame;
    /// <summary>恢复关键帧前暂存的最新视频参数集。</summary>
    private List<(PooledMediaFrame Frame, byte Mask)>? _pendingVideoConfigurations;
    /// <summary>是否已经停止录制。</summary>
    private bool _stopped;
    /// <summary>
    /// 实例是否已释放的状态标记。
    /// </summary>
    private int _disposed;
    /// <summary>最近一次队列溢出警告的单调时刻。</summary>
    private long _lastQueueWarningTick;
    /// <summary>最近一次文件写入警告的单调时刻。</summary>
    private long _lastWriteWarningTick;
    /// <summary>停止录制时返回的路径、长度与时长。</summary>
    private (string FilePath, long FileSize, TimeSpan Duration) _stopResult;
    /// <summary>当前文件是否已经写入 FLV 文件头。</summary>
    private bool _flvHeaderWritten;
    /// <summary>录制时复用的 FLV 封装缓冲区。</summary>
    private byte[]? _flvBuffer;

    /// <summary>
    /// 初始化 StreamRecorder 的新实例。
    /// </summary>
    /// <param name="options">配置选项。</param>
    /// <param name="logger">日志记录器。</param>
    public StreamRecorder(StreamOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _flvMuxer = new FlvMuxer();
        _frameQueue = Channel.CreateBounded<PooledMediaFrame>(new BoundedChannelOptions(
            Math.Max(16, options.RecordingQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _acceptingFrames = true;
        _isRecording = true;
        _writerTask = ProcessQueueAsync();
    }

    /// <summary>是否正在录制。</summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// 收到媒体帧时调用。如果未开始录制则自动开始。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    public void OnFrame(PooledMediaFrame frame)
    {
        if (!_acceptingFrames || !frame.TryRetain()) return;

        lock (_queueLock)
        {
            if (!_acceptingFrames)
            {
                frame.Release();
                return;
            }

            byte parameterSetMask = 0;
            bool hasVideoVcl = false;
            bool hasVideoConfiguration = frame.Kind == MediaFrameKind.Video &&
                AnalyzeVideoConfiguration(frame, out parameterSetMask, out hasVideoVcl);
            bool isVideoConfigurationOnly = hasVideoConfiguration && !hasVideoVcl;
            bool isRecoveryKeyFrame = frame.Kind == MediaFrameKind.Video && frame.IsKeyFrame &&
                (frame.Codec is not (MediaCodec.H264 or MediaCodec.H265) || hasVideoVcl);

            if (_awaitingVideoKeyFrame && frame.Kind == MediaFrameKind.Video)
            {
                if (!isRecoveryKeyFrame)
                {
                    if (hasVideoConfiguration)
                        PreserveFrameConfiguration(frame, isVideoConfigurationOnly, parameterSetMask);
                    else
                        frame.Release();
                    return;
                }

                // 溢出边界丢弃的配置帧可能是编码器唯一发送的副本。恢复关键帧之前，先把保留的各类最新参数集交给写入器。
                if (!TryEnqueuePendingConfigurations())
                {
                    if (hasVideoConfiguration)
                        PreserveFrameConfiguration(frame, isVideoConfigurationOnly, parameterSetMask);
                    else
                        frame.Release();
                    return;
                }
            }

            if (_frameQueue.Writer.TryWrite(frame))
            {
                if (isRecoveryKeyFrame)
                    _awaitingVideoKeyFrame = false;
                return;
            }

            if (frame.Kind == MediaFrameKind.Video)
            {
                _awaitingVideoKeyFrame = true;
                if (hasVideoConfiguration)
                    PreserveFrameConfiguration(frame, isVideoConfigurationOnly, parameterSetMask);
                else
                    frame.Release();
            }
            else
            {
                frame.Release();
            }

            if (ShouldLog(ref _lastQueueWarningTick))
                _logger.LogWarning("录制写盘队列拥塞 [{StreamId}/{Channel}]，已丢帧并等待下一关键帧",
                    _options.StreamId, _options.Channel);
        }
    }

    /// <summary>
    /// 停止录制并返回录制信息。
    /// </summary>
    /// <returns>录制文件路径、文件字节数与录制时长。</returns>
    public (string FilePath, long FileSize, TimeSpan Duration) Stop()
    {
        lock (_lock)
        {
            if (_stopped)
                return _stopResult;

            lock (_queueLock)
            {
                _acceptingFrames = false;
                _frameQueue.Writer.TryComplete();
                ReleasePendingConfigurations();
            }

            try { _writerTask.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                if (ShouldLog(ref _lastWriteWarningTick))
                    _logger.LogWarning(ex, "录制后台任务停止异常 [{Path}]", _currentFilePath);
            }

            _isRecording = false;
            _recordingStarted.TrySetResult(null);

            if (_fileStream != null)
            {
                try
                {
                    _fileStream.Flush();
                    _fileStream.Dispose();
                }
                catch { }
                _fileStream = null;
            }

            var duration = _startTime != default ? DateTimeOffset.UtcNow - _startTime : TimeSpan.Zero;
            var path = _currentFilePath ?? "";
            var size = _fileSize;

            _logger.LogInformation("录制停止 [{Path}], 大小={Size}, 帧数={Frames}, 时长={Duration}",
                path, size, _frameCount, duration);

            _stopResult = (path, size, duration);
            _stopped = true;
            return _stopResult;
        }
    }

    /// <inheritdoc />
    /// <summary>释放当前实例拥有的资源。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
        if (_flvBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_flvBuffer);
            _flvBuffer = null;
        }
        _flvMuxer.Dispose();
    }

    /// <summary>
    /// 处理队列，并异步完成操作。
    /// </summary>
    /// <returns>处理队列，并异步完成操作完成时结束的异步任务。</returns>
    private async Task ProcessQueueAsync()
    {
        await foreach (var frame in _frameQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_writerAwaitingVideoKeyFrame && frame.Kind == MediaFrameKind.Video)
                {
                    bool configurationOnly = IsParameterSetOnly(frame, out _);
                    if (!frame.IsKeyFrame && !configurationOnly)
                        continue;
                    if (frame.IsKeyFrame && !configurationOnly)
                        _writerAwaitingVideoKeyFrame = false;
                }

                WriteFrame(frame);
            }
            catch (Exception ex)
            {
                if (frame.Kind == MediaFrameKind.Video)
                {
                    _writerAwaitingVideoKeyFrame = true;
                    lock (_queueLock)
                        _awaitingVideoKeyFrame = true;
                }

                if (ShouldLog(ref _lastWriteWarningTick))
                    _logger.LogWarning(ex, "录制写入失败 [{Path}]", _currentFilePath);
            }
            finally
            {
                frame.Release();
            }
        }
    }

    /// <summary>
    /// 写入帧。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    private void WriteFrame(PooledMediaFrame frame)
    {
        if (_options.RecordFormat == RecordFormat.FLV && !_flvHeaderWritten)
        {
            WriteFlvHeader();
            _flvHeaderWritten = true;
        }

        if (_options.RecordFormat == RecordFormat.FLV)
        {
            WriteFlvFrame(frame);
        }
        else
        {
            EnsureFileOpen();
            var span = frame.GetSpan();
            _fileStream!.Write(span);
            _fileSize += span.Length;
        }

        _frameCount++;
    }

    /// <summary>保留溢出恢复前仍需要的视频参数集。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    private void PreservePendingConfiguration(PooledMediaFrame frame, byte parameterSetMask)
    {
        _pendingVideoConfigurations ??= new List<(PooledMediaFrame, byte)>(3);
        _pendingVideoConfigurations.Add((frame, parameterSetMask));

        byte coveredParameterSets = 0;
        for (int index = _pendingVideoConfigurations.Count - 1; index >= 0; index--)
        {
            var configuration = _pendingVideoConfigurations[index];
            if ((configuration.Mask & ~coveredParameterSets) == 0)
            {
                configuration.Frame.Release();
                _pendingVideoConfigurations.RemoveAt(index);
            }
            else
            {
                coveredParameterSets |= configuration.Mask;
            }
        }
    }

    /// <summary>在录制起始阶段保留视频帧所需的编码参数集。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="configurationOnly">是否仅处理编码参数集。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    private void PreserveFrameConfiguration(PooledMediaFrame frame,
        bool configurationOnly, byte parameterSetMask)
    {
        if (configurationOnly)
        {
            PreservePendingConfiguration(frame, parameterSetMask);
            return;
        }

        try
        {
            var configuration = ExtractVideoConfiguration(frame, out byte extractedMask);
            if (configuration != null)
                PreservePendingConfiguration(configuration, extractedMask);
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>尝试在恢复关键帧前排入暂存的参数集。</summary>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private bool TryEnqueuePendingConfigurations()
    {
        if (_pendingVideoConfigurations == null)
            return true;

        while (_pendingVideoConfigurations.Count > 0)
        {
            var configuration = _pendingVideoConfigurations[0];
            if (!_frameQueue.Writer.TryWrite(configuration.Frame))
                return false;

            _pendingVideoConfigurations.RemoveAt(0);
        }

        _pendingVideoConfigurations = null;
        return true;
    }

    /// <summary>归还暂存参数集所持有的帧引用。</summary>
    private void ReleasePendingConfigurations()
    {
        if (_pendingVideoConfigurations == null)
            return;

        foreach (var configuration in _pendingVideoConfigurations)
            configuration.Frame.Release();
        _pendingVideoConfigurations = null;
    }

    /// <summary>
    /// 判断是否参数SetOnly。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    /// <returns>是否参数SetOnly的判定结果。</returns>
    private static bool IsParameterSetOnly(PooledMediaFrame frame, out byte parameterSetMask)
    {
        bool hasParameterSet = AnalyzeVideoConfiguration(
            frame, out parameterSetMask, out bool hasVcl);
        return hasParameterSet && !hasVcl;
    }

    /// <summary>识别帧中的视频参数集与非配置 NAL。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    /// <param name="hasVcl">是否包含视频编码层 NAL 单元。</param>
    /// <returns>识别帧中的视频参数集与非配置 NAL是否成立。</returns>
    private static bool AnalyzeVideoConfiguration(PooledMediaFrame frame,
        out byte parameterSetMask, out bool hasVcl)
    {
        parameterSetMask = 0;
        hasVcl = false;
        if (frame.Codec is not (MediaCodec.H264 or MediaCodec.H265))
            return false;

        var data = frame.GetSpan();
        bool hasParameterSet = false;
        bool foundStartCode = false;
        int offset = 0;
        while (TryFindNalHeader(data, offset, out int nalOffset))
        {
            foundStartCode = true;
            InspectNal(data[nalOffset], frame.Codec,
                ref hasParameterSet, ref hasVcl, ref parameterSetMask);
            offset = nalOffset + 1;
        }

        if (!foundStartCode && !data.IsEmpty)
        {
            InspectNal(data[0], frame.Codec,
                ref hasParameterSet, ref hasVcl, ref parameterSetMask);
        }

        return hasParameterSet;
    }

    /// <summary>提取帧内用于恢复解码的视频参数集。</summary>
    /// <param name="frame">媒体帧。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    /// <returns>提取帧内用于恢复解码的视频参数集得到的PooledMediaFrame?对象。</returns>
    private static PooledMediaFrame? ExtractVideoConfiguration(
        PooledMediaFrame frame, out byte parameterSetMask)
    {
        parameterSetMask = 0;
        var data = frame.GetSpan();
        int totalLength = 0;
        bool foundAnnexB = TryReadNextNalu(data, 0, out var nalu, out int nextOffset);

        if (!foundAnnexB)
        {
            if (!TryGetParameterSetMask(data, frame.Codec, out parameterSetMask))
                return null;
            totalLength = checked(data.Length + 4);
        }
        else
        {
            while (true)
            {
                if (TryGetParameterSetMask(nalu, frame.Codec, out byte mask))
                {
                    parameterSetMask |= mask;
                    totalLength = checked(totalLength + nalu.Length + 4);
                }

                if (!TryReadNextNalu(data, nextOffset, out nalu, out nextOffset))
                    break;
            }
        }

        if (totalLength == 0)
            return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = 0;
            if (!foundAnnexB)
            {
                WriteConfigurationNalu(data);
            }
            else
            {
                bool hasNalu = TryReadNextNalu(data, 0, out nalu, out nextOffset);
                while (hasNalu)
                {
                    if (TryGetParameterSetMask(nalu, frame.Codec, out _))
                        WriteConfigurationNalu(nalu);
                    hasNalu = TryReadNextNalu(data, nextOffset, out nalu, out nextOffset);
                }
            }

            var configuration = PooledMediaFrame.TakeOwnership(
                buffer, written, MediaFrameKind.Video, frame.Codec,
                isKeyFrame: false, timestampMs: frame.TimestampMs);
            buffer = null!;
            return configuration;

            // <summary>将一个编码参数集写入录制输出。</summary>
            // <param name="value">当前读取、写入或检查的值。</param>
            void WriteConfigurationNalu(ReadOnlySpan<byte> value)
            {
                buffer[written++] = 0;
                buffer[written++] = 0;
                buffer[written++] = 0;
                buffer[written++] = 1;
                value.CopyTo(buffer.AsSpan(written));
                written += value.Length;
            }
        }
        finally
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 尝试获取参数SetMask。
    /// </summary>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="mask">已发现参数集的类型掩码。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryGetParameterSetMask(
        ReadOnlySpan<byte> nalu, MediaCodec codec, out byte mask)
    {
        mask = 0;
        if (nalu.IsEmpty || codec is not (MediaCodec.H264 or MediaCodec.H265))
            return false;

        int type = codec == MediaCodec.H265 ? (nalu[0] >> 1) & 0x3F : nalu[0] & 0x1F;
        if (codec == MediaCodec.H265)
        {
            mask = type switch
            {
                32 => 0b001,
                33 => 0b010,
                34 => 0b100,
                _ => 0
            };
        }
        else
        {
            mask = type switch
            {
                7 => 0b010,
                8 => 0b100,
                _ => 0
            };
        }

        return mask != 0;
    }

    /// <summary>尝试取得下一个 NAL 单元及后续偏移量。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="nextOffset">下一段数据的起始偏移量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryReadNextNalu(ReadOnlySpan<byte> data, int offset,
        out ReadOnlySpan<byte> nalu, out int nextOffset)
    {
        nalu = default;
        nextOffset = data.Length;
        if (!TryFindNalHeader(data, offset, out int nalOffset))
            return false;

        int nalEnd = AnnexBStartCode.Find(data, nalOffset + 1, out _);
        if (nalEnd < 0) nalEnd = data.Length;

        if (nalEnd <= nalOffset)
            return false;

        nalu = data.Slice(nalOffset, nalEnd - nalOffset);
        nextOffset = nalEnd;
        return true;
    }

    /// <summary>尝试定位下一个有效 NAL 头。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalOffset">NAL 单元在输入数据中的偏移量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryFindNalHeader(ReadOnlySpan<byte> data, int offset, out int nalOffset)
    {
        int start = AnnexBStartCode.Find(data, offset, out int length);
        nalOffset = start < 0 ? 0 : start + length;
        return start >= 0 && nalOffset < data.Length;
    }

    /// <summary>检查 NAL 类型并更新配置集合掩码。</summary>
    /// <param name="header">协议头数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="hasParameterSet">是否发现视频参数集。</param>
    /// <param name="hasVcl">是否包含视频编码层 NAL 单元。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    private static void InspectNal(byte header, MediaCodec codec,
        ref bool hasParameterSet, ref bool hasVcl, ref byte parameterSetMask)
    {
        int type = codec == MediaCodec.H265 ? (header >> 1) & 0x3F : header & 0x1F;
        if (codec == MediaCodec.H265)
        {
            hasParameterSet |= type is 32 or 33 or 34;
            if (type == 32) parameterSetMask |= 0b001;
            else if (type == 33) parameterSetMask |= 0b010;
            else if (type == 34) parameterSetMask |= 0b100;
            hasVcl |= type <= 31;
        }
        else
        {
            hasParameterSet |= type is 7 or 8;
            if (type == 7) parameterSetMask |= 0b010;
            else if (type == 8) parameterSetMask |= 0b100;
            hasVcl |= type is >= 1 and <= 5;
        }
    }

    /// <summary>按最小时间间隔判断是否需要再次输出警告。</summary>
    /// <param name="lastTick">上次处理时的单调时钟计数。</param>
    /// <returns>按最小时间间隔判断是否需要再次输出警告是否成立。</returns>
    private static bool ShouldLog(ref long lastTick)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref lastTick);
        return (previous == 0 || now - previous >= WarningIntervalMs) &&
            Interlocked.CompareExchange(ref lastTick, now, previous) == previous;
    }

    /// <summary>在首次录制数据到达时创建输出文件。</summary>
    private void EnsureFileOpen()
    {
        if (_fileStream != null) return;

        var requestedPath = ResolveFilePath();
        var dir = Path.GetDirectoryName(requestedPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _fileStream = OpenUniqueFile(requestedPath, out var actualPath);
        _currentFilePath = actualPath;
        _startTime = DateTimeOffset.UtcNow;
        _isRecording = true;
        _fileSize = 0;
        _frameCount = 0;
        _flvHeaderWritten = false;

        _recordingStarted.TrySetResult(actualPath);
        _logger.LogInformation("开始录制 [{Path}], 格式={Format}", _currentFilePath, _options.RecordFormat);
    }

    /// <summary>以排他创建方式选择未被其他录制器占用的文件名。</summary>
    /// <param name="requestedPath">客户端请求的资源路径。</param>
    /// <param name="actualPath">实际媒体流路径。</param>
    /// <returns>以排他创建方式选择未被其他录制器占用的文件名得到的FileStream对象。</returns>
    private static FileStream OpenUniqueFile(string requestedPath, out string actualPath)
    {
        string? directory = Path.GetDirectoryName(requestedPath);
        string fileName = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);

        for (int suffix = 0; ; suffix++)
        {
            actualPath = suffix == 0
                ? requestedPath
                : Path.Combine(directory ?? string.Empty, $"{fileName}_{suffix}{extension}");

            try
            {
                return new FileStream(actualPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 65536);
            }
            catch (IOException) when (File.Exists(actualPath))
            {
                // 另一个录制器或同一时间桶中的快速重连可能已占用该文件名；CreateNew 以原子方式避免覆盖竞争。
            }
        }
    }

    /// <summary>
    /// 写入Flv头部。
    /// </summary>
    private void WriteFlvHeader()
    {
        EnsureFileOpen();
        int headerSize = FlvMuxer.FlvHeaderSizeTotal;
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            var span = headerBuf.AsSpan(0, headerSize);
            _flvMuxer.WriteFlvHeaderTo(span);
            _fileStream!.Write(span);
            _fileSize += headerSize;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    /// <summary>
    /// 写入Flv帧。
    /// </summary>
    /// <param name="frame">媒体帧。</param>
    private void WriteFlvFrame(PooledMediaFrame frame)
    {
        EnsureFileOpen();

        var required = _flvMuxer.GetMaxOutputSize(frame);
        if (required == 0) return;

        if (_flvBuffer == null || required > _flvBuffer.Length)
        {
            if (_flvBuffer != null)
                ArrayPool<byte>.Shared.Return(_flvBuffer);

            _flvBuffer = ArrayPool<byte>.Shared.Rent(required);
        }

        var written = _flvMuxer.MuxPooledFrameTo(frame, _flvBuffer);
        if (written > 0)
        {
            _fileStream!.Write(_flvBuffer.AsSpan(0, written));
            _fileSize += written;
        }
    }

    /// <summary>根据媒体流与录制选项生成输出文件路径。</summary>
    /// <returns>根据媒体流与录制选项生成输出文件路径对应的文本。</returns>
    private string ResolveFilePath()
    {
        var path = _options.RecordPath;
        var now = DateTimeOffset.Now;
        path = path.Replace("{StreamId}", _options.StreamId)
                   .Replace("{Channel}", _options.Channel)
                   .Replace("{Date}", now.ToString("yyyyMMdd"))
                   .Replace("{Time}", now.ToString("HHmmss"))
                   .Replace("{DateTime}", now.ToString("yyyyMMdd_HHmmss"));

        var ext = _options.RecordFormat == RecordFormat.FLV ? ".flv" : ".h264";
        if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            path += ext;

        return path;
    }
}

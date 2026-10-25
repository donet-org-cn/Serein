namespace Serein.Media.Server;

/// <summary>保存单个 PS/RTP 同步源的解包、重排与帧分发状态。</summary>
internal sealed class PsRtpStreamSession
{
    /// <summary>识别正常前向序列推进的最大跳跃距离。</summary>
    private const int MaxDropout = 3000;
    /// <summary>允许判断为旧包的反向序列窗口。</summary>
    private const int MaxMisorder = 100;
    /// <summary>
    /// 获取 媒体流标识。
    /// </summary>
    public string StreamId { get; }
    /// <summary>
    /// 获取 Channel标识。
    /// </summary>
    public string ChannelId { get; }
    /// <summary>
    /// 获取 Ssrc。
    /// </summary>
    public uint Ssrc { get; }
    /// <summary>
    /// 获取 Stats。
    /// </summary>
    public RtpStreamStats Stats { get; } = new();
    /// <summary>
    /// 获取或设置 Connected时间。
    /// </summary>
    public DateTimeOffset ConnectedTime { get; set; }
    /// <summary>
    /// 获取或设置 是否Connected。
    /// </summary>
    public bool IsConnected { get; set; }
    /// <summary>
    /// 获取 SyncRoot。
    /// </summary>
    public object SyncRoot { get; } = new();
    /// <summary>保护会话接收及解包状态的互斥锁。</summary>
    public object ReceiveSync { get; } = new();
    /// <summary>会话收包统计与重排状态。</summary>
    public PsRtpIngressState Ingress { get; } = new();
    /// <summary>等待串行通知订阅者的媒体帧队列。</summary>
    internal Relay.MediaStreamSubscription FrameQueue { get; }
    /// <summary>当前帧分发任务。</summary>
    internal Task? FrameDispatchTask;
    /// <summary>保护帧队列与分发任务的互斥锁。</summary>
    internal readonly object FrameDispatchSync = new();
    /// <summary>当前会话是否已停止接受新帧。</summary>
    internal bool FrameDispatchStopped;
    /// <summary>当前是否仍有媒体帧回调在执行。</summary>
    internal bool FrameCallbackActive;
    /// <summary>
    /// 获取 Reassembler。
    /// </summary>
    public PsReassembler Reassembler { get; }
    /// <summary>
    /// 获取或设置 Waiting对应的键帧。
    /// </summary>
    public bool WaitingForKeyFrame { get; set; }
    /// <summary>
    /// 获取或设置 是否释放状态。
    /// </summary>
    public bool IsDisposed { get; set; }
    /// <summary>
    /// 获取或设置 上次LossWarningTick。
    /// </summary>
    public long LastLossWarningTick { get; set; }

    /// <summary>最近确认的 RTP 序列号。</summary>
    private ushort _lastSeqNum;
    /// <summary>RTP 序列号跟踪是否已初始化。</summary>
    private bool _seqInitialized;
    /// <summary>编码器重启候选后期望的下一序列号。</summary>
    private ushort _restartCandidateNextSeq;
    /// <summary>是否已有等待后续包确认的重启候选。</summary>
    private bool _hasRestartCandidate;

    /// <summary>跟踪 RTP 序列号并计算本次新增丢包数量。</summary>
    /// <param name="seqNum">RTP 包序列号。</param>
    /// <returns>本次推进新增的丢包数；重复、迟到或尚未确认重启时为 -1，确认重启时为 1 以标记媒体不连续。</returns>
    public int TrackSequenceNumber(ushort seqNum)
    {
        Stats.LastPacketTime = Environment.TickCount64;
        Stats.PacketsReceived++;

        if (!_seqInitialized)
        {
            _lastSeqNum = seqNum;
            _seqInitialized = true;
            Stats.LastSequenceNumber = seqNum;
            return 0;
        }

        // 采用 RFC 3550 风格的序列校验。单次大跳变仅成为重启候选，随后连续的一包才重置游标，从而允许编码器重启且不把单个迟到包误认为新流。
        int forwardDelta = (ushort)(seqNum - _lastSeqNum);
        if (forwardDelta == 0)
        {
            Stats.PacketsOutOfOrder++;
            return -1;
        }

        if (forwardDelta < MaxDropout)
        {
            _hasRestartCandidate = false;
            _lastSeqNum = seqNum;
            Stats.LastSequenceNumber = seqNum;

            int lost = forwardDelta - 1;
            if (lost > 0)
                Stats.PacketsLost += lost;
            return lost;
        }

        if (forwardDelta <= ushort.MaxValue + 1 - MaxMisorder)
        {
            if (_hasRestartCandidate && seqNum == _restartCandidateNextSeq)
            {
                _hasRestartCandidate = false;
                _lastSeqNum = seqNum;
                Stats.LastSequenceNumber = seqNum;
                Stats.PacketsLost++;
                return 1; // 标记媒体不连续，使解码器等待新的 I 帧。
            }

            _hasRestartCandidate = true;
            _restartCandidateNextSeq = (ushort)(seqNum + 1);
            return -1;
        }

        Stats.PacketsOutOfOrder++;
        return -1;
    }

    /// <summary>
    /// 初始化 PsRtpStreamSession 的新实例。
    /// </summary>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channelId">通道标识。</param>
    /// <param name="ssrc">RTP 同步源标识。</param>
    /// <param name="maxPsBufferSize">PS 重组缓存的字节容量上限。</param>
    /// <param name="frameQueueCapacity">媒体帧回调队列容量。</param>
    public PsRtpStreamSession(string streamId, string channelId, uint ssrc, int maxPsBufferSize, int frameQueueCapacity = 32)
    {
        StreamId = streamId;
        ChannelId = channelId;
        Ssrc = ssrc;
        Reassembler = new PsReassembler(maxPsBufferSize);
        WaitingForKeyFrame = true;
        FrameQueue = new Relay.MediaStreamSubscription(_ => { }, frameQueueCapacity, dropStaleFrames: true);
    }
}

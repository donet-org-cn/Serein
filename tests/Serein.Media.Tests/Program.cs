using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serein.Media;
using Serein.Media.Adapters;
using Serein.Media.Protocol;
using Serein.Media.Relay;
using Serein.Media.Server;
using Serein.Rtsp.Mux;
using Serein.Rtsp.Relay;

return await VideoRegressionTests.RunAsync();

/// <summary>覆盖视频封装、分片恢复、参数集和录制生命周期的回归测试。</summary>
internal static class VideoRegressionTests
{
    /// <summary>运行当前控制台回归套件并报告失败数量。</summary>
    /// <returns>全部检查通过时为零，失败时为非零退出码。</returns>
    public static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Body)[]
        {
            ("FLV H.264 multi-NAL access unit is emitted as one video tag", FlvH264AggregatesAccessUnitAsync),
            ("FLV H.265 CRA is accepted as a random-access frame", FlvH265CraIsRandomAccessAsync),
            ("FLV HEVC hvcC fields are parsed from SPS RBSP", FlvHevcConfigurationRecordIsParsedAsync),
            ("FLV rejects short parameter sets without throwing", FlvShortConfigurationIsSafeAsync),
            ("FLV can prime a late subscriber from cached configuration", FlvCachedConfigurationPrimesLateSubscriberAsync),
            ("Live subscription overflow drops stale GOP and resumes on key frame", LiveSubscriptionOverflowRecoversAtKeyFrameAsync),
            ("Live subscription overflow preserves parameter sets before recovery", LiveSubscriptionOverflowPreservesConfigurationAsync),
            ("PS program stream map selects H.265", PsProgramStreamMapSelectsH265Async),
            ("PS codec discovered from PSM persists across parser feeds", PsCodecPersistsAcrossFeedsAsync),
            ("PS/RTP source binds a stream and receives loopback media", PsRtpSourceLoopbackAsync),
            ("PS parser rejects a truncated declared PES packet", PsTruncatedPesIsNotPublishedAsync),
            ("PS/RTP sequence tracking ignores late packets and handles wrap", PsSequenceTrackingIsStableAsync),
            ("PS/RTP async handlers retain pooled frame and isolate failures", PsAsyncFrameLifetimeIsSafeAsync),
            ("PS/RTP connected callback can unregister without publishing stale frames", PsConnectedCallbackCanUnregisterAsync),
            ("Raw RTP aggregates H.264 and H.265 NAL units by marker", RawRtpAggregatesAccessUnitsAsync),
            ("Raw RTP rejects premature FU markers and recovers at random access", RawRtpFuRecoveryIsSafeAsync),
            ("Recorder writes off the publish path and drains on disconnect", RecorderDrainsOnDisconnectAsync),
            ("Recorder is recreated after a stream reconnects", RecorderIsRecreatedAfterReconnectAsync),
            ("Recorder reconnect never overwrites the previous segment", RecorderReconnectUsesUniqueFileAsync),
            ("RTSP multicast subscribers retain independent frame references", IndustrialStabilityTests.RtspFrameOwnershipAsync),
            ("RTP slow callbacks are bounded and stop preserves in-flight ownership", IndustrialStabilityTests.RtpBackpressureAsync),
            ("RTP stop is bounded for blocked synchronous consumers and disconnect callbacks", IndustrialStabilityTests.RtpBlockedStopAsync),
            ("RTP dispatcher recovers from throwing consumers and loggers", IndustrialStabilityTests.RtpThrowingLoggerAsync),
            ("RTP callback queue overflow resumes video at a key frame", IndustrialStabilityTests.RtpQueueRecoveryAsync),
            ("RTSP rejects oversized headers, bodies and incomplete slow requests", IndustrialStabilityTests.RtspRequestLimitsAsync),
            ("RTSP consumes request bodies and preserves pipelined requests", IndustrialStabilityTests.RtspPipeliningAsync),
            ("RTSP same-instance reconnect uses the new session", IndustrialStabilityTests.RtspReconnectAsync),
            ("RTSP handshake timeout rolls back partially created receivers", IndustrialStabilityTests.RtspHandshakeRollbackAsync),
            ("PS/RTP slow callbacks are bounded and session replacement isolates queued frames", Round3MediaTests.PsCallbacksBoundedAsync),
            ("PS/RTP callback queue overflow resumes at a complete key frame", Round3MediaTests.PsQueueKeyFrameRecoveryAsync),
            ("PS/RTP synchronous blocked callback cannot block unregister", Round3MediaTests.PsBlockedCallbackUnregisterAsync),
            ("RTSP and TCP FLV churn removes completed tasks and stop releases listener", Round3MediaTests.ServerChurnAndStopAsync),
            ("Stream quality warning deduplication survives concurrent reset and frame updates", Round3MediaTests.QualityMonitorConcurrencyAsync),
            ("RTSP incomplete and malformed handshake responses fail with full rollback", Round4NetworkTests.RtspTruncatedHandshakeAsync),
            ("RTSP split interleaved packets preserve coalesced responses and first media frame", Round4NetworkTests.RtspIncrementalMediaAsync),
            ("RTSP UDP and TCP control faults clean up before callback-driven reconnect", Round4NetworkTests.RtspNetworkReconnectAsync),
            ("RTP canceled receiver releases socket and restart resets sequence window", Round4NetworkTests.RtpCancellationRestartAsync),
            ("RTSP and FLV callbacks can stop or dispose their server without self-wait", Round5ServerTests.CallbackStopAsync),
            ("FLV peer EOF and reset release idle hub subscriptions", Round5ServerTests.IdleFlvDisconnectAsync),
            ("RTSP TCP owner releases every described session and isolates other clients", Round5ServerTests.RtspConnectionOwnershipAsync),
            ("RTSP slow response reader cannot block server stop or other clients", Round5ServerTests.SlowRtspReaderStopAsync),
        };

        tests = tests.Concat(AudioTests.Cases).ToArray();
        var failures = new List<string>();
        foreach (var (name, body) in tests)
        {
            try
            {
                await body().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"Video regression tests passed ({tests.Length}).");
            return 0;
        }

        Console.Error.WriteLine($"Video regression tests failed ({failures.Count}/{tests.Length}):");
        foreach (string failure in failures)
            Console.Error.WriteLine($"- {failure}");
        return 1;
    }

    /// <summary>验证 H.264 访问单元中的多个 NAL 被合并到同一 FLV 标签。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task FlvH264AggregatesAccessUnitAsync()
    {
        byte[] sps = [0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9];
        byte[] pps = [0x68, 0xEE, 0x3C, 0x80];
        byte[] firstSlice = [0x65, 0x88, 0x84];
        byte[] secondSlice = [0x65, 0x99, 0x42, 0x10];
        byte[] accessUnit = AnnexB(sps, pps, firstSlice, secondSlice);

        using var muxer = new FlvMuxer();
        using var frame = PooledMediaFrame.Create(
            accessUnit, MediaFrameKind.Video, MediaCodec.H264, isKeyFrame: true, timestampMs: 1_000);
        byte[] output = new byte[muxer.GetMaxOutputSize(frame)];
        int written = muxer.MuxPooledFrameTo(frame, output);

        List<FlvTag> tags = ParseFlvTags(output.AsSpan(0, written));
        Assert.Equal(2, tags.Count, "sequence header plus one coded-frame tag");
        Assert.Equal((byte)0x00, tags[0].Data[1], "AVC sequence-header packet type");
        Assert.Equal((byte)0x01, tags[1].Data[1], "AVC coded-frame packet type");

        ReadOnlySpan<byte> payload = tags[1].Data.AsSpan(5);
        AssertLengthPrefixedNalus(payload, firstSlice, secondSlice);
        return Task.CompletedTask;
    }

    /// <summary>验证 H.265 CRA 图片被识别为随机接入帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task FlvH265CraIsRandomAccessAsync()
    {
        byte[] vps = [0x40, 0x01, 0x0C, 0x01];
        byte[] sps =
        [
            0x42, 0x01, 0x01, 0x60, 0x00, 0x00, 0x00, 0x90,
            0x00, 0x00, 0x00, 0x00, 0x78, 0xA0
        ];
        byte[] pps = [0x44, 0x01, 0xC0, 0x73];
        byte[] cra = [0x2A, 0x01, 0x88, 0x42]; // NAL 类型为 21，即 CRA 随机接入帧。

        using var muxer = new FlvMuxer();
        using var frame = PooledMediaFrame.Create(
            AnnexB(vps, sps, pps, cra),
            MediaFrameKind.Video,
            MediaCodec.H265,
            isKeyFrame: true,
            timestampMs: 9_000);
        byte[] output = new byte[muxer.GetMaxOutputSize(frame)];
        int written = muxer.MuxPooledFrameTo(frame, output);

        List<FlvTag> tags = ParseFlvTags(output.AsSpan(0, written));
        Assert.Equal(2, tags.Count, "HEVC sequence header plus CRA coded-frame tag");
        Assert.Equal((byte)0x90, tags[0].Data[0], "enhanced FLV sequence-start header");
        Assert.Equal((byte)0x91, tags[1].Data[0], "CRA must be emitted as a key coded frame");
        Assert.SequenceEqual("hvc1"u8, tags[1].Data.AsSpan(1, 4), "HEVC FourCC");
        AssertLengthPrefixedNalus(tags[1].Data.AsSpan(8), cra);
        return Task.CompletedTask;
    }

    /// <summary>验证 FLV 的 HEVC 配置记录反映 SPS 字段。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task FlvHevcConfigurationRecordIsParsedAsync()
    {
        byte[] vps = [0x40, 0x01, 0x0C, 0x01];
        byte[] sps = CreateHevcSps();
        byte[] pps = [0x44, 0x01, 0xC0, 0x73];
        byte[] annexBConfiguration = AnnexB(vps, sps, pps);

        using var muxer = new FlvMuxer();
        byte[] output = new byte[
            FlvMuxer.GetMaxVideoSequenceHeaderSize(MediaCodec.H265, annexBConfiguration.Length)];
        int written = muxer.WriteVideoSequenceHeaderTo(
            MediaCodec.H265, annexBConfiguration, output);

        List<FlvTag> tags = ParseFlvTags(output.AsSpan(0, written));
        Assert.Equal(1, tags.Count, "RTSP HEVC sequence-header tag count");
        ReadOnlySpan<byte> rtspHvcc = tags[0].Data.AsSpan(5);
        AssertHevcConfigurationRecord(rtspHvcc, vps, sps, pps);

        // 无效或截断的 RBSP 应使用安全默认值，不得越过 SPS 缓冲区边界。
        Assert.True(!HevcSpsParser.TryParse([0x42, 0x01, 0xFF], out _),
            "truncated HEVC SPS is rejected safely");
        return Task.CompletedTask;
    }

    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task FlvShortConfigurationIsSafeAsync()
    {
        byte[] malformedH264 = AnnexB([0x67], [0x68], [0x65, 0x01]);
        byte[] malformedH265 = AnnexB([0x40], [0x42, 0x01], [0x44], [0x2A, 0x01]);

        using var firstMuxer = new FlvMuxer();
        using var h264 = PooledMediaFrame.Create(
            malformedH264, MediaFrameKind.Video, MediaCodec.H264, isKeyFrame: true, timestampMs: 0);
        byte[] h264Output = new byte[firstMuxer.GetMaxOutputSize(h264)];
        int firstH264 = firstMuxer.MuxPooledFrameTo(h264, h264Output);

        firstMuxer.Reset();
        int secondH264 = firstMuxer.MuxPooledFrameTo(h264, h264Output);
        Assert.Equal(0, firstH264, "short AVC parameter sets must not produce malformed FLV");
        Assert.Equal(firstH264, secondH264, "short AVC configuration result must be deterministic");

        using var secondMuxer = new FlvMuxer();
        using var h265 = PooledMediaFrame.Create(
            malformedH265, MediaFrameKind.Video, MediaCodec.H265, isKeyFrame: true, timestampMs: 0);
        byte[] h265Output = new byte[secondMuxer.GetMaxOutputSize(h265)];
        int firstH265 = secondMuxer.MuxPooledFrameTo(h265, h265Output);

        secondMuxer.Reset();
        int secondH265 = secondMuxer.MuxPooledFrameTo(h265, h265Output);
        Assert.Equal(0, firstH265, "short HEVC parameter sets must not produce malformed FLV");
        Assert.Equal(firstH265, secondH265, "short HEVC configuration result must be deterministic");
        return Task.CompletedTask;
    }

    /// <summary>验证迟加入订阅者得到已缓存的完整视频配置。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task FlvCachedConfigurationPrimesLateSubscriberAsync()
    {
        const string streamId = "late-camera";
        const string channel = "1";
        byte[] sps = [0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9];
        byte[] pps = [0x68, 0xEE, 0x3C, 0x80];
        byte[] idr = [0x65, 0x88, 0x84];

        await using var hub = new MediaStreamHub();
        hub.RegisterStream(new StreamOptions
        {
            StreamId = streamId,
            Channel = channel,
            SubscriptionCapacity = 16
        });

        // 实际摄像机可能分开发送 SPS 和 PPS；流中心必须合并两份快照，使迟加入者获得完整配置。
        hub.PublishAndRelease(PooledMediaFrame.Create(
            AnnexB(sps), MediaFrameKind.Video, MediaCodec.H264,
            isKeyFrame: false, timestampMs: 980), streamId, channel);
        hub.PublishAndRelease(PooledMediaFrame.Create(
            AnnexB(pps), MediaFrameKind.Video, MediaCodec.H264,
            isKeyFrame: false, timestampMs: 990), streamId, channel);
        hub.PublishAndRelease(PooledMediaFrame.Create(
            AnnexB(idr), MediaFrameKind.Video, MediaCodec.H264,
            isKeyFrame: true, timestampMs: 1_000), streamId, channel);

        Assert.True(hub.TryGetVideoInfo(streamId, channel, out MediaCodec codec, out byte[]? cachedConfig),
            "hub should expose cached video information");
        Assert.Equal(MediaCodec.H264, codec, "cached codec");
        Assert.True(cachedConfig is { Length: > 0 }, "cached Annex-B parameter sets");

        using var muxer = new FlvMuxer();
        byte[] output = new byte[cachedConfig!.Length + 4_096];
        int sequenceBytes = muxer.WriteVideoSequenceHeaderTo(codec, cachedConfig, output);
        List<FlvTag> sequenceTags = ParseFlvTags(output.AsSpan(0, sequenceBytes));
        Assert.Equal(1, sequenceTags.Count, "cached configuration sequence tag count");
        Assert.Equal((byte)0x00, sequenceTags[0].Data[1], "cached AVC sequence-header packet type");

        using var lateIdr = PooledMediaFrame.Create(
            AnnexB(idr), MediaFrameKind.Video, MediaCodec.H264, isKeyFrame: true, timestampMs: 1_040);
        output = new byte[muxer.GetMaxOutputSize(lateIdr)];
        int mediaBytes = muxer.MuxPooledFrameTo(lateIdr, output);
        List<FlvTag> mediaTags = ParseFlvTags(output.AsSpan(0, mediaBytes));
        Assert.Equal(1, mediaTags.Count, "late subscriber coded-frame tag count");
        Assert.Equal((byte)0x01, mediaTags[0].Data[1], "late subscriber receives coded frame");
    }

    /// <summary>验证实时订阅溢出后等待关键帧再恢复输出。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task LiveSubscriptionOverflowRecoversAtKeyFrameAsync()
    {
        const string streamId = "slow-viewer";
        const string channel = "1";
        await using var hub = new MediaStreamHub();
        await using MediaStreamSubscription subscription = await hub.SubscribeLiveAsync(
            streamId, channel, "regression", capacity: 16).ConfigureAwait(false);

        for (int index = 0; index < 17; index++)
            PublishVideo(hub, streamId, channel, isKeyFrame: false, index);

        // 溢出丢弃一个 GOP 后，后续帧间预测帧不能重新填满队列。
        PublishVideo(hub, streamId, channel, isKeyFrame: false, 17);
        PublishVideo(hub, streamId, channel, isKeyFrame: true, 18);

        Assert.True(subscription.Reader.TryRead(out PooledMediaFrame? recovered),
            "live subscription should resume when a key frame arrives");
        try
        {
            Assert.True(recovered!.IsKeyFrame, "first frame after overflow must be a key frame");
            Assert.Equal(18L, recovered.TimestampMs, "stale frames must be removed");
        }
        finally
        {
            recovered?.Release();
        }
        Assert.True(!subscription.Reader.TryRead(out _), "only the recovery key frame remains queued");
    }

    /// <summary>验证实时订阅溢出后仍保留恢复需要的参数集。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task LiveSubscriptionOverflowPreservesConfigurationAsync()
    {
        const string streamId = "slow-viewer-config";
        const string channel = "1";
        await using var hub = new MediaStreamHub();
        await using MediaStreamSubscription subscription = await hub.SubscribeLiveAsync(
            streamId, channel, "regression", capacity: 16).ConfigureAwait(false);

        hub.PublishAndRelease(PooledMediaFrame.Create(
            AnnexB([0x67, 0x64, 0x00, 0x1F]), MediaFrameKind.Video,
            MediaCodec.H264, isKeyFrame: false, timestampMs: 0), streamId, channel);
        hub.PublishAndRelease(PooledMediaFrame.Create(
            AnnexB([0x68, 0xEE, 0x3C, 0x80]), MediaFrameKind.Video,
            MediaCodec.H264, isKeyFrame: false, timestampMs: 1), streamId, channel);
        for (int index = 2; index < 17; index++)
            PublishVideo(hub, streamId, channel, isKeyFrame: false, index);
        PublishVideo(hub, streamId, channel, isKeyFrame: true, 17);

        var recovered = new List<PooledMediaFrame>();
        while (subscription.Reader.TryRead(out var item))
            recovered.Add(item);

        try
        {
            Assert.Equal(3, recovered.Count, "configuration plus recovery frame count");
            Assert.True(!recovered[0].IsKeyFrame && !recovered[1].IsKeyFrame,
                "parameter sets precede recovery frame");
            Assert.True(recovered[2].IsKeyFrame, "recovery frame follows parameter sets");
        }
        finally
        {
            foreach (var item in recovered)
                item.Release();
        }
    }

    /// <summary>验证 PS 映射表决定 H.265 编码识别。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task PsRtpSourceLoopbackAsync()
    {
        await using var hub = new MediaStreamHub();
        await using var receiver = new PsRtpReceiver
        {
            ListenEndPoint = new(IPAddress.Loopback, 0), EnableTcp = false
        };
        await using var source = new PsRtpSource(receiver, hub);
        source.RegisterStream("input", "main", 1234);
        Assert.True(hub.GetStreamOptions("input", "main") is not null, "Source failed to register its stream in the hub.");
        await using var subscription = await hub.SubscribeLiveAsync("input", "main");
        await source.StartAsync();
        var socket = (Socket)typeof(PsRtpReceiver).GetField("_udpSocket", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(receiver)!;
        byte[] ps = CreatePsPacket(AnnexB([0x67, 0x64, 0, 0x1f], [0x68, 0xee, 0x3c, 0x80], [0x65, 0x88, 0x84]), 0x1b);
        using var sender = new UdpClient();
        byte[] packet = CreateRtpPacket(ps, 1234, 1, true);
        await sender.SendAsync(packet, (IPEndPoint)socket.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await subscription.Reader.ReadAsync(deadline.Token);
        try { Assert.Equal(MediaCodec.H264, frame.Codec, "PS/RTP video codec"); }
        finally { frame.Release(); }
        await source.StopAsync();
    }

    private static Task PsProgramStreamMapSelectsH265Async()
    {
        byte[] elementaryStream = AnnexB(
            [0x42, 0x01, 0x01, 0x60], // 有意构造与 H.264 类型 2 NAL 存在歧义的数据。
            [0x26, 0x01, 0x88]);
        byte[] packet = CreatePsPacket(elementaryStream, streamType: 0x24);

        List<PsPesFrame> frames = PsParser.ParsePsPacket(packet);
        try
        {
            Assert.Equal(1, frames.Count, "parsed video frame count");
            Assert.Equal(MediaCodec.H265, frames[0].Codec, "PSM stream type 0x24 codec");
            Assert.SequenceEqual(elementaryStream, frames[0].GetSpan(), "PES elementary stream");
        }
        finally
        {
            DisposeFrames(frames);
        }

        return Task.CompletedTask;
    }

    /// <summary>验证截断 PES 包不会发布不完整媒体帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task PsTruncatedPesIsNotPublishedAsync()
    {
        byte[] elementaryStream = AnnexB([0x65, 0x88, 0x84]);
        byte[] complete = CreatePsPacket(elementaryStream, streamType: 0x1B);
        List<PsPesFrame> completeFrames = PsParser.ParsePsPacket(complete);
        try
        {
            Assert.Equal(1, completeFrames.Count, "complete PES frame count");
        }
        finally
        {
            DisposeFrames(completeFrames);
        }

        // 保留声明的 PES 长度，但截去基本流负载尾部。
        byte[] truncated = complete[..^2];
        List<PsPesFrame> truncatedFrames = PsParser.ParsePsPacket(truncated);
        try
        {
            Assert.Equal(0, truncatedFrames.Count, "truncated PES must not be exposed to a decoder");
        }
        finally
        {
            DisposeFrames(truncatedFrames);
        }

        return Task.CompletedTask;
    }

    /// <summary>验证编码识别状态跨多次输入保持有效。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task PsCodecPersistsAcrossFeedsAsync()
    {
        byte[] elementaryStream = AnnexB([0x42, 0x01, 0x01, 0x60], [0x26, 0x01, 0x88]);
        byte[] packet = CreatePsPacket(elementaryStream, streamType: 0x24);
        const int packAndMapLength = 14 + 20;
        var codec = MediaCodec.Unknown;

        List<PsPesFrame> mapFrames = PsParser.ParsePsPacket(packet.AsSpan(0, packAndMapLength), ref codec);
        try
        {
            Assert.Equal(0, mapFrames.Count, "PSM-only feed frame count");
            Assert.Equal(MediaCodec.H265, codec, "codec learned from PSM");
        }
        finally
        {
            DisposeFrames(mapFrames);
        }

        List<PsPesFrame> pesFrames = PsParser.ParsePsPacket(packet.AsSpan(packAndMapLength), ref codec);
        try
        {
            Assert.Equal(1, pesFrames.Count, "following PES feed frame count");
            Assert.Equal(MediaCodec.H265, pesFrames[0].Codec, "persisted codec for following PES feed");
        }
        finally
        {
            DisposeFrames(pesFrames);
        }

        return Task.CompletedTask;
    }

    /// <summary>验证 PS/RTP RTP 序列跳变、迟到与重启判断。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static Task PsSequenceTrackingIsStableAsync()
    {
        var session = new PsRtpStreamSession("device", "channel", 1, 64 * 1024);
        Assert.Equal(0, session.TrackSequenceNumber(100), "initial sequence");
        Assert.Equal(1, session.TrackSequenceNumber(102), "one forward packet loss");
        Assert.Equal(-1, session.TrackSequenceNumber(101), "late packet is rejected");
        Assert.Equal(0, session.TrackSequenceNumber(103), "late packet must not move sequence cursor");

        var wrapping = new PsRtpStreamSession("device", "channel", 2, 64 * 1024);
        Assert.Equal(0, wrapping.TrackSequenceNumber(ushort.MaxValue), "wrap initial sequence");
        Assert.Equal(0, wrapping.TrackSequenceNumber(0), "65535 to 0 wrap");

        var restarted = new PsRtpStreamSession("device", "channel", 3, 64 * 1024);
        Assert.Equal(0, restarted.TrackSequenceNumber(10_000), "restart initial sequence");
        Assert.Equal(-1, restarted.TrackSequenceNumber(0), "first large jump starts restart probation");
        Assert.Equal(1, restarted.TrackSequenceNumber(1), "second sequential packet confirms restart");
        Assert.Equal(0, restarted.TrackSequenceNumber(2), "stream continues after restart");

        var reordered = new PsRtpStreamSession("device", "channel", 4, 64 * 1024);
        Assert.Equal(0, reordered.TrackSequenceNumber(100), "reorder initial sequence");
        Assert.Equal(-1, reordered.TrackSequenceNumber(50), "old packet stays rejected");
        Assert.Equal(0, reordered.TrackSequenceNumber(101), "old packet does not trigger restart");
        Assert.Equal(1L, session.Stats.PacketsLost, "loss is not counted twice");
        return Task.CompletedTask;
    }

    /// <summary>验证异步帧回调结束前借用内存保持有效。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task PsAsyncFrameLifetimeIsSafeAsync()
    {
        const uint ssrc = 0x10203040;
        byte[] elementaryStream = AnnexB([0x65, 0x88, 0x84, 0x21]);
        byte[] psPacket = CreatePsPacket(elementaryStream, streamType: 0x1B);
        byte[] rtpPacket = CreateRtpPacket(psPacket, ssrc, sequence: 1, marker: true);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new PsRtpReceiver();
        server.RegisterStream("device", "channel", ssrc);
        server.FrameReceivedAsync += static (_, _, _, _) =>
            throw new InvalidOperationException("intentional regression-test handler failure");
        server.FrameReceivedAsync += async (_, _, _, frame) =>
        {
            await Task.Yield();
            received.TrySetResult(frame.GetMemory().ToArray());
        };

        server.ProcessRtpPacket(rtpPacket, new IPEndPoint(IPAddress.Loopback, 6000));
        byte[] snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.SequenceEqual(elementaryStream, snapshot,
            "pooled frame remains valid through an asynchronous handler");
        server.UnregisterStream(ssrc);
    }

    /// <summary>验证连接回调中注销同一流不会死锁。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task PsConnectedCallbackCanUnregisterAsync()
    {
        const uint ssrc = 0x55667788;
        byte[] elementaryStream = AnnexB([0x65, 0x88, 0x84, 0x21]);
        byte[] packet = CreateRtpPacket(
            CreatePsPacket(elementaryStream, streamType: 0x1B),
            ssrc, sequence: 1, marker: true);
        int frameCount = 0;

        await using var server = new PsRtpReceiver();
        server.RegisterStream("device", "channel", ssrc);
        server.StreamConnectedAsync += (sender, _, _) =>
        {
            sender.UnregisterStream(ssrc);
            return ValueTask.CompletedTask;
        };
        server.FrameReceivedAsync += (_, _, _, frame) =>
        {
            Interlocked.Increment(ref frameCount);
            frame.Release();
            return ValueTask.CompletedTask;
        };

        server.ProcessRtpPacket(packet, new IPEndPoint(IPAddress.Loopback, 6001));
        Assert.Equal(0, frameCount,
            "a session unregistered by the connected callback cannot publish parsed frames");
    }

    /// <summary>验证裸 RTP NAL 按时间戳与结束标记组成访问单元。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task RawRtpAggregatesAccessUnitsAsync()
    {
        byte[] h264Sps = [0x67, 0x64, 0x00, 0x1F];
        byte[] h264Pps = [0x68, 0xEE, 0x3C, 0x80];
        byte[] h264Slice1 = [0x65, 0x88, 0x84];
        byte[] h264Slice2 = [0x65, 0x99, 0x42];
        var h264Frames = new List<(byte[] Data, bool IsKeyFrame)>();

        await using (var client = new RtpClient
        {
            UsePsDemux = false,
            RawFrameKind = MediaFrameKind.Video,
            RawCodec = MediaCodec.H264,
            MaxRawFragmentSize = 4,
            MaxRawAccessUnitSize = 1_024
        })
        {
            client.FrameReceivedAsync += (_, frame) =>
            {
                h264Frames.Add((frame.GetMemory().ToArray(), frame.IsKeyFrame));
                frame.Release();
                return ValueTask.CompletedTask;
            };

            client.PushRtpPacket(CreateRtpPacket(h264Sps, 1, 1, marker: false, timestamp: 90_000));
            client.PushRtpPacket(CreateRtpPacket(h264Pps, 1, 2, marker: false, timestamp: 90_000));
            client.PushRtpPacket(CreateRtpPacket(h264Slice1, 1, 3, marker: false, timestamp: 90_000));
            client.PushRtpPacket(CreateRtpPacket(h264Slice2, 1, 4, marker: true, timestamp: 90_000));
        }

        Assert.Equal(1, h264Frames.Count, "one H.264 RTP timestamp produces one access unit");
        Assert.True(h264Frames[0].IsKeyFrame, "aggregated H.264 IDR access unit is key");
        Assert.SequenceEqual(AnnexB(h264Sps, h264Pps, h264Slice1, h264Slice2), h264Frames[0].Data,
            "H.264 access unit preserves NAL order");

        byte[] h265Vps = [0x40, 0x01, 0x0C];
        byte[] h265Sps = [0x42, 0x01, 0x01, 0x60];
        byte[] h265Pps = [0x44, 0x01, 0xC0];
        byte[] h265Slice1 = [0x2A, 0x01, 0x88];
        byte[] h265Slice2 = [0x2A, 0x01, 0x99];
        var h265Frames = new List<(byte[] Data, bool IsKeyFrame)>();

        await using (var client = new RtpClient
        {
            UsePsDemux = false,
            RawFrameKind = MediaFrameKind.Video,
            RawCodec = MediaCodec.H265
        })
        {
            client.FrameReceivedAsync += (_, frame) =>
            {
                h265Frames.Add((frame.GetMemory().ToArray(), frame.IsKeyFrame));
                frame.Release();
                return ValueTask.CompletedTask;
            };

            client.PushRtpPacket(CreateRtpPacket(h265Vps, 2, 10, marker: false, timestamp: 180_000));
            client.PushRtpPacket(CreateRtpPacket(h265Sps, 2, 11, marker: false, timestamp: 180_000));
            client.PushRtpPacket(CreateRtpPacket(h265Pps, 2, 12, marker: false, timestamp: 180_000));
            client.PushRtpPacket(CreateRtpPacket(h265Slice1, 2, 13, marker: false, timestamp: 180_000));
            client.PushRtpPacket(CreateRtpPacket(h265Slice2, 2, 14, marker: true, timestamp: 180_000));
        }

        Assert.Equal(1, h265Frames.Count, "one H.265 RTP timestamp produces one access unit");
        Assert.True(h265Frames[0].IsKeyFrame, "aggregated H.265 CRA access unit is key");
        Assert.SequenceEqual(AnnexB(h265Vps, h265Sps, h265Pps, h265Slice1, h265Slice2), h265Frames[0].Data,
            "H.265 access unit preserves NAL order");
    }

    /// <summary>验证分片丢失、越界和不完整结束不会发布损坏帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task RawRtpFuRecoveryIsSafeAsync()
    {
        var h264Frames = new List<byte[]>();
        await using (var client = new RtpClient
        {
            UsePsDemux = false,
            RawFrameKind = MediaFrameKind.Video,
            RawCodec = MediaCodec.H264,
            MaxRawFragmentSize = 8
        })
        {
            client.FrameReceivedAsync += (_, frame) =>
            {
                h264Frames.Add(frame.GetMemory().ToArray());
                frame.Release();
                return ValueTask.CompletedTask;
            };

            // FU-A 首片带 Marker=1、E=0，并超过重组容量上限；它绝不能作为截断 NAL 被发布。
            client.PushRtpPacket(CreateRtpPacket(
                [0x7C, 0x85, 0x10, 0x11, 0x12, 0x13],
                3, 20, marker: true, timestamp: 270_000));
            client.PushRtpPacket(CreateRtpPacket(
                [0x41, 0x01], 3, 21, marker: true, timestamp: 273_000));
            byte[] idr = [0x65, 0x20, 0x21];
            client.PushRtpPacket(CreateRtpPacket(
                idr, 3, 22, marker: true, timestamp: 276_000));

            Assert.Equal(1, h264Frames.Count,
                "malformed/oversized H.264 FU and following P frame stay gated");
            Assert.SequenceEqual(AnnexB(idr), h264Frames[0],
                "H.264 output resumes with the next complete IDR");
        }

        var h265Frames = new List<byte[]>();
        await using (var client = new RtpClient
        {
            UsePsDemux = false,
            RawFrameKind = MediaFrameKind.Video,
            RawCodec = MediaCodec.H265
        })
        {
            client.FrameReceivedAsync += (_, frame) =>
            {
                h265Frames.Add(frame.GetMemory().ToArray());
                frame.Release();
                return ValueTask.CompletedTask;
            };

            client.PushRtpPacket(CreateRtpPacket(
                [0x62, 0x01, 0x95, 0x10],
                4, 30, marker: true, timestamp: 360_000));
            client.PushRtpPacket(CreateRtpPacket(
                [0x02, 0x01, 0x11], 4, 31, marker: true, timestamp: 363_000));
            byte[] cra = [0x2A, 0x01, 0x22];
            client.PushRtpPacket(CreateRtpPacket(
                cra, 4, 32, marker: true, timestamp: 366_000));

            Assert.Equal(1, h265Frames.Count,
                "premature H.265 FU marker and following inter frame stay gated");
            Assert.SequenceEqual(AnnexB(cra), h265Frames[0],
                "H.265 output resumes with the next complete CRA");
        }
    }

    /// <summary>验证断连停止会排空已接受的录制帧。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task RecorderDrainsOnDisconnectAsync()
    {
        const string streamId = "recording-camera";
        const string channel = "1";
        byte[] accessUnit = AnnexB([0x65, 0x88, 0x84, 0x21]);
        string directory = Path.Combine(Path.GetTempPath(), "Serein.Media.Tests");
        string path = Path.Combine(directory, $"recording-{Guid.NewGuid():N}.h264");

        try
        {
            await using var hub = new MediaStreamHub();
            hub.RecorderFactory = static (options, logger) => new StreamRecorder(options, logger);
            hub.RegisterStream(new StreamOptions
            {
                StreamId = streamId,
                Channel = channel,
                RecordToFile = true,
                RecordFormat = RecordFormat.RawES,
                RecordPath = path,
                RecordingQueueCapacity = 16
            });

            hub.PublishAndRelease(PooledMediaFrame.Create(
                accessUnit, MediaFrameKind.Video, MediaCodec.H264,
                isKeyFrame: true, timestampMs: 1_000), streamId, channel);
            await hub.MarkStreamDisconnectedAsync(streamId, channel, "regression").ConfigureAwait(false);

            Assert.True(File.Exists(path), "background recorder creates the configured raw ES file");
            byte[] recordedBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            Assert.SequenceEqual(accessUnit, recordedBytes,
                "disconnect drains all queued frames before closing the recording");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    /// <summary>验证同一流重连后建立新的录制器。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task RecorderIsRecreatedAfterReconnectAsync()
    {
        const string streamId = "reconnecting-camera";
        const string channel = "1";
        var recorders = new List<TrackingRecorder>();

        await using var hub = new MediaStreamHub();
        hub.RecorderFactory = (_, _) =>
        {
            var recorder = new TrackingRecorder();
            recorders.Add(recorder);
            return recorder;
        };
        hub.RegisterStream(new StreamOptions
        {
            StreamId = streamId,
            Channel = channel,
            RecordToFile = true
        });

        PublishVideo(hub, streamId, channel, isKeyFrame: true, timestamp: 1);
        await hub.MarkStreamDisconnectedAsync(streamId, channel, "first disconnect").ConfigureAwait(false);

        Assert.Equal(1, recorders.Count, "first connection recorder count");
        Assert.Equal(1, recorders[0].FrameCount, "first recorder receives its frame");
        Assert.True(recorders[0].Stopped && recorders[0].Disposed,
            "disconnected recorder is stopped and disposed");

        hub.MarkStreamConnected(streamId, channel);
        PublishVideo(hub, streamId, channel, isKeyFrame: true, timestamp: 2);

        Assert.Equal(2, recorders.Count, "reconnect creates a fresh recorder");
        Assert.Equal(1, recorders[1].FrameCount, "replacement recorder receives new frames");
    }

    /// <summary>验证快速重连的录制文件不会覆盖旧文件。</summary>
    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    private static async Task RecorderReconnectUsesUniqueFileAsync()
    {
        const string streamId = "unique-recording-camera";
        const string channel = "1";
        byte[] firstAccessUnit = AnnexB([0x65, 0x10, 0x20]);
        byte[] secondAccessUnit = AnnexB([0x65, 0x30, 0x40, 0x50]);
        string directory = Path.Combine(Path.GetTempPath(), $"Serein.Media.Tests-{Guid.NewGuid():N}");
        string requestedPath = Path.Combine(directory, "clip.h264");
        string secondPath = Path.Combine(directory, "clip_1.h264");
        var stoppedPaths = new List<string>();

        try
        {
            await using var hub = new MediaStreamHub();
            hub.RecorderFactory = static (options, logger) => new StreamRecorder(options, logger);
            hub.RecordStoppedAsync += (_, eventArgs) =>
            {
                stoppedPaths.Add(eventArgs.FilePath);
                return ValueTask.CompletedTask;
            };
            hub.RegisterStream(new StreamOptions
            {
                StreamId = streamId,
                Channel = channel,
                RecordToFile = true,
                RecordFormat = RecordFormat.RawES,
                RecordPath = requestedPath,
                RecordingQueueCapacity = 16
            });

            hub.PublishAndRelease(PooledMediaFrame.Create(
                firstAccessUnit, MediaFrameKind.Video, MediaCodec.H264,
                isKeyFrame: true, timestampMs: 1), streamId, channel);
            await hub.MarkStreamDisconnectedAsync(streamId, channel, "first segment").ConfigureAwait(false);

            hub.MarkStreamConnected(streamId, channel);
            hub.PublishAndRelease(PooledMediaFrame.Create(
                secondAccessUnit, MediaFrameKind.Video, MediaCodec.H264,
                isKeyFrame: true, timestampMs: 2), streamId, channel);
            await hub.MarkStreamDisconnectedAsync(streamId, channel, "second segment").ConfigureAwait(false);

            Assert.Equal(2, stoppedPaths.Count, "two reconnect recording segments stop independently");
            Assert.Equal(requestedPath, stoppedPaths[0], "first segment keeps the requested path");
            Assert.Equal(secondPath, stoppedPaths[1], "second segment uses a collision-free suffix");
            byte[] firstRecordedBytes = await File.ReadAllBytesAsync(requestedPath).ConfigureAwait(false);
            byte[] secondRecordedBytes = await File.ReadAllBytesAsync(secondPath).ConfigureAwait(false);
            Assert.SequenceEqual(firstAccessUnit, firstRecordedBytes,
                "second recorder never truncates the first segment");
            Assert.SequenceEqual(secondAccessUnit, secondRecordedBytes,
                "second segment is written to the unique path");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                    File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    /// <returns>该回归操作完成时结束的任务；断言失败通过异常传播。</returns>
    internal static byte[] AnnexB(params byte[][] nalus)
    {
        int length = nalus.Sum(nalu => 4 + nalu.Length);
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] nalu in nalus)
        {
            result[offset + 3] = 0x01;
            offset += 4;
            nalu.CopyTo(result, offset);
            offset += nalu.Length;
        }
        return result;
    }

    /// <summary>
    /// 创建Ps数据包。
    /// </summary>
    /// <param name="elementaryStream">基本媒体流数据。</param>
    /// <param name="streamType">媒体流类型。</param>
    /// <returns>创建Ps数据包后的字节数据。</returns>
    internal static byte[] CreatePsPacket(byte[] elementaryStream, byte streamType)
    {
        byte[] packHeader =
        [
            0x00, 0x00, 0x01, 0xBA,
            0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x89, 0xC3, 0xF8, 0x00
        ];
        byte[] programStreamMap =
        [
            0x00, 0x00, 0x01, 0xBC, 0x00, 0x0E,
            0xE0, 0xFF,
            0x00, 0x00,
            0x00, 0x04,
            streamType, 0xE0, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];

        int pesPacketLength = checked(3 + 5 + elementaryStream.Length);
        var pes = new byte[6 + pesPacketLength];
        pes[2] = 0x01;
        pes[3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(pes.AsSpan(4), checked((ushort)pesPacketLength));
        pes[6] = 0x80;
        pes[7] = 0x80;
        pes[8] = 0x05;
        // PTS 为零，包含所有必需标记位。
        pes[9] = 0x21;
        pes[10] = 0x00;
        pes[11] = 0x01;
        pes[12] = 0x00;
        pes[13] = 0x01;
        elementaryStream.CopyTo(pes, 14);

        return [.. packHeader, .. programStreamMap, .. pes];
    }

    /// <summary>创建标准 RTP 媒体包。</summary>
    /// <param name="payload">媒体负载。</param>
    /// <param name="ssrc">同步源标识。</param>
    /// <param name="sequence">RTP 包序列号。</param>
    /// <param name="timestamp">媒体时间戳。</param>
    /// <param name="marker">帧结束标记。</param>
    internal static byte[] CreateRtpPacket(
        byte[] payload,
        uint ssrc,
        ushort sequence,
        bool marker,
        uint timestamp = 90_000)
    {
        var packet = new byte[StandardRtpHeader.HeaderLength + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(96 | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        payload.CopyTo(packet, StandardRtpHeader.HeaderLength);
        return packet;
    }

    /// <summary>向测试流中心发布指定时间戳的视频帧。</summary>
    /// <param name="hub">通用媒体流中转枢纽。</param>
    /// <param name="streamId">媒体流标识。</param>
    /// <param name="channel">媒体通道。</param>
    /// <param name="isKeyFrame">当前帧是否为关键帧。</param>
    /// <param name="timestamp">媒体时间戳。</param>
    private static void PublishVideo(
        MediaStreamHub hub,
        string streamId,
        string channel,
        bool isKeyFrame,
        int timestamp)
    {
        var frame = PooledMediaFrame.Create(
            isKeyFrame ? [0x00, 0x00, 0x01, 0x65] : [0x00, 0x00, 0x01, 0x41],
            MediaFrameKind.Video,
            MediaCodec.H264,
            isKeyFrame,
            timestamp);
        hub.PublishAndRelease(frame, streamId, channel);
    }

    /// <summary>
    /// 解析FlvTags。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>解析FlvTags得到的条目集合。</returns>
    private static List<FlvTag> ParseFlvTags(ReadOnlySpan<byte> data)
    {
        var result = new List<FlvTag>();
        int offset = 0;
        while (offset < data.Length)
        {
            Assert.True(data.Length - offset >= 15, "complete FLV tag envelope");
            byte type = data[offset];
            int dataLength = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            int tagLength = checked(11 + dataLength + 4);
            Assert.True(offset + tagLength <= data.Length, "FLV tag data length stays within output");
            int previousTagSize = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 11 + dataLength, 4));
            Assert.Equal(11 + dataLength, previousTagSize, "PreviousTagSize");
            result.Add(new FlvTag(type, data.Slice(offset + 11, dataLength).ToArray()));
            offset += tagLength;
        }
        return result;
    }

    /// <summary>检查封装数据中的长度前缀与 NAL 内容。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="expected">期望值。</param>
    private static void AssertLengthPrefixedNalus(ReadOnlySpan<byte> data, params byte[][] expected)
    {
        int offset = 0;
        foreach (byte[] nalu in expected)
        {
            Assert.True(data.Length - offset >= 4, "NAL length prefix");
            int length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            Assert.Equal(nalu.Length, length, "NAL length");
            Assert.True(data.Length - offset >= length, "NAL payload bounds");
            Assert.SequenceEqual(nalu, data.Slice(offset, length), "NAL payload");
            offset += length;
        }
        Assert.Equal(data.Length, offset, "no extra NAL payload bytes");
    }

    /// <summary>检查 HEVC 配置记录中的固定字段及参数集。</summary>
    /// <param name="configuration">解析或写入的媒体配置。</param>
    /// <param name="expectedVps">期望的视频参数集。</param>
    /// <param name="expectedSps">期望的序列参数集。</param>
    /// <param name="expectedPps">期望的图像参数集。</param>
    private static void AssertHevcConfigurationRecord(
        ReadOnlySpan<byte> configuration,
        byte[] expectedVps,
        byte[] expectedSps,
        byte[] expectedPps)
    {
        Assert.True(configuration.Length >= 23, "complete HEVCDecoderConfigurationRecord");
        Assert.Equal((byte)1, configuration[0], "hvcC configurationVersion");
        Assert.Equal((byte)0xC5, configuration[1],
            "general_profile_space/tier_flag/profile_idc");
        Assert.Equal(0x00000102u,
            BinaryPrimitives.ReadUInt32BigEndian(configuration[2..]),
            "general_profile_compatibility_flags");
        Assert.SequenceEqual([0x00, 0x00, 0x03, 0x44, 0x55, 0x66],
            configuration.Slice(6, 6), "general_constraint_indicator_flags");
        Assert.Equal((byte)0x78, configuration[12], "general_level_idc");
        Assert.Equal((ushort)0xF000,
            BinaryPrimitives.ReadUInt16BigEndian(configuration[13..]),
            "min_spatial_segmentation_idc with reserved bits");
        Assert.Equal((byte)0xFC, configuration[15], "parallelismType");
        Assert.Equal((byte)0xFF, configuration[16], "chroma_format_idc");
        Assert.Equal((byte)0xFA, configuration[17], "bit_depth_luma_minus8");
        Assert.Equal((byte)0xFA, configuration[18], "bit_depth_chroma_minus8");
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(configuration[19..]),
            "avgFrameRate");
        Assert.Equal((byte)0x1F, configuration[21],
            "numTemporalLayers/temporalIdNested/lengthSizeMinusOne");
        Assert.Equal((byte)3, configuration[22], "numOfArrays");

        int offset = 23;
        AssertHevcArray(configuration, ref offset, 0xA0, expectedVps);
        AssertHevcArray(configuration, ref offset, 0xA1, expectedSps);
        AssertHevcArray(configuration, ref offset, 0xA2, expectedPps);
        Assert.Equal(configuration.Length, offset, "no trailing hvcC bytes");
    }

    /// <summary>检查 HEVC 配置中的指定参数集数组。</summary>
    /// <param name="configuration">解析或写入的媒体配置。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="expectedHeader">期望的封装头。</param>
    /// <param name="expectedNalu">期望的 NAL 单元。</param>
    private static void AssertHevcArray(
        ReadOnlySpan<byte> configuration,
        ref int offset,
        byte expectedHeader,
        byte[] expectedNalu)
    {
        Assert.True(configuration.Length - offset >= 5, "complete hvcC array header");
        Assert.Equal(expectedHeader, configuration[offset++],
            "array_completeness and NAL unit type");
        Assert.Equal((ushort)1,
            BinaryPrimitives.ReadUInt16BigEndian(configuration[offset..]),
            "numNalus");
        offset += 2;
        int length = BinaryPrimitives.ReadUInt16BigEndian(configuration[offset..]);
        offset += 2;
        Assert.Equal(expectedNalu.Length, length, "hvcC NAL length");
        Assert.True(configuration.Length - offset >= length, "hvcC NAL bounds");
        Assert.SequenceEqual(expectedNalu, configuration.Slice(offset, length),
            "hvcC keeps the original NAL EBSP bytes");
        offset += length;
    }

    /// <summary>
    /// 创建HevcSps。
    /// </summary>
    /// <returns>创建HevcSps后的字节数据。</returns>
    private static byte[] CreateHevcSps()
    {
        var writer = new TestBitWriter();
        writer.WriteBits(0, 4);             // SPS 引用的视频参数集编号。
        writer.WriteBits(2, 3);             // 最大子层数减一。
        writer.WriteBits(1, 1);             // 时间层嵌套标志。
        writer.WriteBits(0xC5, 8);          // 配置档空间、层级和配置档编号。
        writer.WriteBits(0x00000102, 32);    // 配置档兼容标志，测试数据需要插入防竞争字节。
        writer.WriteBits(0x000003445566, 48);// 约束标志，测试数据需要插入防竞争字节。
        writer.WriteBits(0x78, 8);          // 编码级别编号。
        writer.WriteBits(0, 4);             // 两个子层各自的配置档与级别存在标志。
        writer.WriteBits(0, 12);            // 子层 2～7 对应的两位保留零字段。
        writer.WriteUnsignedExpGolomb(0);    // 序列参数集编号。
        writer.WriteUnsignedExpGolomb(3);    // 色度格式编号。
        writer.WriteBits(0, 1);              // 独立颜色平面标志。
        writer.WriteUnsignedExpGolomb(1920); // 图像宽度。
        writer.WriteUnsignedExpGolomb(1080); // 图像高度。
        writer.WriteBits(0, 1);              // 一致性裁剪窗口存在标志。
        writer.WriteUnsignedExpGolomb(2);    // 亮度位深减八。
        writer.WriteUnsignedExpGolomb(2);    // 色度位深减八。

        byte[] rbsp = writer.ToRbsp();
        var ebsp = new List<byte>(rbsp.Length + 4);
        int consecutiveZeros = 0;
        foreach (byte value in rbsp)
        {
            if (consecutiveZeros >= 2 && value <= 0x03)
            {
                ebsp.Add(0x03);
                consecutiveZeros = 0;
            }

            ebsp.Add(value);
            consecutiveZeros = value == 0 ? consecutiveZeros + 1 : 0;
        }

        return [0x42, 0x01, .. ebsp];
    }

    /// <summary>按位构造 HEVC 参数集测试数据。</summary>
    private sealed class TestBitWriter
    {
        /// <summary>字节数据或字节数量，具体形式由参数类型决定。</summary>
        private readonly List<byte> _bytes = [];
        /// <summary>最后一个字节中已经写入的位数。</summary>
        private int _bitsInLastByte;

        /// <summary>
        /// 写入Bits。
        /// </summary>
        /// <param name="value">当前读取、写入或检查的值。</param>
        /// <param name="count">数据长度或数量。</param>
        public void WriteBits(ulong value, int count)
        {
            for (int shift = count - 1; shift >= 0; shift--)
            {
                if (_bitsInLastByte == 0)
                    _bytes.Add(0);

                if (((value >> shift) & 1) != 0)
                    _bytes[^1] |= (byte)(1 << (7 - _bitsInLastByte));

                _bitsInLastByte = (_bitsInLastByte + 1) & 7;
            }
        }

        /// <summary>
        /// 写入UnsignedExpGolomb。
        /// </summary>
        /// <param name="value">当前读取、写入或检查的值。</param>
        public void WriteUnsignedExpGolomb(uint value)
        {
            ulong codeNum = (ulong)value + 1;
            int bitCount = 0;
            for (ulong remaining = codeNum; remaining != 0; remaining >>= 1)
                bitCount++;
            WriteBits(0, bitCount - 1);
            WriteBits(codeNum, bitCount);
        }

        /// <summary>追加位流结束标记并返回构造的 RBSP 字节。</summary>
        /// <returns>追加位流结束标记并返回构造的 RBSP 字节后的字节数据。</returns>
        public byte[] ToRbsp()
        {
            WriteBits(1, 1); // RBSP 结束标记位。
            if (_bitsInLastByte != 0)
                WriteBits(0, 8 - _bitsInLastByte);
            return [.. _bytes];
        }
    }

    /// <summary>归还测试集合中持有的全部媒体帧引用。</summary>
    /// <param name="frames">媒体帧集合。</param>
    private static void DisposeFrames(IEnumerable<PsPesFrame> frames)
    {
        foreach (PsPesFrame frame in frames)
            frame.Dispose();
    }

    /// <summary>测试解析出的 FLV 标签类型、时间戳及数据。</summary>
    /// <param name="Type">消息或资源类型。</param>
    /// <param name="Data">待处理的数据。</param>
    private sealed record FlvTag(byte Type, byte[] Data);

    /// <summary>记录收帧、停止和释放次数的媒体录制器替身。</summary>
    private sealed class TrackingRecorder : IStreamRecorder
    {
        /// <summary>
        /// 获取或设置 帧数量。
        /// </summary>
        public int FrameCount { get; private set; }
        /// <summary>
        /// 获取或设置 Stopped。
        /// </summary>
        public bool Stopped { get; private set; }
        /// <summary>
        /// 获取或设置 释放状态。
        /// </summary>
        public bool Disposed { get; private set; }

        /// <summary>处理输入媒体帧并按录制器约定持有或复制数据。</summary>
        /// <param name="frame">媒体帧。</param>
        public void OnFrame(PooledMediaFrame frame)
        {
            Assert.True(!Stopped && !Disposed, "stopped recorder must not receive frames");
            _ = frame.GetSpan();
            FrameCount++;
        }

        /// <summary>结束当前录制并返回文件路径、字节数和时长。</summary>
        /// <returns>录制文件路径、文件字节数与录制时长。</returns>
        public (string FilePath, long FileSize, TimeSpan Duration) Stop()
        {
            Stopped = true;
            return (string.Empty, 0, TimeSpan.Zero);
        }

        /// <summary>释放当前实例拥有的资源。</summary>
        public void Dispose() => Disposed = true;
    }
}

internal static class Assert
{
    /// <summary>断言条件为真。</summary>
    /// <param name="condition">需要满足的断言或等待条件。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>断言实际值与期望值相等。</summary>
    /// <typeparam name="T">类型参数 <c>T</c>。</typeparam>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际得到的值。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }

    /// <summary>断言两个序列逐元素一致。</summary>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际得到的值。</param>
    /// <param name="message">断言失败时显示的错误说明。</param>
    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
        }
    }
}

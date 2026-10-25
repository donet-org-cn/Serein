using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Serein.Media.Protocol;

/// <summary>
/// GB/T 28181 PS（Program Stream）包解析器。
/// <para>
/// PS/RTP 媒体源将音视频数据封装为 PS 格式，再通过标准 RTP 传输。
/// PS 包结构: PS Header (14B) → System Header → PSM → PES (视频/音频)。
/// </para>
/// </summary>
public static class PsParser
{
    // PS 起始码
    /// <summary>MPEG-PS 包头起始码。</summary>
    private const uint PackStartCode = 0x000001BA;
    /// <summary>MPEG-PS 系统头起始码。</summary>
    private const uint SystemHeaderStartCode = 0x000001BB;
    /// <summary>MPEG-PS 节目流映射表起始码。</summary>
    private const uint ProgramStreamMapStartCode = 0x000001BC;
    /// <summary>MPEG-PS 节目流目录起始码。</summary>
    private const uint ProgramStreamDirectoryStartCode = 0x000001FF;
    /// <summary>MPEG-PS 私有流一的起始码。</summary>
    private const uint PrivateStream1StartCode = 0x000001BD;
    /// <summary>MPEG-PS 填充流起始码。</summary>
    private const uint PaddingStreamStartCode = 0x000001BE;

    // PES 流 ID 范围
    /// <summary>视频基本流标识范围的最小值。</summary>
    private const byte VideoStreamIdMin = 0xE0; // 视频流标识范围：0xE0～0xEF。
    /// <summary>视频基本流标识范围的最大值。</summary>
    private const byte VideoStreamIdMax = 0xEF;
    /// <summary>音频基本流标识范围的最小值。</summary>
    private const byte AudioStreamIdMin = 0xC0; // 音频流标识范围：0xC0～0xDF。
    /// <summary>音频基本流标识范围的最大值。</summary>
    private const byte AudioStreamIdMax = 0xDF;

    /// <summary>
    /// 诊断日志回调（临时，用于调试）。
    /// </summary>
    public static Action<string>? DiagnosticLog { get; set; }

    /// <summary>已经输出诊断数据的包数量。</summary>
    private static int _diagnosticPacketDumpCount;
    /// <summary>已经输出诊断数据的基本流片段数量。</summary>
    private static int _diagnosticEsDumpCount;
    /// <summary>已经输出诊断数据的合并帧数量。</summary>
    private static int _diagnosticMergeDumpCount;

    /// <summary>
    /// 解析一个 PS 包，提取其中的视频和音频 PES 数据。
    /// </summary>
    /// <param name="data">完整的 PS 包数据（可能包含多个 PS packet）。</param>
    /// <returns>解析出的 PES 帧列表（视频和音频）。</returns>
    public static List<PsPesFrame> ParsePsPacket(ReadOnlySpan<byte> data)
    {
        var videoCodec = MediaCodec.Unknown;
        return ParsePsPacket(data, ref videoCodec);
    }

    /// <summary>解析 MPEG-PS 包并提取音视频 PES 帧。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="videoCodec">跨包保留的视频编码。</param>
    /// <returns>解析 MPEG-PS 包并提取音视频 PES 帧得到的条目集合。</returns>
    public static List<PsPesFrame> ParsePsPacket(ReadOnlySpan<byte> data, ref MediaCodec videoCodec)
        => ParsePsPacket(data, ref videoCodec, new Dictionary<byte, MediaCodec>());

    /// <summary>解析 PS 并保留各音频流的 PSM 编码；字典由调用者按会话持有和重置。</summary>
    /// <param name="data">完整 PS 数据。</param>
    /// <param name="videoCodec">跨包保留的视频编码。</param>
    /// <param name="audioCodecs">按 PES 流标识保存的音频编码。</param>
    /// <returns>调用者负责释放的媒体帧。</returns>
    public static List<PsPesFrame> ParsePsPacket(ReadOnlySpan<byte> data, ref MediaCodec videoCodec, Dictionary<byte, MediaCodec> audioCodecs)
    {
        var frames = new List<PsPesFrame>(4);
        int offset = 0;

        if (data.Length < 4)
        {
            DiagnosticLog?.Invoke($"[PS解析] 数据太短: {data.Length}B");
            return frames;
        }

        // 诊断: dump 前 32 字节
        if (DiagnosticLog != null && _diagnosticPacketDumpCount < 5)
        {
            _diagnosticPacketDumpCount++;
            var header = data.Length > 32 ? data[..32] : data;
            DiagnosticLog.Invoke($"[PS解析] 开始解析 {data.Length}B, 头: {Convert.ToHexString(header)}");
        }

        while (offset < data.Length - 4)
        {
            uint startCode = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

            if (startCode == PackStartCode)
            {
                offset = ParsePackHeader(data, offset);
            }
            else if (startCode == SystemHeaderStartCode)
            {
                offset = SkipPsPacket(data, offset);
            }
            else if (startCode == ProgramStreamMapStartCode)
            {
                offset = ParseProgramStreamMap(data, offset, ref videoCodec, audioCodecs);
            }
            else if (startCode == ProgramStreamDirectoryStartCode)
            {
                offset = SkipPsPacket(data, offset);
            }
            else if (startCode == PrivateStream1StartCode || startCode == PaddingStreamStartCode)
            {
                offset = SkipPsPacket(data, offset);
            }
            else
            {
                byte streamId = data[offset + 3];
                if (streamId >= VideoStreamIdMin && streamId <= VideoStreamIdMax)
                {
                    offset = ParsePesFrame(data, offset, MediaFrameKind.Video, videoCodec, frames);
                }
                else if (streamId >= AudioStreamIdMin && streamId <= AudioStreamIdMax)
                {
                    offset = ParsePesFrame(data, offset, MediaFrameKind.Audio, audioCodecs.GetValueOrDefault(streamId, MediaCodec.G711A), frames);
                }
                else
                {
                    // 未知起始码，跳过
                    offset++;
                }
            }
        }

        return CoalesceVideoPesFragments(frames);
    }

    /// <summary>
    /// 解析 PS Pack Header (pack_start_code = 0x000001BA)。
    /// 返回解析后的偏移位置。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <returns>解析 PS Pack Header (pack_start_code = 0x000001BA)。 返回解析后的偏移位置计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParsePackHeader(ReadOnlySpan<byte> data, int offset)
    {
        // PS Pack Header 至少 14 字节
        if (offset + 14 > data.Length) return data.Length;

        // 检查 MPEG-1 还是 MPEG-2
        byte b4 = data[offset + 4];
        if ((b4 & 0xC4) == 0x44)
        {
            // MPEG-2 PS: 14 字节固定头 + 可选 stuffing
            int packStuffingLength = data[offset + 13] & 0x07;
            return offset + 14 + packStuffingLength;
        }
        else
        {
            // MPEG-1 PS: 12 字节固定头
            return offset + 12;
        }
    }

    /// <summary>
    /// 跳过一个 PS 子包（读取长度字段后跳过）。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <returns>跳过一个 PS 子包（读取长度字段后跳过）计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipPsPacket(ReadOnlySpan<byte> data, int offset)
    {
        // 起始码 4 字节 + stream_id 1 字节 + PES_packet_length 2 字节
        if (offset + 7 > data.Length) return data.Length;

        ushort pesPacketLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        return offset + 6 + pesPacketLength;
    }

    /// <summary>
    /// 解析Program流映射。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="videoCodec">跨包保留的视频编码。</param>
    /// <param name="audioCodecs">按 PES 流标识保存的音频编码。</param>
    /// <returns>解析Program流映射计算得到的数值。</returns>
    private static int ParseProgramStreamMap(
        ReadOnlySpan<byte> data,
        int offset,
        ref MediaCodec videoCodec, Dictionary<byte, MediaCodec> audioCodecs)
    {
        if (offset + 10 > data.Length)
            return data.Length;

        int mapLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        int packetEnd = offset + 6 + mapLength;
        if (packetEnd > data.Length)
            return data.Length;

        int programInfoLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
        int mapLengthOffset = offset + 10 + programInfoLength;
        if (mapLengthOffset + 2 > packetEnd)
            return packetEnd;

        int elementaryMapLength = BinaryPrimitives.ReadUInt16BigEndian(data[mapLengthOffset..]);
        int entryOffset = mapLengthOffset + 2;
        int entriesEnd = entryOffset + elementaryMapLength;
        if (entriesEnd > packetEnd)
            return packetEnd;

        while (entryOffset + 4 <= entriesEnd)
        {
            byte streamType = data[entryOffset];
            byte elementaryStreamId = data[entryOffset + 1];
            int infoLength = BinaryPrimitives.ReadUInt16BigEndian(data[(entryOffset + 2)..]);
            int nextEntry = entryOffset + 4 + infoLength;
            if (nextEntry > entriesEnd)
                break;

            if (elementaryStreamId is >= VideoStreamIdMin and <= VideoStreamIdMax)
            {
                if (streamType == 0x1B)
                    videoCodec = MediaCodec.H264;
                else if (streamType == 0x24)
                    videoCodec = MediaCodec.H265;
            }

            if (elementaryStreamId is >= AudioStreamIdMin and <= AudioStreamIdMax)
            {
                audioCodecs[elementaryStreamId] = streamType switch
                {
                    0x90 => MediaCodec.G711A,
                    0x91 => MediaCodec.G711U,
                    0x0F => MediaCodec.AAC,
                    _ => MediaCodec.Unknown
                };
            }

            entryOffset = nextEntry;
        }

        return packetEnd;
    }

    /// <summary>
    /// 解析 PES 包，提取 ES（Elementary Stream）数据。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="kind">媒体或消息类别。</param>
    /// <param name="videoCodec">跨包保留的视频编码。</param>
    /// <param name="frames">媒体帧集合。</param>
    /// <returns>解析 PES 包，提取 ES（Elementary Stream）数据计算得到的数值。</returns>
    private static int ParsePesFrame(ReadOnlySpan<byte> data, int offset,
        MediaFrameKind kind, MediaCodec videoCodec, List<PsPesFrame> frames)
    {
        // PES 结构: start_code_prefix(3B) + stream_id(1B) + PES_packet_length(2B)
        // + PES_header_data(可选) + ES data
        if (offset + 9 > data.Length) return data.Length;

        int streamId = data[offset + 3];
        ushort pesPacketLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        int pesEnd;
        if (pesPacketLength == 0)
        {
            pesEnd = FindNextStartCode(data, offset + 9);
        }
        else
        {
            pesEnd = offset + 6 + pesPacketLength;
            // 不得发布尚未达到声明长度的 PES 包；把截断的参考图像送给解码器，常会造成 RTP 丢包后的持续花屏。
            if (pesEnd > data.Length)
                return data.Length;
        }

        // PES 头标志。
        byte b6 = data[offset + 6]; // 首字节依次包含固定前缀 10、加扰、优先级、对齐、版权与原始副本标志。
        byte b7 = data[offset + 7]; // 次字节依次包含 PTS/DTS、ESCR、码率、特技模式、附加副本、CRC 及扩展标志。
        byte pesHeaderDataLength = data[offset + 8];

        int esOffset = offset + 9 + pesHeaderDataLength;
        if (esOffset > pesEnd) return pesEnd;

        int esLength = pesEnd - esOffset;
        if (esLength <= 0) return pesEnd;

        // 解析 PTS（呈现时间戳，90kHz）
        long pts = 0;
        byte ptsDtsFlags = (byte)((b7 >> 6) & 0x03);
        if (ptsDtsFlags >= 2 && pesHeaderDataLength >= 5)
        {
            pts = ParsePts(data[(offset + 9)..]);
        }

        // 提取 ES 数据
        var esData = data[esOffset..pesEnd];

        // 诊断: 输出前几帧的 ES 数据头
        if (DiagnosticLog != null && _diagnosticEsDumpCount < 20 && kind == MediaFrameKind.Video)
        {
            _diagnosticEsDumpCount++;
            var esPreview = esData.Length > 32 ? esData[..32] : esData;
            DiagnosticLog.Invoke($"[PS解析] PES ES数据: streamId={streamId}, esLen={esLength}, esOffset={esOffset}, pesEnd={pesEnd}, pts={pts}, 前32B: {Convert.ToHexString(esPreview)}");
        }

        // 推测编码格式
        MediaCodec codec;
        if (kind == MediaFrameKind.Video)
        {
            // 通过 NAL 头判断 H.264 还是 H.265
            codec = videoCodec is MediaCodec.H264 or MediaCodec.H265
                ? videoCodec
                : DetectVideoCodec(esData);
        }
        else
        {
            // 已声明的音频编码来自 PSM，未知编码不得当成 G.711 播放。
            codec = videoCodec;
        }

        frames.Add(PsPesFrame.Create(kind, codec, pts, esData));

        return pesEnd;
    }

    /// <summary>
    /// 查找下次起始Code。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <returns>找到的位置索引；未找到时返回 -1。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNextStartCode(ReadOnlySpan<byte> data, int offset)
    {
        for (int i = offset; i < data.Length - 3; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01 && IsPsStartCodeByte(data[i + 3]))
            {
                return i;
            }
        }

        return data.Length;
    }

    /// <summary>
    /// 判断是否Ps起始CodeByte。
    /// </summary>
    /// <param name="value">当前读取、写入或检查的值。</param>
    /// <returns>是否Ps起始CodeByte的判定结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPsStartCodeByte(byte value)
    {
        return value is 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xFF
            || value is >= AudioStreamIdMin and <= AudioStreamIdMax
            || value is >= VideoStreamIdMin and <= VideoStreamIdMax;
    }

    /// <summary>
    /// 解析 PTS（33 位，90kHz 时钟）。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <returns>解析 PTS（33 位，90kHz 时钟）计算得到的数值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParsePts(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return 0;

        // PTS 由四位前缀、三段时间戳（3、15、15 位）及各段后的标记位组成。
        long b0 = data[0];
        long b1 = data[1];
        long b2 = data[2];
        long b3 = data[3];
        long b4 = data[4];

        long pts = ((b0 >> 1) & 0x07) << 30;
        pts |= (((b1 << 8) | b2) >> 1) << 15;
        pts |= (((b3 << 8) | b4) >> 1);
        return pts;
    }

    /// <summary>
    /// 通过 ES 数据的前几个字节判断视频编码格式。
    /// </summary>
    /// <param name="esData">基本媒体流数据。</param>
    /// <returns>通过 ES 数据的前几个字节判断视频编码格式得到的MediaCodec对象。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MediaCodec DetectVideoCodec(ReadOnlySpan<byte> esData)
    {
        // 优先使用 AVC 与 HEVC 间无歧义的特征；存在节目流映射表时，仍以该表声明为准。
        for (int i = 0; i + 4 < esData.Length; i++)
        {
            if (esData[i] != 0 || esData[i + 1] != 0)
                continue;

            int nalOffset;
            if (esData[i + 2] == 1)
                nalOffset = i + 3;
            else if (esData[i + 2] == 0 && esData[i + 3] == 1)
                nalOffset = i + 4;
            else
                continue;

            if (nalOffset >= esData.Length)
                break;

            byte first = esData[nalOffset];
            int h264Type = first & 0x1F;
            int h265Type = (first >> 1) & 0x3F;
            bool validH265Header = nalOffset + 1 < esData.Length
                && (first & 0x80) == 0
                && (esData[nalOffset + 1] & 0x07) != 0;

            if (validH265Header &&
                (first == 0x40 || h265Type is 33 or 34 || h265Type is >= 16 and <= 21))
                return MediaCodec.H265;
            if (h264Type is 5 or 7 or 8)
                return MediaCodec.H264;
        }

        // 查找起始码 0x00 0x00 0x01 后的第一个 NAL
        for (int i = 0; i < Math.Min(esData.Length - 4, 64); i++)
        {
            if (esData[i] == 0 && esData[i + 1] == 0 && esData[i + 2] == 1)
            {
                byte nalByte = esData[i + 3];

                // H.264 的单字节 NAL 头依次为禁止位、两位参考级别和五位单元类型。
                // 常见 type: 1=slice, 2=slice_A, 5=IDR, 6=SEI, 7=SPS, 8=PPS
                // 注意: H.264 SPS 的 byte=0x67, 如果按 H.265 解码 (0x67>>1)&0x3F=51 会误判!
                int h264Type = nalByte & 0x1F;
                int h264NalRefIdc = (nalByte >> 5) & 0x03;

                // forbidden_zero_bit 必须为 0
                if ((nalByte & 0x80) == 0 && h264NalRefIdc > 0 &&
                    h264Type is >= 1 and <= 8)
                {
                    // 高概率是 H.264: SPS(7), PPS(8), IDR(5), slice(1) 等都有 nal_ref_idc > 0
                    return MediaCodec.H264;
                }

                // H.265 的双字节 NAL 头依次为禁止位、六位单元类型、六位层标识和三位时间层加一值。
                // HEVC 类型编号：VPS=32、SPS=33、PPS=34、IDR_W_RADL=19、IDR_N_LP=20、TRAIL_N=0、TRAIL_R=1。
                int h265Type = (nalByte >> 1) & 0x3F;
                if (h265Type is >= 32 and <= 63)
                    return MediaCodec.H265;

                // 对于 H.265 非 VPS/SPS/PPS 的 NAL (type 0-31)，检查第 2 字节
                // nuh_temporal_id_plus1 必须非零 (通常 = 1)
                if (i + 4 < esData.Length && (nalByte & 0x80) == 0)
                {
                    byte nalByte2 = esData[i + 4];
                    int temporalIdPlus1 = nalByte2 & 0x07;
                    if (temporalIdPlus1 > 0 && h265Type <= 40)
                        return MediaCodec.H265;
                }

                // 默认 H.264
                return MediaCodec.H264;
            }
        }

        // 没找到起始码，检查第一个字节
        if (esData.Length > 0)
        {
            byte b = esData[0];
            int h264Type = b & 0x1F;
            int h264NalRefIdc = (b >> 5) & 0x03;
            if ((b & 0x80) == 0 && h264NalRefIdc > 0 && h264Type is >= 1 and <= 8)
                return MediaCodec.H264;

            int h265Type = (b >> 1) & 0x3F;
            if (h265Type is >= 32 and <= 63)
                return MediaCodec.H265;
        }

        return MediaCodec.H264;
    }

    /// <summary>按显示时间戳边界合并同一访问单元的 PES 分片。</summary>
    /// <param name="frames">媒体帧集合。</param>
    /// <returns>按显示时间戳边界合并同一访问单元的 PES 分片得到的条目集合。</returns>
    private static List<PsPesFrame> CoalesceVideoPesFragments(List<PsPesFrame> frames)
    {
        if (frames.Count <= 1) return frames;

        var result = new List<PsPesFrame>(frames.Count);
        List<PsPesFrame>? pendingVideo = null;
        long pendingVideoTimestamp = 0;
        MediaCodec pendingVideoCodec = MediaCodec.Unknown;

        foreach (var frame in frames)
        {
            if (frame.Kind == MediaFrameKind.Video)
            {
                // 一个 PS 包可以合法包含多个访问单元。同一单元的 PES 分片通常重复 PTS，或仅首片带 PTS；新的非零 PTS 表示帧边界，不能合并到前一帧。
                if (pendingVideo != null &&
                    ((frame.Timestamp90kHz > 0 && pendingVideoTimestamp > 0 &&
                      frame.Timestamp90kHz != pendingVideoTimestamp) ||
                     (frame.Codec != MediaCodec.Unknown && pendingVideoCodec != MediaCodec.Unknown &&
                      frame.Codec != pendingVideoCodec)))
                {
                    FlushPendingVideo();
                }

                pendingVideo ??= new List<PsPesFrame>();
                pendingVideo.Add(frame);
                if (pendingVideoTimestamp == 0 && frame.Timestamp90kHz > 0)
                    pendingVideoTimestamp = frame.Timestamp90kHz;
                if (pendingVideoCodec == MediaCodec.Unknown)
                    pendingVideoCodec = frame.Codec;
                continue;
            }

            FlushPendingVideo();
            result.Add(frame);
        }

        FlushPendingVideo();
        return result;

        // <summary>将已积累的视频片段合并为一个 PES 帧。</summary>
        void FlushPendingVideo()
        {
            if (pendingVideo == null || pendingVideo.Count == 0) return;

            if (pendingVideo.Count == 1)
            {
                result.Add(pendingVideo[0]);
                pendingVideo = null;
                pendingVideoTimestamp = 0;
                pendingVideoCodec = MediaCodec.Unknown;
                return;
            }

            int totalLength = 0;
            long timestamp90kHz = 0;
            MediaCodec codec = MediaCodec.Unknown;

            foreach (var video in pendingVideo)
            {
                totalLength += video.Length;
                if (timestamp90kHz == 0 && video.Timestamp90kHz > 0)
                    timestamp90kHz = video.Timestamp90kHz;
                if (codec == MediaCodec.Unknown)
                    codec = video.Codec;
            }

            var merged = ArrayPool<byte>.Shared.Rent(totalLength);
            int offset = 0;
            foreach (var video in pendingVideo)
            {
                video.GetSpan().CopyTo(merged.AsSpan(offset));
                offset += video.Length;
                video.Dispose();
            }

            if (DiagnosticLog != null && _diagnosticMergeDumpCount < 10)
            {
                _diagnosticMergeDumpCount++;
                DiagnosticLog.Invoke($"[PS解析] 合并视频 PES: {pendingVideo.Count}段 => {totalLength}B, pts={timestamp90kHz}");
            }
            result.Add(PsPesFrame.TakeOwnership(MediaFrameKind.Video,
                codec == MediaCodec.Unknown ? MediaCodec.H264 : codec,
                timestamp90kHz,
                merged,
                totalLength));
            pendingVideo = null;
            pendingVideoTimestamp = 0;
            pendingVideoCodec = MediaCodec.Unknown;
        }
    }
}

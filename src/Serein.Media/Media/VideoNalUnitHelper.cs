using System.Runtime.CompilerServices;

namespace Serein.Media;

/// <summary>视频访问单元的参数集、切片与随机接入扫描结果。</summary>
/// <param name="HasNalUnits">是否发现 NAL 单元。</param>
/// <param name="HasParameterSet">是否发现视频参数集。</param>
/// <param name="HasNonParameterSet">是否发现非参数集 NAL 单元。</param>
/// <param name="HasVcl">是否包含视频编码层 NAL 单元。</param>
/// <param name="IsRandomAccess">是否包含可随机接入的视频帧。</param>
/// <param name="ParameterSetMask">已发现的视频参数集类型掩码。</param>
internal readonly record struct VideoAccessUnitInfo(
    bool HasNalUnits,
    bool HasParameterSet,
    bool HasNonParameterSet,
    bool HasVcl,
    bool IsRandomAccess,
    byte ParameterSetMask);

/// <summary>检查视频 NAL 类型、参数集与随机接入条件。</summary>
internal static class VideoNalUnitHelper
{
    /// <summary>扫描 NAL 单元并汇总参数集、切片与随机接入特征。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>扫描 NAL 单元并汇总参数集、切片与随机接入特征得到的VideoAccessUnitInfo对象。</returns>
    public static VideoAccessUnitInfo Analyze(ReadOnlySpan<byte> data, MediaCodec codec)
    {
        bool hasNal = false;
        bool hasParameter = false;
        bool hasNonParameter = false;
        bool hasVcl = false;
        bool randomAccess = false;
        byte parameterSetMask = 0;

        int searchOffset = 0;
        while (TryFindStartCode(data, searchOffset, out int nalOffset))
        {
            if (nalOffset >= data.Length)
                break;

            AnalyzeHeader(data[nalOffset], codec, ref hasParameter,
                ref hasNonParameter, ref hasVcl, ref randomAccess, ref parameterSetMask);
            hasNal = true;
            searchOffset = nalOffset + 1;
        }

        // RTP 解包器可能传入单个原始 NAL，而不是 Annex-B 格式。
        if (!hasNal && !data.IsEmpty)
        {
            AnalyzeHeader(data[0], codec, ref hasParameter,
                ref hasNonParameter, ref hasVcl, ref randomAccess, ref parameterSetMask);
            hasNal = true;
        }

        return new VideoAccessUnitInfo(
            hasNal, hasParameter, hasNonParameter, hasVcl, randomAccess, parameterSetMask);
    }

    /// <summary>
    /// 判断是否RandomAccessPoint。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否RandomAccessPoint的判定结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRandomAccessPoint(ReadOnlySpan<byte> data, MediaCodec codec) =>
        Analyze(data, codec).IsRandomAccess;

    /// <summary>
    /// 判断是否参数SetOnly。
    /// </summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>是否参数SetOnly的判定结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsParameterSetOnly(ReadOnlySpan<byte> data, MediaCodec codec)
    {
        var info = Analyze(data, codec);
        return info.HasParameterSet && !info.HasVcl;
    }

    /// <summary>检查首个视频编码层 NAL 之前是否存在参数集。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <returns>首个视频编码层 NAL 之前是否存在参数集的判定结果。</returns>
    public static bool HasParameterSetBeforeFirstVcl(ReadOnlySpan<byte> data, MediaCodec codec)
    {
        bool foundStartCode = false;
        int searchOffset = 0;
        while (TryFindStartCode(data, searchOffset, out int nalOffset))
        {
            foundStartCode = true;
            if (nalOffset >= data.Length ||
                (codec == MediaCodec.H265 && nalOffset + 1 >= data.Length))
                return false;

            int type = codec == MediaCodec.H265
                ? (data[nalOffset] >> 1) & 0x3F
                : data[nalOffset] & 0x1F;
            if (codec == MediaCodec.H265)
            {
                if (type is 32 or 33 or 34) return true;
                if (type <= 31) return false;
            }
            else
            {
                if (type is 7 or 8) return true;
                if (type is >= 1 and <= 5) return false;
            }

            searchOffset = nalOffset + 1;
        }

        if (foundStartCode || data.IsEmpty ||
            (codec == MediaCodec.H265 && data.Length < 2))
            return false;

        int rawType = codec == MediaCodec.H265
            ? (data[0] >> 1) & 0x3F
            : data[0] & 0x1F;
        return codec == MediaCodec.H265
            ? rawType is 32 or 33 or 34
            : rawType is 7 or 8;
    }

    /// <summary>按视频编码识别 NAL 头的类型与关键帧特征。</summary>
    /// <param name="header">协议头数据。</param>
    /// <param name="codec">媒体编码格式。</param>
    /// <param name="hasParameter">是否发现参数集。</param>
    /// <param name="hasNonParameter">是否发现非参数集数据。</param>
    /// <param name="hasVcl">是否包含视频编码层 NAL 单元。</param>
    /// <param name="randomAccess">是否可从当前视频帧随机接入。</param>
    /// <param name="parameterSetMask">已发现的视频参数集类型掩码。</param>
    private static void AnalyzeHeader(
        byte header,
        MediaCodec codec,
        ref bool hasParameter,
        ref bool hasNonParameter,
        ref bool hasVcl,
        ref bool randomAccess,
        ref byte parameterSetMask)
    {
        if (codec == MediaCodec.H265)
        {
            int type = (header >> 1) & 0x3F;
            bool parameter = type is 32 or 33 or 34;
            hasParameter |= parameter;
            if (type == 32) parameterSetMask |= 0b001;
            else if (type == 33) parameterSetMask |= 0b010;
            else if (type == 34) parameterSetMask |= 0b100;
            hasNonParameter |= !parameter;
            if (type <= 31)
            {
                hasVcl = true;
                randomAccess |= type is >= 16 and <= 21;
            }
            return;
        }

        int h264Type = header & 0x1F;
        bool h264Parameter = h264Type is 7 or 8;
        hasParameter |= h264Parameter;
        if (h264Type == 7) parameterSetMask |= 0b010;
        else if (h264Type == 8) parameterSetMask |= 0b100;
        hasNonParameter |= !h264Parameter;
        if (h264Type is >= 1 and <= 5)
        {
            hasVcl = true;
            randomAccess |= h264Type == 5;
        }
    }

    /// <summary>尝试定位下一个 Annex-B 起始码。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalOffset">NAL 单元在输入数据中的偏移量。</param>
    /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int offset, out int nalOffset)
    {
        int start = AnnexBStartCode.Find(data, offset, out int length);
        nalOffset = start < 0 ? 0 : start + length;
        return start >= 0;
    }
}

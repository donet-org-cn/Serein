namespace Serein.Media;

/// <summary>从 HEVC 序列参数集中提取的解码配置。</summary>
/// <param name="GeneralProfile">HEVC 通用配置档字段。</param>
/// <param name="GeneralProfileCompatibilityFlags">HEVC 配置档兼容性标志。</param>
/// <param name="GeneralConstraintIndicatorFlags">HEVC 通用约束标志。</param>
/// <param name="GeneralLevelIdc">HEVC 编码级别编号。</param>
/// <param name="NumTemporalLayers">HEVC 时间层数量。</param>
/// <param name="TemporalIdNested">HEVC 时间层嵌套标志。</param>
/// <param name="ChromaFormatIdc">HEVC 色度格式编号。</param>
/// <param name="BitDepthLumaMinus8">亮度位深与八位的差值。</param>
/// <param name="BitDepthChromaMinus8">色度位深与八位的差值。</param>
public readonly record struct HevcSpsConfiguration(
    byte GeneralProfile,
    uint GeneralProfileCompatibilityFlags,
    ulong GeneralConstraintIndicatorFlags,
    byte GeneralLevelIdc,
    byte NumTemporalLayers,
    bool TemporalIdNested,
    byte ChromaFormatIdc,
    byte BitDepthLumaMinus8,
    byte BitDepthChromaMinus8)
{
    /// <summary>缺少可解析参数集时采用的默认解码配置。</summary>
    public static HevcSpsConfiguration Default { get; } = new(
        GeneralProfile: 0,
        GeneralProfileCompatibilityFlags: 0,
        GeneralConstraintIndicatorFlags: 0,
        GeneralLevelIdc: 0,
        NumTemporalLayers: 1,
        TemporalIdNested: false,
        ChromaFormatIdc: 1,
        BitDepthLumaMinus8: 0,
        BitDepthChromaMinus8: 0);
}

/// <summary>解析 HEVC 序列参数集中的配置档、级别与位深。</summary>
public static class HevcSpsParser
{
    /// <summary>HEVC 序列参数集的 NAL 类型编号。</summary>
    private const int SpsNalUnitType = 33;

    /// <summary>解析 HEVC SPS 的配置档、级别、时间层、色度格式与位深。</summary>
    /// <param name="spsNal">序列参数集 NAL 单元。</param>
    /// <param name="configuration">成功时得到的解码配置；失败时保留默认配置。</param>
    /// <returns>SPS 格式与必要位字段有效时为 true；输入截断或字段无效时为 false。</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> spsNal,
        out HevcSpsConfiguration configuration)
    {
        configuration = HevcSpsConfiguration.Default;
        if (spsNal.Length < 3 || ((spsNal[0] >> 1) & 0x3F) != SpsNalUnitType)
            return false;

        // forbidden_zero_bit 必须为零，nuh_temporal_id_plus1 必须非零。
        if ((spsNal[0] & 0x80) != 0 || (spsNal[1] & 0x07) == 0)
            return false;

        byte[] rbsp = new byte[spsNal.Length - 2];
        int rbspLength = RemoveEmulationPreventionBytes(spsNal[2..], rbsp);
        if (rbspLength == 0)
            return false;

        var reader = new BitReader(rbsp.AsSpan(0, rbspLength));

        if (!reader.TrySkipBits(4) ||
            !reader.TryReadBits(3, out ulong maxSubLayersMinus1Value) ||
            maxSubLayersMinus1Value > 6 ||
            !reader.TryReadBit(out bool temporalIdNested) ||
            !reader.TryReadBits(8, out ulong generalProfile) ||
            !reader.TryReadBits(32, out ulong compatibilityFlags) ||
            !reader.TryReadBits(48, out ulong constraintFlags) ||
            !reader.TryReadBits(8, out ulong levelIdc))
        {
            return false;
        }

        int maxSubLayersMinus1 = (int)maxSubLayersMinus1Value;
        Span<bool> subLayerProfilePresent = stackalloc bool[7];
        Span<bool> subLayerLevelPresent = stackalloc bool[7];
        for (int index = 0; index < maxSubLayersMinus1; index++)
        {
            if (!reader.TryReadBit(out subLayerProfilePresent[index]) ||
                !reader.TryReadBit(out subLayerLevelPresent[index]))
            {
                return false;
            }
        }

        if (maxSubLayersMinus1 > 0 &&
            !reader.TrySkipBits((8 - maxSubLayersMinus1) * 2))
        {
            return false;
        }

        for (int index = 0; index < maxSubLayersMinus1; index++)
        {
            // 跳过子层的配置档字段（8 位）、兼容性标志（32 位）和约束标志（48 位）。
            if (subLayerProfilePresent[index] && !reader.TrySkipBits(88))
                return false;
            if (subLayerLevelPresent[index] && !reader.TrySkipBits(8))
                return false;
        }

        if (!reader.TryReadUnsignedExpGolomb(out _) ||
            !reader.TryReadUnsignedExpGolomb(out uint chromaFormatIdc) ||
            chromaFormatIdc > 3)
        {
            return false;
        }

        if (chromaFormatIdc == 3 && !reader.TrySkipBits(1))
            return false;

        if (!reader.TryReadUnsignedExpGolomb(out _) || // 以亮度样本计数的图像宽度。
            !reader.TryReadUnsignedExpGolomb(out _) || // 以亮度样本计数的图像高度。
            !reader.TryReadBit(out bool conformanceWindowFlag))
        {
            return false;
        }

        if (conformanceWindowFlag)
        {
            for (int index = 0; index < 4; index++)
            {
                if (!reader.TryReadUnsignedExpGolomb(out _))
                    return false;
            }
        }

        if (!reader.TryReadUnsignedExpGolomb(out uint bitDepthLumaMinus8) ||
            !reader.TryReadUnsignedExpGolomb(out uint bitDepthChromaMinus8) ||
            bitDepthLumaMinus8 > 7 || bitDepthChromaMinus8 > 7)
        {
            return false;
        }

        configuration = new HevcSpsConfiguration(
            GeneralProfile: (byte)generalProfile,
            GeneralProfileCompatibilityFlags: (uint)compatibilityFlags,
            GeneralConstraintIndicatorFlags: constraintFlags,
            GeneralLevelIdc: (byte)levelIdc,
            NumTemporalLayers: (byte)(maxSubLayersMinus1 + 1),
            TemporalIdNested: temporalIdNested,
            ChromaFormatIdc: (byte)chromaFormatIdc,
            BitDepthLumaMinus8: (byte)bitDepthLumaMinus8,
            BitDepthChromaMinus8: (byte)bitDepthChromaMinus8);
        return true;
    }

    /// <summary>
    /// 移除EmulationPrevention字节序列。
    /// </summary>
    /// <param name="ebsp">包含防竞争字节的编码序列数据。</param>
    /// <param name="rbsp">移除防竞争字节后的原始序列数据。</param>
    /// <returns>移除EmulationPrevention字节序列计算得到的数值。</returns>
    private static int RemoveEmulationPreventionBytes(
        ReadOnlySpan<byte> ebsp,
        Span<byte> rbsp)
    {
        int written = 0;
        int consecutiveZeros = 0;
        for (int index = 0; index < ebsp.Length; index++)
        {
            byte value = ebsp[index];
            if (consecutiveZeros >= 2 && value == 0x03 &&
                index + 1 < ebsp.Length && ebsp[index + 1] <= 0x03)
            {
                consecutiveZeros = 0;
                continue;
            }

            rbsp[written++] = value;
            consecutiveZeros = value == 0 ? consecutiveZeros + 1 : 0;
        }

        return written;
    }

    /// <summary>在 HEVC 参数集边界内读取位字段与指数哥伦布编码。</summary>
    private ref struct BitReader
    {
        /// <summary>待处理的数据。</summary>
        private readonly ReadOnlySpan<byte> _data;
        /// <summary>位读取器当前的位偏移。</summary>
        private int _bitOffset;

        /// <summary>
        /// 初始化 BitReader 的新实例。
        /// </summary>
        /// <param name="data">待处理的数据。</param>
        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        /// <summary>尝试从位流读取一位。</summary>
        /// <param name="value">当前读取、写入或检查的值。</param>
        /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
        public bool TryReadBit(out bool value)
        {
            if (!TryReadBits(1, out ulong bits))
            {
                value = false;
                return false;
            }

            value = bits != 0;
            return true;
        }

        /// <summary>尝试从位流读取指定数量的位。</summary>
        /// <param name="count">数据长度或数量。</param>
        /// <param name="value">当前读取、写入或检查的值。</param>
        /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
        public bool TryReadBits(int count, out ulong value)
        {
            value = 0;
            if ((uint)count > 64 || count > (long)_data.Length * 8 - _bitOffset)
                return false;

            for (int index = 0; index < count; index++)
            {
                int bitIndex = _bitOffset + index;
                value = (value << 1) |
                    (uint)((_data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1);
            }

            _bitOffset += count;
            return true;
        }

        /// <summary>尝试跳过指定数量的位，越界时返回失败。</summary>
        /// <param name="count">数据长度或数量。</param>
        /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
        public bool TrySkipBits(int count)
        {
            if (count < 0 || count > (long)_data.Length * 8 - _bitOffset)
                return false;

            _bitOffset += count;
            return true;
        }

        /// <summary>尝试解码无符号指数哥伦布编码值。</summary>
        /// <param name="value">当前读取、写入或检查的值。</param>
        /// <returns>操作成功时为 true；未满足条件或未完成操作时为 false。</returns>
        public bool TryReadUnsignedExpGolomb(out uint value)
        {
            value = 0;
            int leadingZeros = 0;
            while (true)
            {
                if (!TryReadBit(out bool bit))
                    return false;
                if (bit)
                    break;
                if (++leadingZeros > 31)
                    return false;
            }

            ulong suffix = 0;
            if (leadingZeros > 0 && !TryReadBits(leadingZeros, out suffix))
                return false;

            ulong decoded = ((1UL << leadingZeros) - 1) + suffix;
            if (decoded > uint.MaxValue)
                return false;

            value = (uint)decoded;
            return true;
        }
    }
}

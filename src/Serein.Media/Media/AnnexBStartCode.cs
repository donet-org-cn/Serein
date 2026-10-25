namespace Serein.Media;

/// <summary>使用连续字节搜索定位 Annex-B 起始码和 NAL 边界。</summary>
internal static class AnnexBStartCode
{
    /// <summary>查找下一个三字节或四字节 Annex-B 起始码。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="length">找到的起始码字节数；未找到时为零。</param>
    /// <returns>起始码的字节偏移；未找到时为 -1。</returns>
    internal static int Find(ReadOnlySpan<byte> data, int offset, out int length)
    {
        length = 0;
        offset = Math.Max(0, offset);
        if (offset > data.Length - 3) return -1;

        // 批量匹配完整三字节前缀，避免在负载中逐个检查 0x01 候选。
        int relative = data[offset..].IndexOf("\0\0\u0001"u8);
        if (relative < 0) return -1;
        int start = offset + relative;
        if (start > offset && data[start - 1] == 0)
        {
            length = 4;
            return start - 1;
        }
        length = 3;
        return start;
    }

    /// <summary>定位下一个包含 NAL 头的 Annex-B 负载区间。</summary>
    /// <param name="data">待处理的数据。</param>
    /// <param name="offset">相对于输入数据起点的字节偏移量。</param>
    /// <param name="nalu">不含 Annex-B 起始码的 NAL 单元。</param>
    /// <param name="nextOffset">下一段数据的起始偏移量。</param>
    /// <returns>取得非空 NAL 区间时为 true；否则为 false。</returns>
    internal static bool TryReadNext(ReadOnlySpan<byte> data, int offset,
        out ReadOnlySpan<byte> nalu, out int nextOffset)
    {
        nalu = default;
        nextOffset = data.Length;
        int start = Find(data, offset, out int length);
        if (start < 0 || start + length >= data.Length) return false;
        int payload = start + length;
        // 既有调用方把负载的第一个字节作为 NAL 头。
        int end = Find(data, payload + 1, out _);
        if (end < 0) end = data.Length;
        nalu = data[payload..end];
        nextOffset = end;
        return true;
    }
}

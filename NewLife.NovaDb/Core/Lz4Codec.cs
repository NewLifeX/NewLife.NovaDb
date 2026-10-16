using System.Buffers.Binary;
using System.IO;

namespace NewLife.NovaDb.Core;

/// <summary>纯托管的 LZ4 块格式压缩/解压实现，无第三方依赖</summary>
/// <remarks>
/// 采用 LZ4 块格式（Block Format），不包含帧头与校验，适合列式数据块等已知原始长度的场景。
/// 解压时必须提供原始长度；压缩无收益时可用 <see cref="TryCompress"/> 回退原样数据。
/// </remarks>
public static class Lz4Codec
{
    /// <summary>最小匹配长度</summary>
    private const Int32 MinMatchLength = 4;

    /// <summary>最大匹配距离</summary>
    private const Int32 MaxOffset = 65535;

    /// <summary>匹配起始位置预留的尾部字节数，保证最后 5 字节为字面量</summary>
    private const Int32 MatchFindLimit = 12;

    /// <summary>块末固定作为字面量的字节数</summary>
    private const Int32 LastLiterals = 5;

    /// <summary>哈希表位数</summary>
    private const Int32 HashBits = 12;

    /// <summary>哈希表大小</summary>
    private const Int32 HashTableSize = 1 << HashBits;

    /// <summary>获取压缩后可能的最大长度</summary>
    /// <param name="sourceLength">原始数据长度</param>
    /// <returns>压缩输出缓冲区所需的最大长度</returns>
    public static Int32 GetMaxCompressedLength(Int32 sourceLength) => sourceLength + sourceLength / 255 + 16;

    /// <summary>压缩数据为 LZ4 块格式</summary>
    /// <param name="source">原始数据</param>
    /// <returns>压缩后的字节数组，空输入返回空数组</returns>
    public static Byte[] Compress(ReadOnlySpan<Byte> source)
    {
        var srcLen = source.Length;
        if (srcLen == 0) return [];

        var dst = new Byte[GetMaxCompressedLength(srcLen)];
        var op = 0;
        var anchor = 0;

        if (srcLen >= MatchFindLimit)
        {
            var table = new Int32[HashTableSize];
            for (var k = 0; k < HashTableSize; k++)
            {
                table[k] = -1;
            }

            var limit = srcLen - MatchFindLimit;
            var matchLimit = srcLen - LastLiterals;
            var i = 0;
            while (i <= limit)
            {
                var sequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i));
                var h = Hash(sequence);
                var candidate = table[h];
                table[h] = i;

                if (candidate < 0 || i - candidate > MaxOffset || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(candidate)) != sequence)
                {
                    i++;
                    continue;
                }

                var len = MinMatchLength;
                while (i + len < matchLimit && source[candidate + len] == source[i + len])
                {
                    len++;
                }

                op = EmitSequence(dst, op, source, anchor, i, len, i - candidate);
                i += len;
                anchor = i;
            }
        }

        // 末尾字面量序列
        var lastLen = srcLen - anchor;
        dst[op++] = (Byte)((lastLen >= 15 ? 15 : lastLen) << 4);
        if (lastLen >= 15) op = WriteLength(dst, op, lastLen - 15);
        source.Slice(anchor, lastLen).CopyTo(dst.AsSpan(op));
        op += lastLen;

        return dst.AsSpan(0, op).ToArray();
    }

    /// <summary>解压 LZ4 块格式数据</summary>
    /// <param name="source">压缩数据</param>
    /// <param name="outputLength">原始数据长度</param>
    /// <returns>解压后的字节数组</returns>
    public static Byte[] Decompress(ReadOnlySpan<Byte> source, Int32 outputLength)
    {
        if (outputLength < 0) throw new ArgumentOutOfRangeException(nameof(outputLength));
        if (outputLength == 0)
        {
            if (source.Length != 0) throw new InvalidDataException("LZ4 数据与原始长度不匹配");
            return [];
        }

        var dst = new Byte[outputLength];
        var sp = 0;
        var dp = 0;
        while (sp < source.Length)
        {
            var token = source[sp++];

            // 字面量
            var literalLen = token >> 4;
            if (literalLen == 15)
            {
                Int32 b;
                do
                {
                    if (sp >= source.Length) throw new InvalidDataException("LZ4 字面量长度越界");
                    b = source[sp++];
                    literalLen += b;
                }
                while (b == 255);
            }

            if (literalLen > 0)
            {
                if (sp + literalLen > source.Length || dp + literalLen > outputLength) throw new InvalidDataException("LZ4 字面量越界");
                source.Slice(sp, literalLen).CopyTo(dst.AsSpan(dp));
                sp += literalLen;
                dp += literalLen;
            }

            // 末尾字面量序列
            if (sp >= source.Length) break;

            if (sp + 2 > source.Length) throw new InvalidDataException("LZ4 偏移量缺失");
            var offset = source[sp] | (source[sp + 1] << 8);
            sp += 2;
            if (offset == 0 || offset > dp) throw new InvalidDataException("LZ4 匹配偏移量非法");

            // 匹配长度
            var matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                Int32 b;
                do
                {
                    if (sp >= source.Length) throw new InvalidDataException("LZ4 匹配长度越界");
                    b = source[sp++];
                    matchLen += b;
                }
                while (b == 255);
            }

            matchLen += MinMatchLength;
            if (dp + matchLen > outputLength) throw new InvalidDataException("LZ4 匹配长度越界");

            var match = dp - offset;
            for (var k = 0; k < matchLen; k++)
            {
                dst[dp++] = dst[match + k];
            }
        }

        if (dp != outputLength) throw new InvalidDataException($"LZ4 解压长度不匹配：期望 {outputLength}，实际 {dp}");

        return dst;
    }

    /// <summary>尝试压缩，压缩无收益时回退原样数据</summary>
    /// <param name="source">原始数据</param>
    /// <param name="compressed">输出数据：压缩结果或原始数据副本</param>
    /// <returns>是否实际压缩成功（false 表示压缩后无收益，输出为原始数据）</returns>
    public static Boolean TryCompress(ReadOnlySpan<Byte> source, out Byte[] compressed)
    {
        var data = Compress(source);
        if (data.Length >= source.Length)
        {
            compressed = source.ToArray();
            return false;
        }

        compressed = data;
        return true;
    }

    /// <summary>尝试解压，数据非法时返回 false</summary>
    /// <param name="source">压缩数据</param>
    /// <param name="outputLength">原始数据长度</param>
    /// <param name="data">解压结果，失败时为空数组</param>
    /// <returns>是否解压成功</returns>
    public static Boolean TryDecompress(ReadOnlySpan<Byte> source, Int32 outputLength, out Byte[] data)
    {
        try
        {
            data = Decompress(source, outputLength);
            return true;
        }
        catch (InvalidDataException)
        {
            data = [];
            return false;
        }
    }

    #region 辅助

    /// <summary>计算 4 字节序列的哈希值</summary>
    /// <param name="sequence">4 字节序列</param>
    /// <returns>哈希桶下标</returns>
    private static Int32 Hash(UInt32 sequence) => (Int32)(sequence * 2654435761u >> (32 - HashBits));

    /// <summary>输出一段匹配序列</summary>
    /// <param name="dst">目标缓冲区</param>
    /// <param name="op">目标写位置</param>
    /// <param name="source">原始数据</param>
    /// <param name="anchor">字面量起点</param>
    /// <param name="i">匹配起点</param>
    /// <param name="matchLen">匹配长度</param>
    /// <param name="offset">匹配距离</param>
    /// <returns>新的目标写位置</returns>
    private static Int32 EmitSequence(Byte[] dst, Int32 op, ReadOnlySpan<Byte> source, Int32 anchor, Int32 i, Int32 matchLen, Int32 offset)
    {
        var literalLen = i - anchor;
        var matchCode = matchLen - MinMatchLength;
        dst[op++] = (Byte)((literalLen >= 15 ? 15 : literalLen) << 4 | (matchCode >= 15 ? 15 : matchCode));

        if (literalLen >= 15) op = WriteLength(dst, op, literalLen - 15);
        if (literalLen > 0)
        {
            source.Slice(anchor, literalLen).CopyTo(dst.AsSpan(op));
            op += literalLen;
        }

        dst[op++] = (Byte)offset;
        dst[op++] = (Byte)(offset >> 8);

        if (matchCode >= 15) op = WriteLength(dst, op, matchCode - 15);

        return op;
    }

    /// <summary>写入扩展长度字节（255 累加 + 末尾余数）</summary>
    /// <param name="dst">目标缓冲区</param>
    /// <param name="op">目标写位置</param>
    /// <param name="length">扩展长度</param>
    /// <returns>新的目标写位置</returns>
    private static Int32 WriteLength(Byte[] dst, Int32 op, Int32 length)
    {
        while (length >= 255)
        {
            dst[op++] = 255;
            length -= 255;
        }

        dst[op++] = (Byte)length;
        return op;
    }

    #endregion
}

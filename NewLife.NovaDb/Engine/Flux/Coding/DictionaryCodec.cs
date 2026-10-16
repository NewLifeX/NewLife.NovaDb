using System.Text;

namespace NewLife.NovaDb.Engine.Flux.Coding;

/// <summary>字符串字典编码器，适合低基数列：先写去重字典再写各值索引，重复字符串只存一份</summary>
internal static class DictionaryCodec
{
    /// <summary>编码字符串序列，null 以索引 0 表示</summary>
    /// <param name="values">源序列</param>
    /// <returns>编码字节数组，空序列返回空数组</returns>
    public static Byte[] EncodeStrings(ReadOnlySpan<String?> values)
    {
        if (values.Length == 0) return [];

        var map = new Dictionary<String, Int32>(StringComparer.Ordinal);
        var unique = new List<String>();
        var indices = new Int32[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var s = values[i];
            if (s == null)
            {
                indices[i] = -1;
                continue;
            }

            if (!map.TryGetValue(s, out var index))
            {
                index = unique.Count;
                map[s] = index;
                unique.Add(s);
            }

            indices[i] = index;
        }

        var size = VarInt.GetSize((UInt64)unique.Count);
        var bytesList = new List<Byte[]>(unique.Count);
        foreach (var s in unique)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            bytesList.Add(bytes);
            size += VarInt.GetSize((UInt64)bytes.Length) + bytes.Length;
        }

        for (var i = 0; i < indices.Length; i++)
        {
            size += VarInt.GetSize((UInt64)(indices[i] + 1));
        }

        var buffer = new Byte[size];
        var pos = 0;
        pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)unique.Count);
        for (var i = 0; i < unique.Count; i++)
        {
            var bytes = bytesList[i];
            pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)bytes.Length);
            bytes.CopyTo(buffer, pos);
            pos += bytes.Length;
        }

        for (var i = 0; i < indices.Length; i++)
        {
            pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)(indices[i] + 1));
        }

        return buffer;
    }

    /// <summary>解码字符串序列</summary>
    /// <param name="data">编码数据</param>
    /// <param name="count">元素个数</param>
    /// <returns>解码后的数组，个数为 0 时返回空数组</returns>
    public static String?[] DecodeStrings(ReadOnlySpan<Byte> data, Int32 count)
    {
        if (count <= 0) return [];

        var pos = 0;
        var dictCount = (Int32)VarInt.Read(data, ref pos);
        var unique = new String[dictCount];
        for (var i = 0; i < dictCount; i++)
        {
            var len = (Int32)VarInt.Read(data, ref pos);
            unique[i] = Encoding.UTF8.GetString(data.Slice(pos, len));
            pos += len;
        }

        var result = new String?[count];
        for (var i = 0; i < count; i++)
        {
            var index = (Int64)VarInt.Read(data, ref pos) - 1;
            result[i] = index < 0 ? null : unique[index];
        }

        return result;
    }
}

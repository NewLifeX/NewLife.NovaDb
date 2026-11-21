using System.Globalization;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>索引键编解码器：把键按列声明类型编码为可哈希、可比较的字节串</summary>
/// <remarks>
/// 格式：每个索引列一段 <c>[长度:Int32][编码内容]</c>，长度 -1 表示 NULL。
/// 编码一律按列的声明类型进行，使 SQL 传入的 Int32/Int64 等参数归一到同一字节串，
/// 避免"比较相等但哈希不同"导致索引漏查。
/// 单列数值/时间/布尔键额外生成 8 字节可比较前缀，有序段比较时不必反复解码。
/// </remarks>
internal sealed class IndexKeyCodec
{
    private readonly IDataCodec _codec;
    private readonly DataType[] _types;
    private readonly Func<Object?, Object?, Int32>? _compare;

    /// <summary>索引列的数据类型（按索引列顺序）</summary>
    public DataType[] Types => _types;

    /// <summary>索引列数</summary>
    public Int32 PartCount => _types.Length;

    /// <summary>是否支持可比较前缀</summary>
    public Boolean HasSortPrefix => _types.Length == 1 && IsPrefixType(_types[0]);

    /// <summary>实例化键编解码器</summary>
    /// <param name="codec">值编解码器</param>
    /// <param name="types">索引列的数据类型</param>
    /// <param name="compare">键比较委托，为空时使用 <see cref="DefaultCompare"/></param>
    public IndexKeyCodec(IDataCodec codec, DataType[] types, Func<Object?, Object?, Int32>? compare = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        if (types.Length == 0) throw new ArgumentException("Index key must have at least one column", nameof(types));

        _compare = compare;
    }

    /// <summary>计算键的编码长度</summary>
    /// <param name="key">键值。单列索引传标量，联合索引传 Object[]</param>
    /// <returns>编码后的字节长度</returns>
    public Int32 GetEncodedLength(Object key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        var total = 0;
        for (var i = 0; i < _types.Length; i++)
        {
            var value = GetPartValue(key, i);

            total += sizeof(Int32);
            if (value != null)
            {
                value = Normalize(value, _types[i]);
                total += _codec.GetEncodedLength(value, _types[i]);
            }
        }

        return total;
    }

    /// <summary>编码键</summary>
    /// <param name="key">键值。单列索引传标量，联合索引传 Object[]</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <returns>写入的字节长度</returns>
    public Int32 Encode(Object key, Byte[] buffer, Int32 offset)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        var position = offset;
        for (var i = 0; i < _types.Length; i++)
        {
            var value = GetPartValue(key, i);
            if (value == null)
            {
                WriteInt32(buffer, position, -1);
                position += sizeof(Int32);
                continue;
            }

            value = Normalize(value, _types[i]);
            var length = _codec.GetEncodedLength(value, _types[i]);
            WriteInt32(buffer, position, length);
            position += sizeof(Int32);

            position += _codec.Encode(value, _types[i], buffer, position);
        }

        return position - offset;
    }

    /// <summary>解码键</summary>
    /// <param name="buffer">键字节</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="length">键字节长度</param>
    /// <returns>单列索引返回标量，联合索引返回 Object[]</returns>
    public Object? Decode(Byte[] buffer, Int32 offset, Int32 length)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || length < 0 || offset + length > buffer.Length)
            throw new NovaException(ErrorCode.FileCorrupted, "Index key range [{0},{1}) is out of buffer length {2}", offset, offset + length, buffer.Length);

        var end = offset + length;
        var position = offset;
        var parts = new Object?[_types.Length];

        for (var i = 0; i < _types.Length; i++)
        {
            if (position + sizeof(Int32) > end)
                throw new NovaException(ErrorCode.FileCorrupted, "Truncated index key at part {0}", i);

            var partLength = ReadInt32(buffer, position);
            position += sizeof(Int32);
            if (partLength < 0)
            {
                parts[i] = null;
                continue;
            }
            if (position + partLength > end)
                throw new NovaException(ErrorCode.FileCorrupted, "Index key part {0} exceeds key length", i);

            parts[i] = _codec.Decode(buffer, position, _types[i]);
            position += partLength;
        }

        return parts.Length == 1 ? parts[0] : parts;
    }

    /// <summary>尝试生成可比较前缀</summary>
    /// <param name="key">键值</param>
    /// <param name="prefix">8 字节前缀，按 Int64 升序排列</param>
    /// <returns>是否支持；不支持时调用方回退到解码比较</returns>
    public Boolean TryGetSortPrefix(Object key, out Int64 prefix)
    {
        prefix = 0;
        if (!HasSortPrefix || key == null) return false;

        var type = _types[0];
        try
        {
            switch (type)
            {
                case DataType.Boolean:
                    prefix = (Boolean)Normalize(key, type) ? 1 : 0;
                    return true;

                case DataType.Int32:
                    prefix = (Int32)Normalize(key, type);
                    return true;

                case DataType.Int64:
                    prefix = (Int64)Normalize(key, type);
                    return true;

                case DataType.DateTime:
                    prefix = ((DateTime)Normalize(key, type)).Ticks;
                    return true;

                case DataType.Double:
                    {
                        var value2 = (Double)Normalize(key, type);
                        if (Double.IsNaN(value2)) return false;

                        var bits = BitConverter.DoubleToInt64Bits(value2);

                        // 正数位序即升序；负数取反后整体下移到负数区间，与升序一致
                        prefix = bits >= 0 ? bits : unchecked(~bits + Int64.MinValue);
                        return true;
                    }

                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>比较两个已编码的键</summary>
    /// <param name="a">键 A 字节</param>
    /// <param name="aOffset">键 A 起始偏移</param>
    /// <param name="aLength">键 A 长度</param>
    /// <param name="aHasPrefix">键 A 是否有前缀</param>
    /// <param name="aPrefix">键 A 前缀</param>
    /// <param name="b">键 B 字节</param>
    /// <param name="bOffset">键 B 起始偏移</param>
    /// <param name="bLength">键 B 长度</param>
    /// <param name="bHasPrefix">键 B 是否有前缀</param>
    /// <param name="bPrefix">键 B 前缀</param>
    /// <returns>比较结果</returns>
    public Int32 Compare(Byte[] a, Int32 aOffset, Int32 aLength, Boolean aHasPrefix, Int64 aPrefix,
        Byte[] b, Int32 bOffset, Int32 bLength, Boolean bHasPrefix, Int64 bPrefix)
    {
        // 前缀只解决"明显不等"的绝大多数场景；相同时仍解码做精确比较，
        // 保证与内存跳表时代的比较语义完全一致
        if (aHasPrefix && bHasPrefix && aPrefix != bPrefix) return aPrefix < bPrefix ? -1 : 1;

        return CompareDecoded(Decode(a, aOffset, aLength), Decode(b, bOffset, bLength));
    }

    /// <summary>解码后比较</summary>
    /// <param name="left">左值</param>
    /// <param name="right">右值</param>
    /// <returns>比较结果</returns>
    public Int32 CompareDecoded(Object? left, Object? right) => _compare != null ? _compare(left, right) : DefaultCompare(left, right);

    /// <summary>稳定哈希（FNV-1a 64 + murmur3 收尾）</summary>
    /// <param name="buffer">数据缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数</param>
    /// <returns>64 位哈希</returns>
    /// <remarks>不能用 Object.GetHashCode：.NET 字符串哈希逐进程随机化，持久化后会全部失效</remarks>
    public static UInt64 Hash(Byte[] buffer, Int32 offset, Int32 count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        var hash = 14695981039346656037UL;
        for (var i = 0; i < count; i++)
        {
            hash ^= buffer[offset + i];
            hash *= 1099511628211UL;
        }

        // fmix64，打散低位，避免"低位决定桶号"时分布不均
        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CEB9FE1A85EC53UL;
        hash ^= hash >> 33;

        return hash;
    }

    /// <summary>获取键某一段的值</summary>
    /// <param name="key">键值</param>
    /// <param name="index">段序号</param>
    /// <returns>段值</returns>
    private Object? GetPartValue(Object key, Int32 index)
    {
        if (_types.Length == 1 && key is not Object?[]) return key;

        if (key is Object?[] values)
        {
            if (values.Length != _types.Length)
                throw new NovaException(ErrorCode.InvalidArgument, "Index key has {0} parts, expected {1}", values.Length, _types.Length);

            return values[index];
        }

        throw new NovaException(ErrorCode.InvalidArgument, "Index key must be Object[] for composite index");
    }

    /// <summary>把值归一到列声明类型</summary>
    /// <param name="value">原始值</param>
    /// <param name="type">列类型</param>
    /// <returns>归一后的值</returns>
    private static Object Normalize(Object value, DataType type) => type switch
    {
        DataType.Boolean when value is not Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        DataType.Int32 when value is not Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        DataType.Int64 when value is not Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        DataType.Double when value is not Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        DataType.Decimal when value is not Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        DataType.String when value is not String => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DataType.DateTime when value is not DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        _ => value
    };

    /// <summary>是否支持可比较前缀</summary>
    /// <param name="type">数据类型</param>
    /// <returns>是否支持</returns>
    private static Boolean IsPrefixType(DataType type) => type is DataType.Boolean or DataType.Int32 or DataType.Int64 or DataType.DateTime or DataType.Double;

    /// <summary>默认比较：优先 IComparable，否则按字符串比较</summary>
    /// <param name="left">左值</param>
    /// <param name="right">右值</param>
    /// <returns>比较结果</returns>
    private static Int32 DefaultCompare(Object? left, Object? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        if (left.GetType() == right.GetType() && left is IComparable sameType) return sameType.CompareTo(right);
        if (left is IComparable comparable && right is IComparable) return comparable.CompareTo(right);

        return String.Compare(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>小端写入 Int32</summary>
    private static void WriteInt32(Byte[] buffer, Int32 offset, Int32 value)
    {
        buffer[offset] = (Byte)value;
        buffer[offset + 1] = (Byte)(value >> 8);
        buffer[offset + 2] = (Byte)(value >> 16);
        buffer[offset + 3] = (Byte)(value >> 24);
    }

    /// <summary>小端读取 Int32</summary>
    private static Int32 ReadInt32(Byte[] buffer, Int32 offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
}

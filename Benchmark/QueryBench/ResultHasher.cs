using System.Globalization;
using System.Text;

namespace NewLife.QueryBench;

/// <summary>结果集哈希。排序查询按行序哈希，非排序查询按行哈希的和/异或聚合，保证跨库可比</summary>
public sealed class ResultHasher
{
    private const UInt64 Offset = 14695981039346656037UL;
    private const UInt64 Prime = 1099511628211UL;

    private readonly Boolean _ordered;
    private UInt64 _sequence = Offset;
    private UInt64 _sum;
    private UInt64 _xor;
    private Int64 _rows;

    /// <summary>实例化</summary>
    /// <param name="ordered">是否按行序哈希</param>
    public ResultHasher(Boolean ordered) => _ordered = ordered;

    /// <summary>已哈希行数</summary>
    public Int64 Rows => _rows;

    /// <summary>加入一行</summary>
    /// <param name="values"></param>
    public void Add(Object?[] values)
    {
        var rowHash = Offset;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                rowHash ^= (Byte)'|';
                rowHash *= Prime;
            }
            rowHash = AppendValue(rowHash, values[i]);
        }

        if (_ordered)
        {
            _sequence = AppendHash(_sequence, rowHash);
        }

        _sum += rowHash;
        _xor ^= rowHash;
        _rows++;
    }

    /// <summary>完成并返回 16 位十六进制哈希</summary>
    /// <returns></returns>
    public String Finish()
    {
        if (_ordered) return _sequence.ToString("x16", CultureInfo.InvariantCulture);

        var composite = String.Concat(
            _rows.ToString(CultureInfo.InvariantCulture), ":",
            _sum.ToString("x16", CultureInfo.InvariantCulture), ":",
            _xor.ToString("x16", CultureInfo.InvariantCulture));
        return Fnv(composite).ToString("x16", CultureInfo.InvariantCulture);
    }

    private static UInt64 AppendValue(UInt64 hash, Object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return AppendAscii(hash, "NULL");
            case String text:
                return AppendAscii(hash, text);
            case Int32 int32:
                return AppendInt64(hash, int32);
            case Int64 int64:
                return AppendInt64(hash, int64);
            case DateTime time:
                return AppendAscii(hash, time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            case Double number:
                return AppendAscii(hash, number.ToString("R", CultureInfo.InvariantCulture));
            case Single number:
                return AppendAscii(hash, number.ToString("R", CultureInfo.InvariantCulture));
            case Decimal number:
                return AppendAscii(hash, number.ToString(CultureInfo.InvariantCulture));
            case Boolean flag:
                return AppendAscii(hash, flag ? "1" : "0");
            default:
                return AppendAscii(hash, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL");
        }
    }

    private static UInt64 AppendHash(UInt64 hash, UInt64 value)
    {
        for (var i = 7; i >= 0; i--)
        {
            hash ^= (Byte)(value >> (i * 8));
            hash *= Prime;
        }
        return hash;
    }

    private static UInt64 AppendInt64(UInt64 hash, Int64 value)
    {
        Span<Byte> buffer = stackalloc Byte[20];
        if (!value.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture)) return hash;

        for (var i = 0; i < written; i++)
        {
            hash ^= buffer[i];
            hash *= Prime;
        }
        return hash;
    }

    private static UInt64 AppendAscii(UInt64 hash, String text)
    {
        foreach (var ch in text)
        {
            if (ch < 0x80)
            {
                hash ^= (Byte)ch;
                hash *= Prime;
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(ch.ToString()))
                {
                    hash ^= b;
                    hash *= Prime;
                }
            }
        }
        return hash;
    }

    private static UInt64 Fnv(String text)
    {
        var hash = Offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= Prime;
        }
        return hash;
    }
}

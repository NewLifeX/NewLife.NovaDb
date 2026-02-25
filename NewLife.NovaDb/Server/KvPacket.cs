using System.Text;
using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.NovaDb.Server;

/// <summary>KV 操作的二进制协议编解码工具，供服务端和客户端共用</summary>
/// <remarks>
/// 编码规则：
///   字符串 = EncodedInt(UTF8字节数) + UTF8字节
///   可空字节数组 = EncodedInt(-1)表示null / EncodedInt(0)表示空 / EncodedInt(n)+n字节
///   Boolean = 1字节 (0=false, 1=true)
///   Int32 = 4字节小端
///   Int64 = 8字节小端
///   Double = 8字节小端
/// </remarks>
internal static class KvPacket
{
    #region 编码请求

    /// <summary>编码 Set 请求</summary>
    public static IPacket EncodeSet(String tableName, String key, Byte[]? value, Int32 ttlSeconds)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var bufSize = 32 + tableBytes.Length + keyBytes.Length + (value?.Length ?? 0);
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, keyBytes);
        WriteNullableBytes(ref writer, value);
        writer.Write(ttlSeconds);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 Get / Delete / Exists 请求（tableName + key）</summary>
    public static IPacket EncodeTableKey(String tableName, String key)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var bufSize = 16 + tableBytes.Length + keyBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, keyBytes);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码仅含 tableName 的请求（GetCount / GetAllKeys / Clear）</summary>
    public static IPacket EncodeTableOnly(String tableName)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var buf = new Byte[8 + tableBytes.Length];
        var writer = new SpanWriter(buf, 0, buf.Length);
        WriteString(ref writer, tableBytes);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 SetExpire 请求（tableName + key + ttlSeconds）</summary>
    public static IPacket EncodeSetExpire(String tableName, String key, Int32 ttlSeconds)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var bufSize = 16 + tableBytes.Length + keyBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, keyBytes);
        writer.Write(ttlSeconds);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 Increment 请求（tableName + key + Int64 delta）</summary>
    public static IPacket EncodeIncrement(String tableName, String key, Int64 delta)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var bufSize = 24 + tableBytes.Length + keyBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, keyBytes);
        writer.Write(delta);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 IncrementDouble 请求（tableName + key + Double delta）</summary>
    public static IPacket EncodeIncrementDouble(String tableName, String key, Double delta)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var bufSize = 24 + tableBytes.Length + keyBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, keyBytes);
        writer.Write(delta);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 Search 请求（tableName + pattern + offset + count）</summary>
    public static IPacket EncodeSearch(String tableName, String pattern, Int32 offset, Int32 count)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        var bufSize = 24 + tableBytes.Length + patternBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, patternBytes);
        writer.Write(offset);
        writer.Write(count);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 DeleteByPattern 请求（tableName + pattern）</summary>
    public static IPacket EncodeDeleteByPattern(String tableName, String pattern)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        var bufSize = 16 + tableBytes.Length + patternBytes.Length;
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        WriteString(ref writer, patternBytes);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 GetAll 请求（tableName + keys[]）</summary>
    public static IPacket EncodeGetAll(String tableName, String[] keys)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var keyBytesArr = keys.Select(k => Encoding.UTF8.GetBytes(k)).ToArray();
        var bufSize = 16 + tableBytes.Length + 4 + keyBytesArr.Sum(b => 4 + b.Length);
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        writer.Write(keys.Length);
        foreach (var kb in keyBytesArr)
            WriteString(ref writer, kb);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 SetAll 请求（tableName + ttlSeconds + values dict）</summary>
    public static IPacket EncodeSetAll(String tableName, IDictionary<String, Byte[]?> values, Int32 ttlSeconds)
    {
        var tableBytes = Encoding.UTF8.GetBytes(tableName ?? "default");
        var kvBytesArr = values.Select(kvp => (
            Key: Encoding.UTF8.GetBytes(kvp.Key),
            Val: kvp.Value
        )).ToArray();
        var bufSize = 32 + tableBytes.Length
            + kvBytesArr.Sum(kv => 8 + kv.Key.Length + (kv.Val?.Length ?? 0));
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        WriteString(ref writer, tableBytes);
        writer.Write(ttlSeconds);
        writer.Write(values.Count);
        foreach (var (keyBytes, val) in kvBytesArr)
        {
            WriteString(ref writer, keyBytes);
            WriteNullableBytes(ref writer, val);
        }
        return new ArrayPacket(buf, 0, writer.Position);
    }

    #endregion

    #region 解码请求

    /// <summary>解码 Set 请求</summary>
    public static (String tableName, String key, Byte[]? value, Int32 ttlSeconds) DecodeSet(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        var value = ReadNullableBytes(ref reader);
        var ttlSeconds = reader.ReadInt32();
        return (tableName, key, value, ttlSeconds);
    }

    /// <summary>解码 tableName + key 请求（Get / Delete / Exists / GetExpire）</summary>
    public static (String tableName, String key) DecodeTableKey(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        return (tableName, key);
    }

    /// <summary>解码仅含 tableName 的请求</summary>
    public static String DecodeTableOnly(IPacket data)
    {
        var reader = new SpanReader(data);
        return ReadString(ref reader);
    }

    /// <summary>解码 SetExpire 请求</summary>
    public static (String tableName, String key, Int32 ttlSeconds) DecodeSetExpire(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        var ttlSeconds = reader.ReadInt32();
        return (tableName, key, ttlSeconds);
    }

    /// <summary>解码 Increment 请求</summary>
    public static (String tableName, String key, Int64 delta) DecodeIncrement(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        var delta = reader.ReadInt64();
        return (tableName, key, delta);
    }

    /// <summary>解码 IncrementDouble 请求</summary>
    public static (String tableName, String key, Double delta) DecodeIncrementDouble(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        var delta = reader.ReadDouble();
        return (tableName, key, delta);
    }

    /// <summary>解码 Search 请求</summary>
    public static (String tableName, String pattern, Int32 offset, Int32 count) DecodeSearch(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var pattern = ReadString(ref reader);
        var offset = reader.ReadInt32();
        var count = reader.ReadInt32();
        return (tableName, pattern, offset, count);
    }

    /// <summary>解码 DeleteByPattern 请求</summary>
    public static (String tableName, String pattern) DecodeDeleteByPattern(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var pattern = ReadString(ref reader);
        return (tableName, pattern);
    }

    /// <summary>解码 GetAll 请求</summary>
    public static (String tableName, String[] keys) DecodeGetAll(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var keyCount = reader.ReadInt32();
        var keys = new String[keyCount];
        for (var i = 0; i < keyCount; i++)
            keys[i] = ReadString(ref reader);
        return (tableName, keys);
    }

    /// <summary>解码 SetAll 请求</summary>
    public static (String tableName, IDictionary<String, Byte[]?> values, Int32 ttlSeconds) DecodeSetAll(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var ttlSeconds = reader.ReadInt32();
        var count = reader.ReadInt32();
        var values = new Dictionary<String, Byte[]?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString(ref reader);
            var val = ReadNullableBytes(ref reader);
            values[key] = val;
        }
        return (tableName, values, ttlSeconds);
    }

    #endregion

    #region 编码响应

    /// <summary>编码 Boolean 响应（1 字节）</summary>
    public static IPacket EncodeBoolean(Boolean value) => new ArrayPacket(new Byte[] { value ? (Byte)1 : (Byte)0 });

    /// <summary>编码 Int32 响应（4 字节小端）</summary>
    public static IPacket EncodeInt32(Int32 value)
    {
        var buf = new Byte[4];
        new SpanWriter(buf, 0, 4).Write(value);
        return new ArrayPacket(buf);
    }

    /// <summary>编码 Int64 响应（8 字节小端）</summary>
    public static IPacket EncodeInt64(Int64 value)
    {
        var buf = new Byte[8];
        new SpanWriter(buf, 0, 8).Write(value);
        return new ArrayPacket(buf);
    }

    /// <summary>编码 Double 响应（8 字节小端）</summary>
    public static IPacket EncodeDouble(Double value)
    {
        var buf = new Byte[8];
        new SpanWriter(buf, 0, 8).Write(value);
        return new ArrayPacket(buf);
    }

    /// <summary>编码可空字节数组响应（null → 1 字节 0x00；非 null → 1 字节 0x01 + 值字节）</summary>
    public static IPacket EncodeNullableValue(Byte[]? value)
    {
        if (value == null)
            return new ArrayPacket(new Byte[] { 0 });
        var result = new Byte[1 + value.Length];
        result[0] = 1;
        value.CopyTo(result, 1);
        return new ArrayPacket(result);
    }

    /// <summary>编码字符串数组响应（Int32 count + 每项 EncodedString）</summary>
    public static IPacket EncodeStringArray(String[] keys)
    {
        if (keys.Length == 0)
        {
            var emptyBuf = new Byte[4];
            new SpanWriter(emptyBuf, 0, 4).Write(0);
            return new ArrayPacket(emptyBuf, 0, 4);
        }

        var keyBytesArr = keys.Select(k => Encoding.UTF8.GetBytes(k)).ToArray();
        var bufSize = 4 + keyBytesArr.Sum(b => 4 + b.Length);
        var buf = new Byte[bufSize];
        var writer = new SpanWriter(buf, 0, bufSize);
        writer.Write(keys.Length);
        foreach (var kb in keyBytesArr)
            WriteString(ref writer, kb);
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码 GetAll 响应（Int32 keyCount + 每项 key EncodedString + value nullable bytes）</summary>
    public static IPacket EncodeGetAllResponse(String[] keys, IDictionary<String, IOwnerPacket?> data)
    {
        var keyBytesArr = keys.Select(k => Encoding.UTF8.GetBytes(k)).ToArray();
        // 预估大小：count(4) + n * (keyLen(4)+keyBytes + valueFlag(1)+valueLen(4)+valueBytes)
        var estSize = 4 + keys.Length * 16 + keyBytesArr.Sum(b => b.Length);
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var pk) && pk != null)
                estSize += pk.Length;
        }

        var buf = new Byte[estSize + 64];
        var writer = new SpanWriter(buf, 0, buf.Length);
        writer.Write(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            WriteString(ref writer, keyBytesArr[i]);
            if (data.TryGetValue(keys[i], out var pk) && pk != null)
            {
                var valueBytes = pk.ReadBytes();
                writer.WriteByte(1);
                writer.WriteEncodedInt(valueBytes.Length);
                writer.Write(valueBytes);
            }
            else
            {
                writer.WriteByte(0);
            }
        }
        return new ArrayPacket(buf, 0, writer.Position);
    }

    /// <summary>编码空响应（用于 Clear 等无返回值操作）</summary>
    public static IPacket EncodeEmpty() => new ArrayPacket(new Byte[0]);

    #endregion

    #region 解码响应

    /// <summary>解码 Boolean 响应</summary>
    public static Boolean DecodeBoolean(IPacket? pk) => pk != null && pk.Length > 0 && pk.GetSpan()[0] != 0;

    /// <summary>解码 Int32 响应</summary>
    public static Int32 DecodeInt32(IPacket? pk)
    {
        if (pk == null || pk.Length < 4) return 0;
        return new SpanReader(pk).ReadInt32();
    }

    /// <summary>解码 Int64 响应</summary>
    public static Int64 DecodeInt64(IPacket? pk)
    {
        if (pk == null || pk.Length < 8) return 0;
        return new SpanReader(pk).ReadInt64();
    }

    /// <summary>解码 Double 响应</summary>
    public static Double DecodeDouble(IPacket? pk)
    {
        if (pk == null || pk.Length < 8) return 0;
        return new SpanReader(pk).ReadDouble();
    }

    /// <summary>解码可空字节数组响应（第一字节 0=null，1=有值）</summary>
    public static Byte[]? DecodeNullableValue(IPacket? pk)
    {
        if (pk == null || pk.Length == 0) return null;
        var span = pk.GetSpan();
        if (span[0] == 0) return null;
        if (pk.Length == 1) return new Byte[0];
        return span[1..].ToArray();
    }

    /// <summary>解码字符串数组响应</summary>
    public static String[] DecodeStringArray(IPacket? pk)
    {
        if (pk == null || pk.Length < 4) return [];
        var reader = new SpanReader(pk);
        var count = reader.ReadInt32();
        if (count <= 0) return [];
        var result = new String[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadString(ref reader);
        return result;
    }

    /// <summary>解码 GetAll 响应</summary>
    public static IDictionary<String, Byte[]?> DecodeGetAllResponse(IPacket? pk)
    {
        if (pk == null || pk.Length < 4) return new Dictionary<String, Byte[]?>();
        var reader = new SpanReader(pk);
        var count = reader.ReadInt32();
        var result = new Dictionary<String, Byte[]?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString(ref reader);
            var flag = reader.ReadByte();
            if (flag != 0)
            {
                var len = reader.ReadEncodedInt();
                var valueBytes = len > 0 ? reader.ReadBytes(len).ToArray() : new Byte[0];
                result[key] = valueBytes;
            }
            else
            {
                result[key] = null;
            }
        }
        return result;
    }

    #endregion

    #region 私有辅助

    private static void WriteString(ref SpanWriter writer, Byte[] strBytes)
    {
        writer.WriteEncodedInt(strBytes.Length);
        if (strBytes.Length > 0) writer.Write(strBytes);
    }

    private static void WriteNullableBytes(ref SpanWriter writer, Byte[]? value)
    {
        if (value == null)
            writer.WriteEncodedInt(-1);
        else
        {
            writer.WriteEncodedInt(value.Length);
            if (value.Length > 0) writer.Write(value);
        }
    }

    private static String ReadString(ref SpanReader reader)
    {
        var len = reader.ReadEncodedInt();
        if (len <= 0) return String.Empty;
        return Encoding.UTF8.GetString(reader.ReadBytes(len));
    }

    private static Byte[]? ReadNullableBytes(ref SpanReader reader)
    {
        var len = reader.ReadEncodedInt();
        if (len < 0) return null;
        if (len == 0) return new Byte[0];
        return reader.ReadBytes(len).ToArray();
    }

    #endregion
}

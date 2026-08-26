using System.Text;
using NewLife.Buffers;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Utilities;

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
    private static readonly Encoding _encoding = Encoding.UTF8;

#if NET45
    private static readonly Byte[] EmptyBytes = [];
#else
    private static readonly Byte[] EmptyBytes = Array.Empty<Byte>();
#endif

    #region 编码请求

    /// <summary>编码 Set 请求</summary>
    public static IOwnerPacket EncodeSet(String tableName, String key, Byte[]? value, Int32 ttlSeconds)
    {
        var table = tableName ?? "default";
        var bufSize = 32 + _encoding.GetByteCount(table) + _encoding.GetByteCount(key) + (value?.Length ?? 0) + 8;
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(key);
        WriteNullableBytesWithFlag(ref writer, value);
        writer.Write(ttlSeconds);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 Get / Delete / Exists 请求（tableName + key）</summary>
    public static IOwnerPacket EncodeTableKey(String tableName, String key)
    {
        var table = tableName ?? "default";
        var bufSize = 16 + _encoding.GetByteCount(table) + _encoding.GetByteCount(key);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(key);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码仅含 tableName 的请求（GetCount / GetAllKeys / Clear）</summary>
    public static IOwnerPacket EncodeTableOnly(String tableName)
    {
        var table = tableName ?? "default";
        var bufSize = 8 + _encoding.GetByteCount(table);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 SetExpire 请求（tableName + key + ttlSeconds）</summary>
    public static IOwnerPacket EncodeSetExpire(String tableName, String key, Int32 ttlSeconds)
    {
        var table = tableName ?? "default";
        var bufSize = 16 + _encoding.GetByteCount(table) + _encoding.GetByteCount(key);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(key);
        writer.Write(ttlSeconds);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 Increment 请求（tableName + key + Int64 delta）</summary>
    public static IOwnerPacket EncodeIncrement(String tableName, String key, Int64 delta)
    {
        var table = tableName ?? "default";
        var bufSize = 24 + _encoding.GetByteCount(table) + _encoding.GetByteCount(key);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(key);
        writer.Write(delta);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 IncrementDouble 请求（tableName + key + Double delta）</summary>
    public static IOwnerPacket EncodeIncrementDouble(String tableName, String key, Double delta)
    {
        var table = tableName ?? "default";
        var bufSize = 24 + _encoding.GetByteCount(table) + _encoding.GetByteCount(key);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(key);
        writer.Write(delta);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 Search 请求（tableName + pattern + offset + count）</summary>
    public static IOwnerPacket EncodeSearch(String tableName, String pattern, Int32 offset, Int32 count)
    {
        var table = tableName ?? "default";
        var bufSize = 24 + _encoding.GetByteCount(table) + _encoding.GetByteCount(pattern);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(pattern);
        writer.Write(offset);
        writer.Write(count);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 DeleteByPattern 请求（tableName + pattern）</summary>
    public static IOwnerPacket EncodeDeleteByPattern(String tableName, String pattern)
    {
        var table = tableName ?? "default";
        var bufSize = 16 + _encoding.GetByteCount(table) + _encoding.GetByteCount(pattern);
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(pattern);
        return pk.Resize(writer.Position);
    }

    /// <summary>编码 GetAll 请求（tableName + keys[]）</summary>
    public static IOwnerPacket EncodeGetAll(String tableName, String[] keys)
    {
        var table = tableName ?? "default";
        var bufSize = 16 + _encoding.GetByteCount(table) + 4 + keys.Sum(k => 4 + _encoding.GetByteCount(k));
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(keys.Length);
        foreach (var key in keys)
            writer.Write(key);

        return pk.Resize(writer.Position);
    }

    /// <summary>编码 SetAll 请求（tableName + ttlSeconds + values dict）</summary>
    public static IOwnerPacket EncodeSetAll(String tableName, IDictionary<String, Byte[]?> values, Int32 ttlSeconds)
    {
        var table = tableName ?? "default";
        var valueBytesLengthTotal = values.Sum(kvp => 8 + _encoding.GetByteCount(kvp.Key) + (kvp.Value?.Length ?? 0));
        var bufSize = 32 + _encoding.GetByteCount(table) + valueBytesLengthTotal;
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(table);
        writer.Write(ttlSeconds);
        writer.Write(values.Count);
        foreach (var keyValuePair in values)
        {
            writer.Write(keyValuePair.Key);
            WriteNullableBytes(ref writer, keyValuePair.Value);
        }
        return pk.Resize(writer.Position);
    }

    #endregion

    #region 解码请求

    /// <summary>解码 Set 请求</summary>
    public static (String tableName, String key, Byte[]? value, Int32 ttlSeconds) DecodeSet(IPacket data)
    {
        var reader = new SpanReader(data);
        var tableName = ReadString(ref reader);
        var key = ReadString(ref reader);
        var value = ReadNullableBytesWithFlag(ref reader);
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

    /// <summary>缓存的 Boolean 响应包</summary>
    private static readonly IPacket TruePacket = new ArrayPacket([1]);
    private static readonly IPacket FalsePacket = new ArrayPacket([0]);

    /// <summary>缓存的空响应包</summary>
    private static readonly IPacket EmptyPacket = new ArrayPacket(EmptyBytes);

    /// <summary>编码 Boolean 响应（1 字节）</summary>
    public static IPacket EncodeBoolean(Boolean value) => value ? TruePacket : FalsePacket;

    /// <summary>编码 Int32 响应（4 字节小端）</summary>
    public static IOwnerPacket EncodeInt32(Int32 value)
    {
        var pk = new OwnerPacket(4);
        new SpanWriter(pk).Write(value);
        return pk;
    }

    /// <summary>编码 Int64 响应（8 字节小端）</summary>
    public static IOwnerPacket EncodeInt64(Int64 value)
    {
        var pk = new OwnerPacket(8);
        new SpanWriter(pk).Write(value);
        return pk;
    }

    /// <summary>编码 Double 响应（8 字节小端）</summary>
    public static IOwnerPacket EncodeDouble(Double value)
    {
        var pk = new OwnerPacket(8);
        new SpanWriter(pk).Write(value);
        return pk;
    }

    /// <summary>编码空响应（用于 Clear 等无返回值操作，以及 Get 未找到键时的空包）</summary>
    public static IPacket EncodeEmpty() => EmptyPacket;

    /// <summary>编码字符串数组响应（Int32 count + 每项 EncodedString）</summary>
    public static IOwnerPacket EncodeStringArray(String[] keys)
    {
        if (keys.Length == 0)
        {
            var emptyPk = new OwnerPacket(4);
            new SpanWriter(emptyPk).Write(0);
            return emptyPk;
        }

        var bufSize = 4 + keys.Sum(k => 4 + _encoding.GetByteCount(k));
        var pk = new OwnerPacket(bufSize);
        var writer = new SpanWriter(pk);
        writer.Write(keys.Length);
        foreach (var key in keys)
            writer.Write(key);

        return pk.Resize(writer.Position);
    }

    /// <summary>编码 GetAll 响应（Int32 keyCount + 每项 key EncodedString + value nullable bytes）</summary>
    public static IOwnerPacket EncodeGetAllResponse(String[] keys, IDictionary<String, IOwnerPacket?> data)
    {
        // 预估大小：count(4) + n * (keyLen(4)+keyBytes + valueFlag(1)+valueLen(4)+valueBytes)
        var estSize = 4 + keys.Length * 16 + keys.Sum(k => _encoding.GetByteCount(k));
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var val) && val != null)
                estSize += val.Length;
        }

        var pk = new OwnerPacket(estSize + 64);
        var writer = new SpanWriter(pk);
        writer.Write(keys.Length);
        foreach (var key in keys)
        {
            writer.Write(key);

            if (data.TryGetValue(key, out var val) && val != null)
            {
                var valueSpan = val.GetSpan();
                writer.WriteByte(1);
                writer.WriteEncodedInt(valueSpan.Length);
                writer.Write(valueSpan);
            }
            else
            {
                writer.WriteByte(0);
            }
        }
        return pk.Resize(writer.Position);
    }

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

    /// <summary>编码 Get 响应（含压缩标志）。空包语义不变（未找到）</summary>
    /// <param name="value">原始值字节</param>
    /// <returns>带压缩标志的值响应包</returns>
    public static IOwnerPacket EncodeValue(Byte[] value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        // 压缩（小数据原样返回，CompressionCodec 内部阈值判断）
        var compressed = CompressionCodec.Default.Compress(value);
        var flag = ReferenceEquals(compressed, value) ? (Byte)0 : (Byte)CompressionCodec.Default.Algorithm;

        var pk = new OwnerPacket(compressed.Length + 5);
        var writer = new SpanWriter(pk);
        writer.WriteByte(flag);
        writer.WriteEncodedInt(compressed.Length);
        writer.Write(compressed);
        return pk.Resize(writer.Position);
    }

    /// <summary>解码 Get 响应（空包=未找到；非空=[1B flag][EncodedInt n][n 字节]，flag 非 0 时解压）</summary>
    public static Byte[]? DecodeNullableValue(IPacket? pk)
    {
        if (pk == null || pk.Length == 0) return null;

        var reader = new SpanReader(pk);
        var flag = reader.ReadByte();
        var len = reader.ReadEncodedInt();
        if (len < 0) return null;
        if (len == 0) return EmptyBytes;

        var data = reader.ReadBytes(len).ToArray();
        if (flag == (Byte)CompressionAlgorithm.GZip || flag == (Byte)CompressionAlgorithm.Deflate)
            return CompressionCodec.Default.Decompress(data);

        return data;
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
                var valueBytes = len > 0 ? reader.ReadBytes(len).ToArray() : EmptyBytes;
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

    private static void WriteNullableBytesWithFlag(ref SpanWriter writer, Byte[]? value)
    {
        if (value == null)
        {
            writer.WriteEncodedInt(-1);
            return;
        }

        if (value.Length == 0)
        {
            writer.WriteEncodedInt(0);
            return;
        }

        // 压缩（小数据原样返回，CompressionCodec 内部阈值判断）
        var compressed = CompressionCodec.Default.Compress(value);
        var flag = ReferenceEquals(compressed, value) ? (Byte)0 : (Byte)CompressionCodec.Default.Algorithm;

        writer.WriteEncodedInt(compressed.Length + 1);
        writer.WriteByte(flag);
        writer.Write(compressed);
    }

    private static Byte[]? ReadNullableBytesWithFlag(ref SpanReader reader)
    {
        var len = reader.ReadEncodedInt();
        if (len < 0) return null;
        if (len == 0) return EmptyBytes;

        // 新格式：len 含 1 字节压缩标志
        var flag = reader.ReadByte();
        var dataLen = len - 1;
        var data = dataLen > 0 ? reader.ReadBytes(dataLen).ToArray() : EmptyBytes;

        if (flag == (Byte)CompressionAlgorithm.GZip || flag == (Byte)CompressionAlgorithm.Deflate)
            return CompressionCodec.Default.Decompress(data);

        return data;
    }

    private static String ReadString(ref SpanReader reader)
    {
        var len = reader.ReadEncodedInt();
        if (len <= 0) return String.Empty;
        return _encoding.GetString(reader.ReadBytes(len));
    }

    private static Byte[]? ReadNullableBytes(ref SpanReader reader)
    {
        var len = reader.ReadEncodedInt();
        if (len < 0) return null;
        if (len == 0) return EmptyBytes;
        return reader.ReadBytes(len).ToArray();
    }

#endregion
}

using System.Buffers.Binary;
using System.IO;
using System.Text;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux.Coding;
using NewLife.NovaDb.Utilities;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>时序列式块编解码器</summary>
/// <remarks>
/// 将同一分区的条目拆分为时间戳、序列号、字段、标签四类列；整数列按数据形态在
/// 原始值 / Delta / DeltaDelta / RLE 之间择优，浮点列在原始值 / XOR / RLE 之间择优，
/// 字符串列在字典 / 原始值之间择优，列负载再按需用 LZ4 压缩。
/// 稀疏列（字段与标签）通过索引列表记录存在位置，null 字段值以类型标签 0 的空负载列表示。
/// </remarks>
internal static class FluxBlockCodec
{
    #region 常量

    /// <summary>列种类：时间戳</summary>
    internal const Byte ColumnKind_Timestamp = 0;

    /// <summary>列种类：序列号</summary>
    internal const Byte ColumnKind_Sequence = 1;

    /// <summary>列种类：字段</summary>
    internal const Byte ColumnKind_Field = 2;

    /// <summary>列种类：标签</summary>
    internal const Byte ColumnKind_Tag = 3;

    /// <summary>编码：原始值序列</summary>
    internal const Byte Codec_Raw = 0;

    /// <summary>编码：一阶差分</summary>
    internal const Byte Codec_Delta = 1;

    /// <summary>编码：二阶差分</summary>
    internal const Byte Codec_DeltaDelta = 2;

    /// <summary>编码：Gorilla XOR</summary>
    internal const Byte Codec_Xor = 3;

    /// <summary>编码：游程长度</summary>
    internal const Byte Codec_Rle = 4;

    /// <summary>编码：字典</summary>
    internal const Byte Codec_Dictionary = 5;

    /// <summary>压缩：不压缩</summary>
    internal const Byte Compress_None = 0;

    /// <summary>压缩：LZ4</summary>
    internal const Byte Compress_Lz4 = 1;

    /// <summary>字段值类型标签：空值</summary>
    private const Byte TypeTag_Null = 0;

    /// <summary>字段值类型标签：字符串</summary>
    private const Byte TypeTag_String = 1;

    /// <summary>字段值类型标签：Int32</summary>
    private const Byte TypeTag_Int32 = 2;

    /// <summary>字段值类型标签：Int64</summary>
    private const Byte TypeTag_Int64 = 3;

    /// <summary>字段值类型标签：Double</summary>
    private const Byte TypeTag_Double = 4;

    /// <summary>字段值类型标签：Boolean</summary>
    private const Byte TypeTag_Boolean = 5;

    /// <summary>字段值类型标签：字节数组</summary>
    private const Byte TypeTag_Bytes = 6;

    #endregion

    #region 编码

    /// <summary>将分区条目编码为列式块负载</summary>
    /// <param name="partitionKey">分区键</param>
    /// <param name="entries">条目集合，须按时间顺序排列</param>
    /// <param name="stats">编码统计</param>
    /// <returns>块负载字节数组</returns>
    public static Byte[] Encode(String partitionKey, IList<FluxEntry> entries, out FluxBlockStats stats)
    {
        if (partitionKey == null) throw new ArgumentNullException(nameof(partitionKey));
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        var columns = BuildColumns(entries);

        var w = new PooledBufferWriter(1024);
        try
        {
            stats = new FluxBlockStats { EntryCount = entries.Count };

            WriteString(ref w, partitionKey);
            WriteVarInt(ref w, (UInt64)entries.Count);
            WriteVarInt(ref w, (UInt64)columns.Count);

            foreach (var col in columns)
            {
                w.WriteByte(col.Kind);
                if (col.Kind == ColumnKind_Field || col.Kind == ColumnKind_Tag) WriteString(ref w, col.Name);
                w.WriteByte(col.TypeTag);

                var payload = EncodeColumnValues(col, out var codec);
                var index = EncodeIndexes(col.Indexes);

                // 列负载压缩：仅在确有益处时启用
                var compressed = false;
                var stored = payload;
                if (payload.Length > 32 && Lz4Codec.TryCompress(payload, out var lz4))
                {
                    stored = lz4;
                    compressed = true;
                }

                w.WriteByte(codec);
                w.WriteByte(compressed ? Compress_Lz4 : Compress_None);
                WriteVarInt(ref w, (UInt64)index.Length);
                WriteVarInt(ref w, (UInt64)payload.Length);
                WriteVarInt(ref w, (UInt64)stored.Length);
                w.WriteBytes(index);
                w.WriteBytes(stored);

                stats.ColumnCount++;
                stats.RawPayloadBytes += index.Length + payload.Length;
                stats.StoredPayloadBytes += index.Length + stored.Length;
                if (compressed) stats.CompressedColumnCount++;
            }

            return w.Buffer.AsSpan(0, w.WrittenCount).ToArray();
        }
        finally
        {
            w.Dispose();
        }
    }

    #endregion

    #region 解码

    /// <summary>解码列式块负载</summary>
    /// <param name="data">块负载</param>
    /// <param name="partitionKey">输出分区键</param>
    /// <returns>条目列表</returns>
    public static List<FluxEntry> Decode(ReadOnlySpan<Byte> data, out String partitionKey)
    {
        var pos = 0;
        partitionKey = ReadString(data, ref pos);
        var count = (Int32)VarInt.Read(data, ref pos);
        var columnCount = (Int32)VarInt.Read(data, ref pos);

        var columns = new List<ColumnBuffer>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            var col = new ColumnBuffer { Kind = data[pos++] };
            if (col.Kind == ColumnKind_Field || col.Kind == ColumnKind_Tag) col.Name = ReadString(data, ref pos);
            col.TypeTag = data[pos++];

            var codec = data[pos++];
            var compress = data[pos++];
            var indexLength = (Int32)VarInt.Read(data, ref pos);
            var rawLength = (Int32)VarInt.Read(data, ref pos);
            var storedLength = (Int32)VarInt.Read(data, ref pos);

            var indexData = data.Slice(pos, indexLength);
            pos += indexLength;
            var stored = data.Slice(pos, storedLength);
            pos += storedLength;

            DecodeIndexes(col, indexData);
            DecodeColumnValues(col, stored, rawLength, compress, codec, count);
            columns.Add(col);
        }

        var entries = new List<FluxEntry>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(new FluxEntry());
        }

        foreach (var col in columns)
        {
            switch (col.Kind)
            {
                case ColumnKind_Timestamp:
                    for (var i = 0; i < count; i++)
                    {
                        entries[i].Timestamp = (Int64)col.Values[i]!;
                    }
                    break;
                case ColumnKind_Sequence:
                    for (var i = 0; i < count; i++)
                    {
                        entries[i].SequenceId = (Int32)col.Values[i]!;
                    }
                    break;
                case ColumnKind_Field:
                    var fi = 0;
                    foreach (var index in col.Indexes)
                    {
                        entries[index].Fields[col.Name] = col.TypeTag == TypeTag_Null ? null : col.Values[fi++];
                    }
                    break;
                case ColumnKind_Tag:
                    var ti = 0;
                    foreach (var index in col.Indexes)
                    {
                        entries[index].Tags[col.Name] = (String)col.Values[ti++]!;
                    }
                    break;
            }
        }

        return entries;
    }

    /// <summary>读取块负载的列概况，用于启动加载后重建统计</summary>
    /// <param name="data">块负载</param>
    /// <returns>列总数与启用压缩的列数</returns>
    public static (Int32 ColumnCount, Int32 CompressedCount) ReadColumnSummary(ReadOnlySpan<Byte> data)
    {
        var pos = 0;
        ReadString(data, ref pos);
        _ = VarInt.Read(data, ref pos);
        var columnCount = (Int32)VarInt.Read(data, ref pos);

        var compressed = 0;
        for (var c = 0; c < columnCount; c++)
        {
            var kind = data[pos++];
            if (kind == ColumnKind_Field || kind == ColumnKind_Tag) ReadString(data, ref pos);
            pos++; // 类型标签
            pos++; // 编码
            var compress = data[pos++];
            var indexLength = (Int32)VarInt.Read(data, ref pos);
            _ = VarInt.Read(data, ref pos);
            var storedLength = (Int32)VarInt.Read(data, ref pos);
            pos += indexLength + storedLength;

            if (compress == Compress_Lz4) compressed++;
        }

        return (columnCount, compressed);
    }

    #endregion

    #region 列构建

    /// <summary>按（种类, 名称, 类型标签）构建列，时间戳与序列号为全量列</summary>
    /// <param name="entries">条目集合</param>
    /// <returns>列集合</returns>
    private static List<ColumnBuffer> BuildColumns(IList<FluxEntry> entries)
    {
        var columns = new List<ColumnBuffer>();
        var map = new Dictionary<(Byte Kind, String Name, Byte Tag), ColumnBuffer>();

        var ts = new ColumnBuffer { Kind = ColumnKind_Timestamp, TypeTag = TypeTag_Int64 };
        var seq = new ColumnBuffer { Kind = ColumnKind_Sequence, TypeTag = TypeTag_Int32 };
        for (var i = 0; i < entries.Count; i++)
        {
            ts.Values.Add(entries[i].Timestamp);
            seq.Values.Add((Int64)entries[i].SequenceId);
        }
        columns.Add(ts);
        columns.Add(seq);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            foreach (var kv in entry.Fields)
            {
                var tag = GetTypeTag(kv.Value);
                var col = GetOrAdd(columns, map, ColumnKind_Field, kv.Key, tag);
                col.Indexes.Add(i);
                if (tag != TypeTag_Null) col.Values.Add(NormalizeValue(kv.Value, tag));
            }

            foreach (var kv in entry.Tags)
            {
                var col = GetOrAdd(columns, map, ColumnKind_Tag, kv.Key, TypeTag_String);
                col.Indexes.Add(i);
                col.Values.Add(kv.Value);
            }
        }

        return columns;
    }

    /// <summary>获取或创建列缓冲</summary>
    /// <param name="columns">列集合</param>
    /// <param name="map">列索引</param>
    /// <param name="kind">列种类</param>
    /// <param name="name">列名称</param>
    /// <param name="tag">类型标签</param>
    /// <returns>列缓冲</returns>
    private static ColumnBuffer GetOrAdd(List<ColumnBuffer> columns, Dictionary<(Byte Kind, String Name, Byte Tag), ColumnBuffer> map, Byte kind, String name, Byte tag)
    {
        if (map.TryGetValue((kind, name, tag), out var col)) return col;

        col = new ColumnBuffer { Kind = kind, Name = name, TypeTag = tag };
        map[(kind, name, tag)] = col;
        columns.Add(col);
        return col;
    }

    #endregion

    #region 列值编码

    /// <summary>按列类型择优编码值负载</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="codec">输出编码标识</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeColumnValues(ColumnBuffer col, out Byte codec)
    {
        switch (col.TypeTag)
        {
            case TypeTag_Null:
                codec = Codec_Raw;
                return [];
            case TypeTag_Int32:
            case TypeTag_Int64:
                return EncodeInt64Column(col, out codec);
            case TypeTag_Double:
                return EncodeDoubleColumn(col, out codec);
            case TypeTag_Boolean:
                return EncodeBooleanColumn(col, out codec);
            case TypeTag_Bytes:
                codec = Codec_Raw;
                return EncodeBytesColumn(col);
            default:
                return EncodeStringColumn(col, out codec);
        }
    }

    /// <summary>整数列：原始值 / Delta /（时间戳列的）DeltaDelta / RLE 择优</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="codec">输出编码标识</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeInt64Column(ColumnBuffer col, out Byte codec)
    {
        var n = col.Values.Count;
        var values = new Int64[n];
        for (var i = 0; i < n; i++)
        {
            values[i] = col.Values[i] is Int32 v32 ? v32 : (Int64)col.Values[i]!;
        }

        var best = EncodeInt64Raw(values);
        codec = Codec_Raw;

        if (col.Kind == ColumnKind_Timestamp)
        {
            var dd = DeltaDeltaCodec.EncodeInt64(values);
            if (dd.Length < best.Length)
            {
                best = dd;
                codec = Codec_DeltaDelta;
            }
        }

        var delta = DeltaCodec.EncodeInt64(values);
        if (delta.Length < best.Length)
        {
            best = delta;
            codec = Codec_Delta;
        }

        var rle = RleCodec.EncodeInt64(values);
        if (rle.Length < best.Length)
        {
            best = rle;
            codec = Codec_Rle;
        }

        return best;
    }

    /// <summary>浮点列：原始值 / XOR / RLE 择优</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="codec">输出编码标识</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeDoubleColumn(ColumnBuffer col, out Byte codec)
    {
        var n = col.Values.Count;
        var values = new Double[n];
        var bits = new Int64[n];
        var raw = new Byte[n * 8];
        for (var i = 0; i < n; i++)
        {
            values[i] = (Double)col.Values[i]!;
            bits[i] = BitConverter.DoubleToInt64Bits(values[i]);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(i * 8), bits[i]);
        }

        var best = raw;
        codec = Codec_Raw;

        var xor = XorCodec.EncodeDouble(values);
        if (xor.Length < best.Length)
        {
            best = xor;
            codec = Codec_Xor;
        }

        var rle = RleCodec.EncodeInt64(bits);
        if (rle.Length < best.Length)
        {
            best = rle;
            codec = Codec_Rle;
        }

        return best;
    }

    /// <summary>布尔列：原始值 / RLE 择优</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="codec">输出编码标识</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeBooleanColumn(ColumnBuffer col, out Byte codec)
    {
        var n = col.Values.Count;
        var values = new Boolean[n];
        var raw = new Byte[n];
        for (var i = 0; i < n; i++)
        {
            values[i] = (Boolean)col.Values[i]!;
            raw[i] = values[i] ? (Byte)1 : (Byte)0;
        }

        var best = raw;
        codec = Codec_Raw;

        var rle = RleCodec.EncodeBoolean(values);
        if (rle.Length < best.Length)
        {
            best = rle;
            codec = Codec_Rle;
        }

        return best;
    }

    /// <summary>字符串列：原始值 / 字典择优</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="codec">输出编码标识</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeStringColumn(ColumnBuffer col, out Byte codec)
    {
        var n = col.Values.Count;
        var values = new String?[n];
        var rawSize = 0;
        for (var i = 0; i < n; i++)
        {
            values[i] = (String?)col.Values[i];
            rawSize += VarInt.GetSize((UInt64)Encoding.UTF8.GetByteCount(values[i] ?? String.Empty)) + Encoding.UTF8.GetByteCount(values[i] ?? String.Empty);
        }

        var w = new PooledBufferWriter(rawSize + 16);
        try
        {
            foreach (var s in values)
            {
                var bytes = Encoding.UTF8.GetBytes(s ?? String.Empty);
                WriteVarInt(ref w, (UInt64)bytes.Length);
                w.WriteBytes(bytes);
            }

            var best = w.Buffer.AsSpan(0, w.WrittenCount).ToArray();
            codec = Codec_Raw;

            var dict = DictionaryCodec.EncodeStrings(values);
            if (dict.Length < best.Length)
            {
                best = dict;
                codec = Codec_Dictionary;
            }

            return best;
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>字节数组列：长度前缀原始序列</summary>
    /// <param name="col">列缓冲</param>
    /// <returns>值负载</returns>
    private static Byte[] EncodeBytesColumn(ColumnBuffer col)
    {
        var w = new PooledBufferWriter(256);
        try
        {
            foreach (var item in col.Values)
            {
                var bytes = (Byte[]?)item ?? [];
                WriteVarInt(ref w, (UInt64)bytes.Length);
                w.WriteBytes(bytes);
            }

            return w.Buffer.AsSpan(0, w.WrittenCount).ToArray();
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>原始整数编码：逐值 ZigZag 变长</summary>
    /// <param name="values">整数序列</param>
    /// <returns>编码结果</returns>
    private static Byte[] EncodeInt64Raw(ReadOnlySpan<Int64> values)
    {
        var size = 0;
        foreach (var v in values)
        {
            size += VarInt.GetSize(ZigZag.Encode(v));
        }

        var buffer = new Byte[size];
        var pos = 0;
        foreach (var v in values)
        {
            pos += VarInt.Write(buffer.AsSpan(pos), ZigZag.Encode(v));
        }

        return buffer;
    }

    /// <summary>稀疏列索引编码：首值绝对值 + 后续间隔变长</summary>
    /// <param name="indexes">索引序列</param>
    /// <returns>编码结果</returns>
    private static Byte[] EncodeIndexes(List<Int32> indexes)
    {
        if (indexes.Count == 0) return [];

        var size = VarInt.GetSize((UInt64)indexes[0]);
        for (var i = 1; i < indexes.Count; i++)
        {
            size += VarInt.GetSize((UInt64)(indexes[i] - indexes[i - 1]));
        }

        var buffer = new Byte[size];
        var pos = 0;
        pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)indexes[0]);
        for (var i = 1; i < indexes.Count; i++)
        {
            pos += VarInt.Write(buffer.AsSpan(pos), (UInt64)(indexes[i] - indexes[i - 1]));
        }

        return buffer;
    }

    #endregion

    #region 列值解码

    /// <summary>解码稀疏列索引：首值绝对值 + 后续间隔变长</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="data">索引数据，空表示全量列</param>
    private static void DecodeIndexes(ColumnBuffer col, ReadOnlySpan<Byte> data)
    {
        if (data.Length == 0) return;

        var pos = 0;
        var current = (Int32)VarInt.Read(data, ref pos);
        col.Indexes.Add(current);
        while (pos < data.Length)
        {
            current += (Int32)VarInt.Read(data, ref pos);
            col.Indexes.Add(current);
        }
    }

    /// <summary>解码列值负载</summary>
    /// <param name="col">列缓冲</param>
    /// <param name="stored">存储负载</param>
    /// <param name="rawLength">压缩前长度</param>
    /// <param name="compress">压缩标识</param>
    /// <param name="codec">编码标识</param>
    /// <param name="entryCount">块内条目总数，全量列按此取值</param>
    private static void DecodeColumnValues(ColumnBuffer col, ReadOnlySpan<Byte> stored, Int32 rawLength, Byte compress, Byte codec, Int32 entryCount)
    {
        var payload = stored;
        if (compress == Compress_Lz4)
        {
            if (!Lz4Codec.TryDecompress(stored, rawLength, out var raw)) throw new InvalidDataException("Flux 列负载解压失败");
            payload = raw;
        }

        if (col.Kind != ColumnKind_Timestamp && col.Kind != ColumnKind_Sequence && col.TypeTag == TypeTag_Null) return;

        var count = col.Kind == ColumnKind_Timestamp || col.Kind == ColumnKind_Sequence ? entryCount : col.Indexes.Count;
        switch (col.TypeTag)
        {
            case TypeTag_Int32:
                foreach (var v in DecodeInt64Column(payload, count, codec))
                {
                    col.Values.Add((Int32)v);
                }
                break;
            case TypeTag_Int64:
                foreach (var v in DecodeInt64Column(payload, count, codec))
                {
                    col.Values.Add(v);
                }
                break;
            case TypeTag_Double:
                foreach (var v in DecodeDoubleColumn(payload, count, codec))
                {
                    col.Values.Add(v);
                }
                break;
            case TypeTag_Boolean:
                foreach (var v in DecodeBooleanColumn(payload, count, codec))
                {
                    col.Values.Add(v);
                }
                break;
            case TypeTag_Bytes:
                var bp = 0;
                for (var i = 0; i < count; i++)
                {
                    var len = (Int32)VarInt.Read(payload, ref bp);
                    col.Values.Add(payload.Slice(bp, len).ToArray());
                    bp += len;
                }
                break;
            default:
                foreach (var v in DecodeStringColumn(payload, count, codec))
                {
                    col.Values.Add(v);
                }
                break;
        }
    }

    /// <summary>解码整数列</summary>
    /// <param name="payload">值负载</param>
    /// <param name="count">值数量</param>
    /// <param name="codec">编码标识</param>
    /// <returns>整数数组</returns>
    private static Int64[] DecodeInt64Column(ReadOnlySpan<Byte> payload, Int32 count, Byte codec)
    {
        switch (codec)
        {
            case Codec_Delta:
                return DeltaCodec.DecodeInt64(payload, count);
            case Codec_DeltaDelta:
                return DeltaDeltaCodec.DecodeInt64(payload, count);
            case Codec_Rle:
                return RleCodec.DecodeInt64(payload, count);
            default:
                var result = new Int64[count];
                var pos = 0;
                for (var i = 0; i < count; i++)
                {
                    result[i] = ZigZag.Decode(VarInt.Read(payload, ref pos));
                }
                return result;
        }
    }

    /// <summary>解码浮点列</summary>
    /// <param name="payload">值负载</param>
    /// <param name="count">值数量</param>
    /// <param name="codec">编码标识</param>
    /// <returns>浮点数组</returns>
    private static Double[] DecodeDoubleColumn(ReadOnlySpan<Byte> payload, Int32 count, Byte codec)
    {
        switch (codec)
        {
            case Codec_Xor:
                return XorCodec.DecodeDouble(payload, count);
            case Codec_Rle:
                var bits = RleCodec.DecodeInt64(payload, count);
                var rleValues = new Double[count];
                for (var i = 0; i < count; i++)
                {
                    rleValues[i] = BitConverter.Int64BitsToDouble(bits[i]);
                }
                return rleValues;
            default:
                var result = new Double[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8)));
                }
                return result;
        }
    }

    /// <summary>解码布尔列</summary>
    /// <param name="payload">值负载</param>
    /// <param name="count">值数量</param>
    /// <param name="codec">编码标识</param>
    /// <returns>布尔数组</returns>
    private static Boolean[] DecodeBooleanColumn(ReadOnlySpan<Byte> payload, Int32 count, Byte codec)
    {
        if (codec == Codec_Rle) return RleCodec.DecodeBoolean(payload, count);

        var result = new Boolean[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = payload[i] != 0;
        }

        return result;
    }

    /// <summary>解码字符串列</summary>
    /// <param name="payload">值负载</param>
    /// <param name="count">值数量</param>
    /// <param name="codec">编码标识</param>
    /// <returns>字符串数组</returns>
    private static String?[] DecodeStringColumn(ReadOnlySpan<Byte> payload, Int32 count, Byte codec)
    {
        if (codec == Codec_Dictionary) return DictionaryCodec.DecodeStrings(payload, count);

        var result = new String?[count];
        var pos = 0;
        for (var i = 0; i < count; i++)
        {
            var len = (Int32)VarInt.Read(payload, ref pos);
            result[i] = len == 0 ? String.Empty : ReadUtf8(payload.Slice(pos, len));
            pos += len;
        }

        return result;
    }

    #endregion

    #region 辅助

    /// <summary>获取字段值类型标签，与 AOF 行式记录保持一致</summary>
    /// <param name="value">字段值</param>
    /// <returns>类型标签</returns>
    private static Byte GetTypeTag(Object? value) => value switch
    {
        null => TypeTag_Null,
        Int32 => TypeTag_Int32,
        Int64 => TypeTag_Int64,
        Double => TypeTag_Double,
        Boolean => TypeTag_Boolean,
        Byte[] => TypeTag_Bytes,
        _ => TypeTag_String,
    };

    /// <summary>归一化字段值，非基础类型统一转为字符串（与 AOF 行式记录一致）</summary>
    /// <param name="value">字段值</param>
    /// <param name="tag">类型标签</param>
    /// <returns>归一化后的值</returns>
    private static Object? NormalizeValue(Object? value, Byte tag) => tag == TypeTag_String && value is not String ? value?.ToString() ?? String.Empty : value;

    /// <summary>测量行式 AOF 记录体积，用于压缩率对比</summary>
    /// <param name="entry">时序条目</param>
    /// <returns>行式记录的估算字节数</returns>
    public static Int64 MeasureRowBytes(FluxEntry entry)
    {
        var size = 9L + 8 + 4 + 4; // 记录头 + 时间戳 + 序列号 + 字段数量
        foreach (var kv in entry.Fields)
        {
            var tag = GetTypeTag(kv.Value);
            size += 4 + Encoding.UTF8.GetByteCount(kv.Key) + 1;
            size += tag switch
            {
                TypeTag_Int32 => 4,
                TypeTag_Int64 or TypeTag_Double => 8,
                TypeTag_Boolean => 1,
                TypeTag_Bytes => 4 + (kv.Value is Byte[] b ? b.Length : 0),
                TypeTag_String => 4 + Encoding.UTF8.GetByteCount(NormalizeValue(kv.Value, tag)?.ToString() ?? String.Empty),
                _ => 0,
            };
        }

        size += 4; // 标签数量
        foreach (var kv in entry.Tags)
        {
            size += 4 + Encoding.UTF8.GetByteCount(kv.Key) + 4 + Encoding.UTF8.GetByteCount(kv.Value);
        }

        return size;
    }

    /// <summary>写入变长整数</summary>
    /// <param name="w">写入器</param>
    /// <param name="value">数值</param>
    private static void WriteVarInt(ref PooledBufferWriter w, UInt64 value)
    {
        Span<Byte> tmp = stackalloc Byte[10];
        var n = VarInt.Write(tmp, value);
        w.WriteBytes(tmp.Slice(0, n));
    }

    /// <summary>写入 UTF-8 字符串（变长长度前缀）</summary>
    /// <param name="w">写入器</param>
    /// <param name="value">字符串</param>
    private static void WriteString(ref PooledBufferWriter w, String value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(ref w, (UInt64)bytes.Length);
        w.WriteBytes(bytes);
    }

    /// <summary>读取 UTF-8 字符串（变长长度前缀）</summary>
    /// <param name="data">数据</param>
    /// <param name="position">读取位置，读取后自动前进</param>
    /// <returns>字符串</returns>
    private static String ReadString(ReadOnlySpan<Byte> data, ref Int32 position)
    {
        var len = (Int32)VarInt.Read(data, ref position);
        if (len <= 0) return String.Empty;
        var s = ReadUtf8(data.Slice(position, len));
        position += len;
        return s;
    }

    /// <summary>按 UTF-8 解码字符串</summary>
    /// <param name="data">字节数据</param>
    /// <returns>字符串</returns>
    private static String ReadUtf8(ReadOnlySpan<Byte> data)
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return Encoding.UTF8.GetString(data);
#else
        return Encoding.UTF8.GetString(data.ToArray());
#endif
    }

    #endregion

    #region 内部类型

    /// <summary>列缓冲：既用于编码收集，也用于解码输出</summary>
    private sealed class ColumnBuffer
    {
        /// <summary>列种类</summary>
        public Byte Kind { get; set; }

        /// <summary>列名称（字段名或标签键）</summary>
        public String Name { get; set; } = String.Empty;

        /// <summary>类型标签</summary>
        public Byte TypeTag { get; set; }

        /// <summary>值在块内的位置索引</summary>
        public List<Int32> Indexes { get; } = [];

        /// <summary>列值；时间戳与序列号为 Int64，Int32 列按 Int32 存放</summary>
        public List<Object?> Values { get; } = [];
    }

    #endregion
}

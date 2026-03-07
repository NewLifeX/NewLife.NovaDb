using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using NewLife.Buffers;
using NewLife.NovaDb.Utilities;
using NewLife.Security;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>FluxEngine 数据持久化逻辑</summary>
/// <remarks>
/// 采用 Append-Only Log (AOF) 方式持久化时序数据。
/// 每次 Append 写入一条 FluxAppend 记录，DeleteExpiredPartitions 写入一条 FluxPurge 记录。
/// 启动时顺序回放所有记录，重建内存分区索引。
/// 
/// 记录格式：
/// [RecordLength: 4B] [RecordType: 1B] [Data: variable] [Checksum: 4B]
/// 
/// FluxAppend Data = [Timestamp: 8B] [SequenceId: 4B] [FieldCount: 4B] [{FieldEntry}...] [TagCount: 4B] [{TagEntry}...]
/// FieldEntry = [KeyLen: 4B] [Key: UTF-8] [TypeTag: 1B] [ValueLen: 4B (仅非固定长度)] [Value]
/// TagEntry = [KeyLen: 4B] [Key: UTF-8] [ValueLen: 4B] [Value: UTF-8]
/// FluxPurge Data = [CutoffKey: 10B UTF-8 yyyyMMddHH]
/// </remarks>
public partial class FluxEngine
{
    private const Byte RecordType_FluxAppend = 1;
    private const Byte RecordType_FluxPurge = 2;

    private static readonly Encoding _encoding = Encoding.UTF8;
    private static readonly Byte[] FluxLogMagic = [(Byte)'N', (Byte)'F', (Byte)'L', (Byte)'G'];
    private const Int32 FluxLogHeaderSize = 32;

    /// <summary>字段值类型标签</summary>
    private const Byte TypeTag_Null = 0;
    private const Byte TypeTag_String = 1;
    private const Byte TypeTag_Int32 = 2;
    private const Byte TypeTag_Int64 = 3;
    private const Byte TypeTag_Double = 4;
    private const Byte TypeTag_Boolean = 5;
    private const Byte TypeTag_Bytes = 6;

    private FileStream? _fluxLogStream;

    #region 文件管理

    /// <summary>获取时序日志文件路径</summary>
    private String GetFluxLogPath() => Path.Combine(_basePath, "flux.rlog");

    /// <summary>打开时序日志文件</summary>
    private void OpenFluxLog()
    {
        var path = GetFluxLogPath();
        var isNew = !File.Exists(path) || new FileInfo(path).Length < FluxLogHeaderSize;

        _fluxLogStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        if (isNew)
        {
            WriteFluxLogHeader();
        }
        else
        {
            ValidateFluxLogHeader();
            LoadFromFluxLog();
        }
    }

    /// <summary>写入文件头</summary>
    private void WriteFluxLogHeader()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[FluxLogHeaderSize];
        FluxLogMagic.AsSpan().CopyTo(header.Slice(0, 4));
#else
        var header = new Byte[FluxLogHeaderSize];
        FluxLogMagic.AsSpan().CopyTo(header.AsSpan(0, 4));
#endif

        header[4] = 1; // Version

        _fluxLogStream!.Position = 0;

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        _fluxLogStream.Write(header);
#else
        _fluxLogStream.Write(header, 0, header.Length);
#endif

        _fluxLogStream.Flush();
    }

    /// <summary>校验文件头</summary>
    private void ValidateFluxLogHeader()
    {
        if (_fluxLogStream!.Length < FluxLogHeaderSize) return;

        _fluxLogStream.Position = 0;
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[FluxLogHeaderSize];
        if (_fluxLogStream.Read(header) < FluxLogHeaderSize) return;
#else
        var header = new Byte[FluxLogHeaderSize];
        if (_fluxLogStream.Read(header, 0, header.Length) < FluxLogHeaderSize) return;
#endif
        if (header[0] != FluxLogMagic[0] || header[1] != FluxLogMagic[1] ||
            header[2] != FluxLogMagic[2] || header[3] != FluxLogMagic[3])
            throw new InvalidOperationException("Invalid Flux log file header");
    }

    #endregion

    #region 持久化写入

    /// <summary>持久化 Append 记录</summary>
    /// <param name="entry">时序条目</param>
    private void PersistFluxAppend(FluxEntry entry)
    {
        if (_fluxLogStream == null) return;

        // 初始容量给个经验值：头部(8+4) + fields/tags 数量 + 少量字符串
        // 不够会自动扩容（扩容也不会产生 GC 垃圾，走 ArrayPool）
        var w = new PooledBufferWriter(initialCapacity: 1024);
        try
        {
            w.WriteInt64(entry.Timestamp);
            w.WriteInt32(entry.SequenceId);

            // Fields
            var fields = entry.Fields;
            w.WriteInt32(fields.Count);
            foreach (var kv in fields)
            {
                WriteString(ref w, kv.Key);
                WriteFieldValue(ref w, kv.Value);
            }

            // Tags
            var tags = entry.Tags;
            w.WriteInt32(tags.Count);
            foreach (var kv in tags)
            {
                WriteString(ref w, kv.Key);
                WriteString(ref w, kv.Value);
            }

            WriteFluxRecord(1, w.Buffer, 0, w.WrittenCount);
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>持久化 Purge 记录（删除过期分区）</summary>
    /// <param name="cutoffKey">截止分区键</param>
    private void PersistFluxPurge(String cutoffKey)
    {
        if (_fluxLogStream == null) return;

        using var pooledBytes = _encoding.GetPooledEncodedBytes(cutoffKey);
        WriteFluxRecord(RecordType_FluxPurge, pooledBytes.Buffer, 0, pooledBytes.Length);
    }

    private void WriteFluxRecord(Byte recordType, Byte[] data, Int32 offset, Int32 count)
    {
        // recordLength: [recordType(1)] + [payload(count)] + [crc32(4)]
        var recordLength = checked(1 + count + 4);
        var totalLength = checked(4 + recordLength); // length prefix + record

        Byte[]? rented = null;
        var buffer = totalLength <= 1024
            ? stackalloc Byte[totalLength]
            : (rented = ArrayPool<Byte>.Shared.Rent(totalLength)).AsSpan(0, totalLength);

        try
        {
            // 写 recordLength (Little Endian)
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), recordLength);

            // 写 recordType
            buffer[4] = recordType;

            // 写 payload
            data.AsSpan(offset, count).CopyTo(buffer.Slice(5, count));

            // 计算 CRC32：覆盖 [recordType + payload]，也就是 buffer[4..(5+count))
            var checksum = Crc32.Compute(buffer.Slice(4, 1 + count));

            // 写 CRC32 (Little Endian)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(5 + count, 4), checksum);

            // 写入到底层流
            _fluxLogStream!.Seek(0, SeekOrigin.End);
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
            _fluxLogStream.Write(buffer); // .NET Standard 2.1+ 有 Span 重载；低版本见下面兼容写法
#else
            if (rented is not null)
            {
                _fluxLogStream.Write(rented, 0, totalLength);
            }
            else
            {
                var tempBuffer = ArrayPool<Byte>.Shared.Rent(totalLength);
                try
                {
                    buffer.CopyTo(tempBuffer.AsSpan(0, totalLength));
                    _fluxLogStream.Write(tempBuffer, 0, totalLength);
                }
                finally
                {
                    ArrayPool<Byte>.Shared.Return(tempBuffer);
                }
            }
#endif
            _fluxLogStream.Flush();
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Byte>.Shared.Return(rented);
        }
    }

    #endregion

    #region 字段序列化

    /// <summary>写入 UTF-8 字符串（长度前缀）</summary>
    private static void WriteString(ref PooledBufferWriter w, String value)
    {
        using var bytes = value.ToPooledUtf8Bytes();
        w.WriteInt32(bytes.Length);
        w.WriteBytes(bytes.AsSpan());
    }

    /// <summary>读取 UTF-8 字符串（长度前缀）</summary>
    private static String ReadString(BinaryReader br)
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len);
        return _encoding.GetString(bytes);
    }

    /// <summary>写入字段值（带类型标签）</summary>
    private static void WriteFieldValue(ref PooledBufferWriter w, Object? value)
    {
        switch (value)
        {
            case null:
                w.WriteByte(TypeTag_Null);
                break;
            case Int32 i:
                w.WriteByte(TypeTag_Int32);
                w.WriteInt32(i);
                break;
            case Int64 l:
                w.WriteByte(TypeTag_Int64);
                w.WriteInt64(l);
                break;
            case Double d:
                w.WriteByte(TypeTag_Double);
                w.WriteDouble(d);
                break;
            case Boolean b:
                w.WriteByte(TypeTag_Boolean);
                w.WriteBool(b);
                break;
            case Byte[] bytes:
                w.WriteByte(TypeTag_Bytes);
                w.WriteInt32(bytes.Length);
                w.WriteBytes(bytes);
                break;
            default:
                // 其他类型统一转为 String
                w.WriteByte(TypeTag_String);
                WriteString(ref w, value.ToString() ?? String.Empty);
                break;
        }
    }

    /// <summary>读取字段值（带类型标签）</summary>
    private static Object? ReadFieldValue(BinaryReader br)
    {
        var tag = br.ReadByte();
        switch (tag)
        {
            case TypeTag_Null:
                return null;
            case TypeTag_Int32:
                return br.ReadInt32();
            case TypeTag_Int64:
                return br.ReadInt64();
            case TypeTag_Double:
                return br.ReadDouble();
            case TypeTag_Boolean:
                return br.ReadBoolean();
            case TypeTag_Bytes:
                var len = br.ReadInt32();
                return br.ReadBytes(len);
            case TypeTag_String:
            default:
                return ReadString(br);
        }
    }

    #endregion

    #region 启动恢复

    /// <summary>从日志文件恢复数据</summary>
    private void LoadFromFluxLog()
    {
        if (_fluxLogStream == null) return;

        _fluxLogStream.Position = FluxLogHeaderSize;

        // 复用长度前缀缓冲区
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> lenBuf = stackalloc Byte[4];
#else
        var lenBuf = new Byte[4];
#endif
        // 复用记录体缓冲区
        var bodyBuf = ArrayPool<Byte>.Shared.Rent(4096);
        try
        {
            while (_fluxLogStream.Position < _fluxLogStream.Length)
            {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
                if (_fluxLogStream.Read(lenBuf) < 4) break;
                var recordLength = BitConverter.ToInt32(lenBuf);
#else
                if (_fluxLogStream.Read(lenBuf, 0, 4) < 4) break;
                var recordLength = BitConverter.ToInt32(lenBuf, 0);
#endif
                if (recordLength < 5) break;

                // 确保缓冲区足够大
                if (bodyBuf.Length < recordLength)
                {
                    ArrayPool<Byte>.Shared.Return(bodyBuf);
                    bodyBuf = ArrayPool<Byte>.Shared.Rent(recordLength);
                }

                if (_fluxLogStream.Read(bodyBuf, 0, recordLength) < recordLength) break;

                var recordType = bodyBuf[0];
                var dataLength = recordLength - 1 - 4;
                if (dataLength < 0) break;

                // 校验 CRC32
                var expectedChecksum = BitConverter.ToUInt32(bodyBuf, recordLength - 4);
                var actualChecksum = Crc32.Compute(bodyBuf, 0, 1 + dataLength);
                if (expectedChecksum != actualChecksum) continue;

                if (recordType == RecordType_FluxAppend && dataLength >= 12)
                {
                    ReplayFluxAppend(bodyBuf, 1, dataLength);
                }
                else if (recordType == RecordType_FluxPurge && dataLength > 0)
                {
                    ReplayFluxPurge(bodyBuf, 1, dataLength);
                }
            }
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(bodyBuf);
        }
    }

    /// <summary>回放 Append 记录</summary>
    private void ReplayFluxAppend(Byte[] body, Int32 offset, Int32 dataLength)
    {
        var reader = new SpanReader(body, offset, dataLength);

        var entry = new FluxEntry
        {
            Timestamp = reader.ReadInt64(),
            SequenceId = reader.ReadInt32()
        };

        // Fields
        var fieldCount = reader.ReadInt32();
        for (var i = 0; i < fieldCount; i++)
        {
            var key = ReadSpanString(ref reader);
            var value = ReadSpanFieldValue(ref reader, body);
            entry.Fields[key] = value;
        }

        // Tags
        var tagCount = reader.ReadInt32();
        for (var i = 0; i < tagCount; i++)
        {
            var key = ReadSpanString(ref reader);
            var value = ReadSpanString(ref reader);
            entry.Tags[key] = value;
        }

        // 加入分区（不重新分配 SequenceId，直接还原）
        var partKey = GetPartitionKey(entry.Timestamp);
        if (!_partitions.TryGetValue(partKey, out var list))
        {
            list = [];
            _partitions[partKey] = list;
        }
        list.Add(entry);
    }

    /// <summary>使用 SpanReader 读取 UTF-8 字符串（长度前缀）</summary>
    private static String ReadSpanString(ref SpanReader reader)
    {
        var len = reader.ReadInt32();
        if (len <= 0) return String.Empty;
        var bytes = reader.ReadBytes(len);
        return _encoding.GetString(bytes);
    }

    /// <summary>使用 SpanReader 读取字段值（带类型标签）</summary>
    private static Object? ReadSpanFieldValue(ref SpanReader reader, Byte[] body)
    {
        var tag = reader.ReadByte();
        switch (tag)
        {
            case TypeTag_Null:
                return null;
            case TypeTag_Int32:
                return reader.ReadInt32();
            case TypeTag_Int64:
                return reader.ReadInt64();
            case TypeTag_Double:
                return reader.ReadDouble();
            case TypeTag_Boolean:
                return reader.ReadByte() != 0;
            case TypeTag_Bytes:
                var len = reader.ReadInt32();
                return reader.ReadBytes(len).ToArray();
            case TypeTag_String:
            default:
                return ReadSpanString(ref reader);
        }
    }

    /// <summary>回放 Purge 记录</summary>
    private void ReplayFluxPurge(Byte[] body, Int32 offset, Int32 dataLength)
    {
        var cutoffKey = _encoding.GetString(body, offset, dataLength);

        var toRemove = new List<String>();
        foreach (var key in _partitions.Keys)
        {
            if (String.Compare(key, cutoffKey, StringComparison.Ordinal) < 0)
                toRemove.Add(key);
        }

        foreach (var key in toRemove)
        {
            _partitions.Remove(key);
        }
    }

    /// <summary>压缩时序日志（重写仅包含存活条目的日志）</summary>
    public void CompactFluxLog()
    {
        if (_fluxLogStream == null) return;

        lock (_lock)
        {
            _fluxLogStream.SetLength(FluxLogHeaderSize);
            _fluxLogStream.Position = FluxLogHeaderSize;

            foreach (var list in _partitions.Values)
            {
                foreach (var entry in list)
                {
                    PersistFluxAppend(entry);
                }
            }

            _fluxLogStream.Flush();
        }
    }

    /// <summary>截断时序日志（清空所有数据，仅保留文件头）</summary>
    private void TruncateFluxLog()
    {
        if (_fluxLogStream == null) return;

        _fluxLogStream.SetLength(FluxLogHeaderSize);
        _fluxLogStream.Position = FluxLogHeaderSize;
        _fluxLogStream.Flush();
    }

    /// <summary>关闭时序日志文件</summary>
    internal void CloseFluxLog()
    {
        _fluxLogStream?.Dispose();
        _fluxLogStream = null;
    }

    #endregion
}

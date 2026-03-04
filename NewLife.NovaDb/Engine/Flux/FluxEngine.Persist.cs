using System.Text;
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
        var header = new Byte[FluxLogHeaderSize];
        Array.Copy(FluxLogMagic, 0, header, 0, 4);
        header[4] = 1; // Version

        _fluxLogStream!.Position = 0;
        _fluxLogStream.Write(header, 0, header.Length);
        _fluxLogStream.Flush();
    }

    /// <summary>校验文件头</summary>
    private void ValidateFluxLogHeader()
    {
        if (_fluxLogStream!.Length < FluxLogHeaderSize) return;

        _fluxLogStream.Position = 0;
        var header = new Byte[FluxLogHeaderSize];
        if (_fluxLogStream.Read(header, 0, header.Length) < FluxLogHeaderSize) return;

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

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(entry.Timestamp);
        bw.Write(entry.SequenceId);

        // Fields
        var fields = entry.Fields;
        bw.Write(fields.Count);
        foreach (var kv in fields)
        {
            WriteString(bw, kv.Key);
            WriteFieldValue(bw, kv.Value);
        }

        // Tags
        var tags = entry.Tags;
        bw.Write(tags.Count);
        foreach (var kv in tags)
        {
            WriteString(bw, kv.Key);
            WriteString(bw, kv.Value);
        }

        var data = ms.ToArray();
        WriteFluxRecord(RecordType_FluxAppend, data, 0, data.Length);
    }

    /// <summary>持久化 Purge 记录（删除过期分区）</summary>
    /// <param name="cutoffKey">截止分区键</param>
    private void PersistFluxPurge(String cutoffKey)
    {
        if (_fluxLogStream == null) return;

        using var pooledBytes = _encoding.GetPooledEncodedBytes(cutoffKey);
        WriteFluxRecord(RecordType_FluxPurge, pooledBytes.Buffer, 0, pooledBytes.Length);
    }

    /// <summary>写入一条记录</summary>
    private void WriteFluxRecord(Byte recordType, Byte[] data, int offset, int count)
    {
        var recordLength = 1 + count + 4;

        using var ms = new MemoryStream(4 + recordLength);
        using var bw = new BinaryWriter(ms);

        bw.Write(recordLength);
        bw.Write(recordType);
        bw.Write(data, offset, count);

        // CRC32 校验
        var checkBuffer = new Byte[1 + count];
        checkBuffer[0] = recordType;
        Array.Copy(data, offset, checkBuffer, 1, count);
        var checksum = Crc32.Compute(checkBuffer, 0, checkBuffer.Length);
        bw.Write(checksum);

        var buffer = ms.ToArray();
        _fluxLogStream!.Position = _fluxLogStream.Length;
        _fluxLogStream.Write(buffer, 0, buffer.Length);
        _fluxLogStream.Flush();
    }

    #endregion

    #region 字段序列化

    /// <summary>写入 UTF-8 字符串（长度前缀）</summary>
    private static void WriteString(BinaryWriter bw, String value)
    {
        using var bytes = _encoding.GetPooledEncodedBytes(value);
        bw.Write(bytes.Length);
#if NETSTANDARD2_1_OR_GREATER
        bw.Write(bytes.AsSpan());
#else
        bw.Write(bytes.Buffer, 0, bytes.Length);
#endif
    }

    /// <summary>读取 UTF-8 字符串（长度前缀）</summary>
    private static String ReadString(BinaryReader br)
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len);
        return _encoding.GetString(bytes);
    }

    /// <summary>写入字段值（带类型标签）</summary>
    private static void WriteFieldValue(BinaryWriter bw, Object? value)
    {
        switch (value)
        {
            case null:
                bw.Write(TypeTag_Null);
                break;
            case Int32 i:
                bw.Write(TypeTag_Int32);
                bw.Write(i);
                break;
            case Int64 l:
                bw.Write(TypeTag_Int64);
                bw.Write(l);
                break;
            case Double d:
                bw.Write(TypeTag_Double);
                bw.Write(d);
                break;
            case Boolean b:
                bw.Write(TypeTag_Boolean);
                bw.Write(b);
                break;
            case Byte[] bytes:
                bw.Write(TypeTag_Bytes);
                bw.Write(bytes.Length);
                bw.Write(bytes);
                break;
            default:
                // 其他类型统一转为 String
                bw.Write(TypeTag_String);
                WriteString(bw, value.ToString() ?? "");
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

        while (_fluxLogStream.Position < _fluxLogStream.Length)
        {
            var lenBuf = new Byte[4];
            if (_fluxLogStream.Read(lenBuf, 0, 4) < 4) break;
            var recordLength = BitConverter.ToInt32(lenBuf, 0);
            if (recordLength < 5) break;

            var body = new Byte[recordLength];
            if (_fluxLogStream.Read(body, 0, recordLength) < recordLength) break;

            var recordType = body[0];
            var dataLength = recordLength - 1 - 4;
            if (dataLength < 0) break;

            // 校验 CRC32
            var expectedChecksum = BitConverter.ToUInt32(body, recordLength - 4);
            var actualChecksum = Crc32.Compute(body, 0, 1 + dataLength);
            if (expectedChecksum != actualChecksum) continue;

            if (recordType == RecordType_FluxAppend && dataLength >= 12)
            {
                ReplayFluxAppend(body, 1, dataLength);
            }
            else if (recordType == RecordType_FluxPurge && dataLength > 0)
            {
                ReplayFluxPurge(body, 1, dataLength);
            }
        }
    }

    /// <summary>回放 Append 记录</summary>
    private void ReplayFluxAppend(Byte[] body, Int32 offset, Int32 dataLength)
    {
        using var ms = new MemoryStream(body, offset, dataLength);
        using var br = new BinaryReader(ms);

        var entry = new FluxEntry
        {
            Timestamp = br.ReadInt64(),
            SequenceId = br.ReadInt32()
        };

        // Fields
        var fieldCount = br.ReadInt32();
        for (var i = 0; i < fieldCount; i++)
        {
            var key = ReadString(br);
            var value = ReadFieldValue(br);
            entry.Fields[key] = value;
        }

        // Tags
        var tagCount = br.ReadInt32();
        for (var i = 0; i < tagCount; i++)
        {
            var key = ReadString(br);
            var value = ReadString(br);
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

using System.Buffers;
using System.Buffers.Binary;
using NewLife.Buffers;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Tx;
using NewLife.NovaDb.Utilities;
using NewLife.Security;

namespace NewLife.NovaDb.Engine;

/// <summary>NovaTable 数据持久化逻辑</summary>
/// <remarks>
/// 采用 Append-Only Row Log 方式持久化行数据。
/// 每次 Insert/Update 写入一条 Put 记录（完整行序列化），Delete 写入一条 Delete 记录（仅主键编码）。
/// 启动时顺序回放所有记录，重建内存 SkipList 索引。
/// 
/// 记录格式：
/// [RecordLength: 4B] [RecordType: 1B] [Data: variable] [Checksum: 4B]
/// 
/// Put 记录的 Data = 完整行序列化（含所有列，主键可从中提取）
/// Delete 记录的 Data = 主键编码（通过 IDataCodec 编码）
/// </remarks>
public partial class NovaTable
{
    /// <summary>Put 记录类型</summary>
    private const Byte RecordType_Put = 1;

    /// <summary>Delete 记录类型</summary>
    private const Byte RecordType_Delete = 2;

    /// <summary>行日志文件头魔数</summary>
    private static readonly Byte[] RowLogMagic = [(Byte)'N', (Byte)'R', (Byte)'L', (Byte)'G'];

    /// <summary>行日志文件头大小</summary>
    private const Int32 RowLogHeaderSize = 32;

    /// <summary>行日志文件流</summary>
    private FileStream? _rowLogStream;

    /// <summary>行载荷读取器（内存映射窗口，按需映射，不把整份行日志读进内存）</summary>
    private RowStore? _rowStore;

    #region 行日志文件管理

    /// <summary>获取行日志文件路径</summary>
    /// <returns>行日志文件路径</returns>
    private String GetRowLogPath() => Path.Combine(_dbPath, $"{_schema.TableName}.rlog");

    /// <summary>打开行日志文件，如果文件已存在则加载数据</summary>
    private void OpenRowLog()
    {
        var path = GetRowLogPath();
        var isNew = !File.Exists(path) || new FileInfo(path).Length < RowLogHeaderSize;

        _rowLogStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _rowStore = new RowStore(_rowLogStream);

        if (isNew)
        {
            // 写入文件头
            WriteRowLogHeader();
        }
        else
        {
            // 校验文件头
            ValidateRowLogHeader();

            // 回放日志恢复数据
            LoadFromRowLog();
        }
    }

    /// <summary>写入行日志文件头（32 字节）</summary>
    private void WriteRowLogHeader()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[RowLogHeaderSize];
        RowLogMagic.AsSpan().CopyTo(header.Slice(0, 4));
#else
        var header = new Byte[RowLogHeaderSize];
        RowLogMagic.AsSpan().CopyTo(header.AsSpan(0, 4));
#endif

        header[4] = 1; // Version = 1

        _rowLogStream!.Position = 0;

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        _rowLogStream.Write(header);
#else
        _rowLogStream.Write(header, 0, header.Length);
#endif

        _rowLogStream.Flush();
    }

    /// <summary>校验行日志文件头</summary>
    private void ValidateRowLogHeader()
    {
        if (_rowLogStream!.Length < RowLogHeaderSize) return;

        _rowLogStream.Position = 0;

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[RowLogHeaderSize];
        var read = _rowLogStream.Read(header);
#else
        var header = new Byte[RowLogHeaderSize];
        var read = _rowLogStream.Read(header, 0, header.Length);
#endif

        if (read < RowLogHeaderSize) return;

        // 校验魔数
        if (header[0] != RowLogMagic[0] || header[1] != RowLogMagic[1] ||
            header[2] != RowLogMagic[2] || header[3] != RowLogMagic[3])
            throw new NovaException(ErrorCode.FileCorrupted, "Invalid row log file header for table '{0}'", _schema.TableName);
    }

    #endregion

    #region 持久化写入

    /// <summary>持久化 Put 记录（Insert 或 Update 的新版本）</summary>
    /// <param name="payload">完整行序列化数据</param>
    /// <param name="version">对应的行版本，写入后记录载荷偏移并释放内存副本；为空时载荷保持驻留内存</param>
    private void PersistPut(Byte[] payload, RowVersion? version = null)
    {
        if (_rowLogStream == null) return;

        var offset = WriteRecord(RecordType_Put, payload);

        // 载荷已落盘，改由行日志按偏移读取，避免行数增长时托管堆线性膨胀
        if (version != null && offset > 0)
        {
            version.DataOffset = offset;
            version.DataLength = payload.Length;
            version.Payload = null;
        }
    }

    /// <summary>持久化 Delete 记录</summary>
    /// <param name="key">主键值</param>
    private void PersistDelete(Object key)
    {
        if (_rowLogStream == null) return;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var keyBytes = _codec.Encode(key, pkCol.DataType);

        WriteRecord(RecordType_Delete, keyBytes);
    }

    /// <summary>写入一条记录到行日志</summary>
    /// <param name="recordType">记录类型</param>
    /// <param name="data">记录数据</param>
    /// <returns>记录数据的起始偏移（5B 记录头之后），失败返回 0</returns>
    private Int64 WriteRecord(Byte recordType, Byte[] data) => WriteRecordTo(_rowLogStream!, recordType, data);

    /// <summary>向指定文件流追加一条记录</summary>
    /// <param name="stream">目标文件流</param>
    /// <param name="recordType">记录类型</param>
    /// <param name="data">记录数据</param>
    /// <returns>记录数据的起始偏移（5B 记录头之后）</returns>
    private static Int64 WriteRecordTo(FileStream stream, Byte recordType, Byte[] data)
    {
        // 格式：[RecordLength: 4B] [RecordType: 1B] [Data: variable] [Checksum: 4B]
        // RecordLength = 1 + data.Length + 4（不含 RecordLength 自身）
        var recordLength = checked(1 + data.Length + 4);
        var totalLength = checked(4 + recordLength); // 4B length prefix + record

        using var w = new PooledBufferWriter(initialCapacity: totalLength);

        // 1) 写 RecordLength
        w.WriteInt32(recordLength);

        // 2) 写 RecordType
        w.WriteByte(recordType);

        // 3) 写 Data
        w.WriteBytes(data.AsSpan());

        // 4) CRC32 覆盖 [RecordType + Data]
        //    这里直接对 writer 内部缓冲切片计算，不再构造 checkBuffer
        //    recordType 位于 offset=4，长度=1+data.Length
        var checksum = Crc32.Compute(w.Buffer.AsSpan(4, 1 + data.Length));

        // 5) 写 Checksum（小端）
        w.WriteUInt32(checksum);

        // 6) 直接追加写入文件
        var recordStart = stream.Length;
        stream.Position = recordStart;
        stream.Write(w.Buffer, 0, w.WrittenCount);
        stream.Flush();

        return recordStart + 5;
    }

    #endregion

    #region 启动恢复

    /// <summary>从行日志文件加载数据，恢复内存索引</summary>
    /// <remarks>载荷不读入内存，只记录（偏移，长度）；主键用键值对象去重，避免为每行分配临时字符串</remarks>
    private void LoadFromRowLog()
    {
        if (_rowLogStream == null) return;

        _rowLogStream.Position = RowLogHeaderSize;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        // 主键 → 存活版本的载荷位置；删除记录移除对应项
        var liveRows = new Dictionary<ComparableObject, RowVersion>();

        // 单缓冲区：读取长度前缀 + 数据体
        var buf = ArrayPool<Byte>.Shared.Rent(4096);
        try
        {
            while (_rowLogStream.Position < _rowLogStream.Length)
            {
                var recordStart = _rowLogStream.Position;

                if (!_rowLogStream.TryReadLengthPrefixedBlock(ref buf, out var recordLength)) break;
                if (recordLength < 5) break; // 至少 1B type + 4B checksum

                var recordType = buf[0];
                var dataLength = recordLength - 1 - 4;
                if (dataLength < 0) break;

                // 校验 CRC32
                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(recordLength - 4));
                var actualChecksum = Crc32.Compute(buf, 0, 1 + dataLength);
                if (expectedChecksum != actualChecksum) continue; // CRC 不匹配，跳过损坏记录

                if (recordType == RecordType_Put && dataLength > 0)
                {
                    // 只解析主键，载荷留在文件里按需映射读取
                    var row = DeserializeRow(buf, 1, dataLength);
                    var pkValue = row[pkCol.Ordinal];
                    if (pkValue == null) continue;

                    var version = new RowVersion(0, pkValue, null)
                    {
                        DataOffset = recordStart + 5,
                        DataLength = dataLength,
                    };
                    liveRows[new ComparableObject(pkValue)] = version;
                }
                else if (recordType == RecordType_Delete && dataLength > 0)
                {
                    // 直接从 buf 解码主键，无需分配 keyData
                    var pkValue = _codec.Decode(buf, 1, pkCol.DataType);
                    if (pkValue == null) continue;

                    liveRows.Remove(new ComparableObject(pkValue));
                }
            }
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buf);
        }

        // 将存活的行加载到内存索引：每个主键只保留最后一版
        foreach (var kv in liveRows)
        {
            var versions = new List<RowVersion>(1) { kv.Value };
            _primaryIndex.Insert(kv.Key, versions);
        }
    }

    /// <summary>截断行日志文件（清空所有记录，仅保留文件头）</summary>
    private void TruncateRowLog()
    {
        if (_rowLogStream == null) return;

        // 有映射视图时不能缩短文件（Windows 报 ERROR_USER_MAPPED_FILE），先释放映射再截断
        _rowStore?.Reset();

        _rowLogStream.SetLength(RowLogHeaderSize);
        _rowLogStream.Position = RowLogHeaderSize;
        _rowLogStream.Flush();
    }

    /// <summary>压缩行日志（重写仅包含存活行的日志）</summary>
    /// <remarks>
    /// 遍历当前内存中的所有存活行，重写日志文件以消除删除和旧版本记录。
    /// 载荷已按偏移存放在行日志里，因此先写到临时文件再整体替换，避免边读边截断自身；
    /// 替换后统一更新各版本的载荷偏移。
    /// </remarks>
    public void CompactRowLog()
    {
        if (_rowLogStream == null) return;

        lock (_lock)
        {
            var path = GetRowLogPath();
            var tempPath = path + ".compact";

            // 收集要保留的版本（每个主键一个最新存活版本）
            var keeps = new List<RowVersion>();
            foreach (var entry in _primaryIndex.GetAll())
            {
                foreach (var ver in entry.Value)
                {
                    if (ver.DeletedByTx == 0 && (ver.Payload != null || ver.DataOffset > 0))
                    {
                        keeps.Add(ver);
                        break; // 每个 key 只写最新可见版本
                    }
                }
            }

            var offsets = new Int64[keeps.Count];
            var lengths = new Int32[keeps.Count];

            // 先写临时文件，逐条把载荷搬过去并记录新偏移
            using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var header = new Byte[RowLogHeaderSize];
                RowLogMagic.AsSpan().CopyTo(header.AsSpan(0, 4));
                header[4] = 1;
                temp.Write(header, 0, header.Length);

                for (var i = 0; i < keeps.Count; i++)
                {
                    var ver = keeps[i];
                    var payload = ver.Payload ?? _rowStore!.Read(ver.DataOffset, ver.DataLength);
                    offsets[i] = WriteRecordTo(temp, RecordType_Put, payload);
                    lengths[i] = payload.Length;
                }
            }

            // 替换文件并重新打开读写流与映射
            _rowStore?.Dispose();
            _rowLogStream.Dispose();
            File.Delete(path);
            File.Move(tempPath, path);

            _rowLogStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _rowStore = new RowStore(_rowLogStream);
            _rowLogStream.Position = _rowLogStream.Length;

            // 更新载荷偏移，释放内存副本
            for (var i = 0; i < keeps.Count; i++)
            {
                keeps[i].DataOffset = offsets[i];
                keeps[i].DataLength = lengths[i];
                keeps[i].Payload = null;
            }
        }
    }

    /// <summary>向指定文件流追加一条记录</summary>
    /// <param name="stream">目标文件流</param>
    /// <param name="recordType">记录类型</param>
    /// <param name="data">记录数据</param>
    /// <returns>记录数据的起始偏移（5B 记录头之后）</returns>
    private static Int64 AppendRecord(FileStream stream, Byte recordType, Byte[] data)
    {
        var recordLength = checked(1 + data.Length + 4);
        var totalLength = checked(4 + recordLength);

        using var writer = new PooledBufferWriter(initialCapacity: totalLength);
        writer.WriteInt32(recordLength);
        writer.WriteByte(recordType);
        writer.WriteBytes(data.AsSpan());

        var checksum = Crc32.Compute(writer.Buffer.AsSpan(4, 1 + data.Length));
        writer.WriteUInt32(checksum);

        var recordStart = stream.Length;
        stream.Position = recordStart;
        stream.Write(writer.Buffer, 0, writer.WrittenCount);
        stream.Flush();

        return recordStart + 5;
    }

    #endregion
}

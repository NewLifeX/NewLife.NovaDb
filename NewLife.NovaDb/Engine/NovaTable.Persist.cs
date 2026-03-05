using System.Buffers.Binary;
using NewLife.NovaDb.Core;
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
            throw new NovaException(ErrorCode.FileCorrupted, $"Invalid row log file header for table '{_schema.TableName}'");
    }

    #endregion

    #region 持久化写入

    /// <summary>持久化 Put 记录（Insert 或 Update 的新版本）</summary>
    /// <param name="payload">完整行序列化数据</param>
    private void PersistPut(Byte[] payload)
    {
        if (_rowLogStream == null) return;

        WriteRecord(RecordType_Put, payload);
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
    private void WriteRecord(Byte recordType, Byte[] data)
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
        _rowLogStream!.Position = _rowLogStream.Length;
        _rowLogStream.Write(w.Buffer, 0, w.WrittenCount);
        _rowLogStream.Flush();
    }

    #endregion

    #region 启动恢复

    /// <summary>从行日志文件加载数据，恢复内存索引</summary>
    private void LoadFromRowLog()
    {
        if (_rowLogStream == null) return;

        _rowLogStream.Position = RowLogHeaderSize;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        // 中间状态：记录每个主键最后的操作（true=存在, false=已删除）
        var liveRows = new Dictionary<String, Byte[]>();
        var deletedKeys = new HashSet<String>();

        while (_rowLogStream.Position < _rowLogStream.Length)
        {
            // 读取 RecordLength
            var lenBuf = new Byte[4];
            if (_rowLogStream.Read(lenBuf, 0, 4) < 4) break;
            var recordLength = BitConverter.ToInt32(lenBuf, 0);

            if (recordLength < 5) break; // 至少 1B type + 4B checksum

            // 读取记录体
            var body = new Byte[recordLength];
            var read = _rowLogStream.Read(body, 0, recordLength);
            if (read < recordLength) break; // 截断记录，忽略

            var recordType = body[0];
            var dataLength = recordLength - 1 - 4;
            if (dataLength < 0) break;

            // 校验 CRC32
            var expectedChecksum = BitConverter.ToUInt32(body, recordLength - 4);
            var actualChecksum = Crc32.Compute(body, 0, 1 + dataLength);
            if (expectedChecksum != actualChecksum) continue; // CRC 不匹配，跳过损坏记录

            if (recordType == RecordType_Put && dataLength > 0)
            {
                var payload = new Byte[dataLength];
                Array.Copy(body, 1, payload, 0, dataLength);

                // 反序列化行以提取主键
                var row = DeserializeRow(payload);
                var pkValue = row[pkCol.Ordinal];
                if (pkValue == null) continue;

                var keyStr = pkValue.ToString()!;
                liveRows[keyStr] = payload;
                deletedKeys.Remove(keyStr);
            }
            else if (recordType == RecordType_Delete && dataLength > 0)
            {
                var keyData = new Byte[dataLength];
                Array.Copy(body, 1, keyData, 0, dataLength);

                // 反序列化主键
                var pkValue = _codec.Decode(keyData, 0, pkCol.DataType);
                if (pkValue == null) continue;

                var keyStr = pkValue.ToString()!;
                liveRows.Remove(keyStr);
                deletedKeys.Add(keyStr);
            }
        }

        // 将存活的行加载到内存索引
        foreach (var kv in liveRows)
        {
            var row = DeserializeRow(kv.Value);
            var pkValue = row[pkCol.Ordinal];
            if (pkValue == null) continue;

            var comparableKey = new ComparableObject(pkValue);

            // 创建已提交的行版本（txId=0 表示持久化的已提交数据）
            var rowVersion = new Tx.RowVersion(0, pkValue, kv.Value);

            if (!_primaryIndex.TryGetValue(comparableKey, out var versions))
            {
                versions = [];
                _primaryIndex.Insert(comparableKey, versions);
            }
            versions!.Add(rowVersion);
        }
    }

    /// <summary>截断行日志文件（清空所有记录，仅保留文件头）</summary>
    private void TruncateRowLog()
    {
        if (_rowLogStream == null) return;

        _rowLogStream.SetLength(RowLogHeaderSize);
        _rowLogStream.Position = RowLogHeaderSize;
        _rowLogStream.Flush();
    }

    /// <summary>压缩行日志（重写仅包含存活行的日志）</summary>
    /// <remarks>遍历当前内存中的所有存活行，重写日志文件以消除删除和旧版本记录</remarks>
    public void CompactRowLog()
    {
        if (_rowLogStream == null) return;

        lock (_lock)
        {
            // 截断文件
            _rowLogStream.SetLength(RowLogHeaderSize);
            _rowLogStream.Position = RowLogHeaderSize;

            // 重写所有存活行
            var allEntries = _primaryIndex.GetAll();
            foreach (var entry in allEntries)
            {
                foreach (var ver in entry.Value)
                {
                    // 只写入未删除的版本
                    if (ver.DeletedByTx == 0 && ver.Payload != null)
                    {
                        WriteRecord(RecordType_Put, ver.Payload);
                        break; // 每个 key 只写最新可见版本
                    }
                }
            }

            _rowLogStream.Flush();
        }
    }

    #endregion
}

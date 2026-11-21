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

    /// <summary>行日志逻辑长度。缓冲写入未刷新时 FileStream.Length 会滞后，追加位置以本字段为准</summary>
    private Int64 _rowLogLength;

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
        _rowLogLength = _rowLogStream.Length;
        _rowStore = new RowStore(_rowLogStream, () => _rowLogLength);

        if (isNew)
        {
            // 写入文件头
            WriteRowLogHeader();
        }
        else
        {
            // 校验文件头
            ValidateRowLogHeader();
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
        _rowLogLength = RowLogHeaderSize;
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

    /// <summary>写入一条记录到行日志</summary>
    /// <param name="recordType">记录类型</param>
    /// <param name="data">记录数据</param>
    /// <returns>记录数据的起始偏移（5B 记录头之后），失败返回 0</returns>
    private Int64 WriteRecord(Byte recordType, Byte[] data)
    {
        var offset = WriteRecordTo(_rowLogStream!, _rowLogLength, recordType, data, out var end);
        _rowLogLength = end;
        return offset;
    }

    #endregion

    #region 启动恢复

    /// <summary>打开主键索引；文件缺失、损坏或偏移落后时按行日志整表重建</summary>
    private void OpenPrimaryIndex()
    {
        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var hashPath = _fileManager.GetPrimaryHashIndexFilePath();
        var orderedPath = _fileManager.GetPrimaryOrderedIndexFilePath();
        var pkType = pkCol.DataType;

        if (_options.ReadOnly)
        {
            _primaryIndex = NovaPrimaryIndex.Open(hashPath, orderedPath, _codec, pkType, readOnly: true);
            if (_primaryIndex.HashAppliedOffset != _rowLogLength || _primaryIndex.OrderedAppliedOffset != _rowLogLength)
                throw new NovaException(ErrorCode.FileCorrupted, "Primary index is out of date, reopen in read-write mode to rebuild: {0}", _schema.TableName);

            return;
        }

        try
        {
            _primaryIndex = NovaPrimaryIndex.Open(hashPath, orderedPath, _codec, pkType);
            if (_primaryIndex.HashAppliedOffset == _rowLogLength && _primaryIndex.OrderedAppliedOffset == _rowLogLength) return;

            // 偏移落后说明上次没有正常关闭：整表重建，避免把半批记录当成已应用
            _primaryIndex.Dispose();
        }
        catch (Exception ex) when (ex is NovaException or IOException or InvalidOperationException)
        {
            // 文件缺失或损坏：走重建
        }

        _primaryIndex = NovaPrimaryIndex.Recreate(hashPath, orderedPath, _codec, pkType);
        RebuildPrimaryIndex();
    }

    /// <summary>按行日志重建主键索引</summary>
    /// <remarks>
    /// 先顺序回放行日志得到"每个主键的最后存活版本"，再一次性写入索引：
    /// Read Committed 只读最新已提交版本，重建不需要保留历史版本；
    /// 这样也避免同一批次里先插入后更新时无法解析被替换版本的问题。
    /// </remarks>
    private void RebuildPrimaryIndex()
    {
        if (_rowLogStream == null) return;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var liveRows = new Dictionary<ComparableObject, RowVersion>();
        var buf = ArrayPool<Byte>.Shared.Rent(4096);

        try
        {
            _rowLogStream.Position = RowLogHeaderSize;

            while (_rowLogStream.Position < _rowLogLength)
            {
                var recordStart = _rowLogStream.Position;

                if (!_rowLogStream.TryReadLengthPrefixedBlock(ref buf, out var recordLength)) break;
                if (recordLength < 5) break;

                var recordType = buf[0];
                var dataLength = recordLength - 1 - 4;
                if (dataLength < 0) break;

                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(recordLength - 4));
                if (expectedChecksum != Crc32.Compute(buf, 0, 1 + dataLength)) continue;

                if (recordType == RecordType_Put && dataLength > 0)
                {
                    var row = DeserializeRow(buf, 1, dataLength, null);
                    var pkValue = row[pkCol.Ordinal];
                    if (pkValue == null) continue;

                    var version = new RowVersion(NaturalTransactionId(recordStart), pkValue, null)
                    {
                        DataOffset = recordStart + 5,
                        DataLength = dataLength,
                    };
                    liveRows[new ComparableObject(pkValue)] = version;
                }
                else if (recordType == RecordType_Delete && dataLength > 0)
                {
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

        var commits = new List<PrimaryCommit>(1024);
        foreach (var kv in liveRows)
        {
            commits.Add(new PrimaryCommit(kv.Key.Value, kv.Value, null, kv.Value.CreatedByTx));

            if (commits.Count >= 1024)
            {
                _primaryIndex.Apply(commits, -1);
                commits.Clear();
            }
        }

        _primaryIndex.Apply(commits, _rowLogLength);
        _primaryIndex.MarkApplied(_rowLogLength);
        _primaryIndex.Flush();
    }

    /// <summary>由行日志偏移派生重建用的事务号，避免与真实事务号冲突</summary>
    /// <param name="recordStart">记录起始偏移</param>
    /// <returns>事务号</returns>
    private static UInt64 NaturalTransactionId(Int64 recordStart) => (1UL << 63) | (UInt64)recordStart;

    /// <summary>截断行日志文件（清空所有记录，仅保留文件头）</summary>
    private void TruncateRowLog()
    {
        if (_rowLogStream == null) return;

        // 有映射视图时不能缩短文件（Windows 报 ERROR_USER_MAPPED_FILE），先释放映射再截断
        _rowStore?.Reset();

        _rowLogStream.SetLength(RowLogHeaderSize);
        _rowLogStream.Position = RowLogHeaderSize;
        _rowLogStream.Flush();
        _rowLogLength = RowLogHeaderSize;
    }

    /// <summary>压缩行日志（重写仅包含存活行的日志）</summary>
    /// <remarks>
    /// 遍历主键索引中的所有存活行，重写日志文件以消除删除和旧版本记录。
    /// 载荷已按偏移存放在行日志里，因此先写到临时文件再整体替换，避免边读边截断自身；
    /// 替换后主键索引中的载荷偏移全部失效，必须按新日志重建。
    /// </remarks>
    public void CompactRowLog()
    {
        if (_rowLogStream == null) return;

        lock (_lock)
        {
            if (_pendingIndex.Count > 0)
                throw new NovaException(ErrorCode.TransactionError, "Cannot compact row log while transactions have uncommitted changes");

            var path = GetRowLogPath();
            var tempPath = path + ".compact";

            // 收集要保留的版本（每个主键一个存活版本）
            var keeps = new List<RowVersion>();
            _primaryIndex.Scan(null, true, null, true, false, 0, (_, ver) =>
            {
                if (ver.DeletedByTx == 0 && (ver.Payload != null || ver.DataOffset > 0)) keeps.Add(ver);

                return true;
            });

            // 先写临时文件，逐条把载荷搬过去
            using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var header = new Byte[RowLogHeaderSize];
                RowLogMagic.AsSpan().CopyTo(header.AsSpan(0, 4));
                header[4] = 1;
                temp.Write(header, 0, header.Length);
                var tempLength = (Int64)RowLogHeaderSize;

                for (var i = 0; i < keeps.Count; i++)
                {
                    var ver = keeps[i];
                    var payload = ver.Payload ?? _rowStore!.Read(ver.DataOffset, ver.DataLength);
                    WriteRecordTo(temp, tempLength, RecordType_Put, payload, out tempLength);
                }
            }

            // 替换文件并重新打开读写流与映射
            _rowStore?.Dispose();
            _rowLogStream.Dispose();
            File.Delete(path);
            File.Move(tempPath, path);

            _rowLogStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _rowLogLength = _rowLogStream.Length;
            _rowStore = new RowStore(_rowLogStream, () => _rowLogLength);
            _rowLogStream.Position = _rowLogStream.Length;

            // 旧索引里的载荷偏移已失效，按新日志重建主键索引
            _primaryIndex.Clear();
            RebuildPrimaryIndex();
        }
    }

    /// <summary>向指定文件流追加一条记录</summary>
    /// <param name="stream">目标文件流</param>
    /// <param name="startOffset">记录写入的起始偏移（调用方维护的逻辑长度）</param>
    /// <param name="recordType">记录类型</param>
    /// <param name="data">记录数据</param>
    /// <param name="endOffset">写入后的文件逻辑长度</param>
    /// <returns>记录数据的起始偏移（5B 记录头之后）</returns>
    /// <remarks>不逐条刷新文件：一次提交只在最后刷新一次，否则每条记录都是一次系统调用</remarks>
    private static Int64 WriteRecordTo(FileStream stream, Int64 startOffset, Byte recordType, Byte[] data, out Int64 endOffset)
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

        // 6) 直接追加写入文件（顺序追加时不重设位置，避免 Position 设置触发缓冲区刷新）
        if (stream.Position != startOffset) stream.Position = startOffset;
        stream.Write(w.Buffer, 0, w.WrittenCount);

        endOffset = startOffset + w.WrittenCount;

        return startOffset + 5;
    }

    #endregion
}

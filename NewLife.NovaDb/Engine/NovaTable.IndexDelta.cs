using System.Buffers;
using System.Buffers.Binary;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Utilities;
using NewLife.Security;

namespace NewLife.NovaDb.Engine;

/// <summary>NovaTable 二级索引增量持久化逻辑</summary>
/// <remarks>
/// 基线文件 {Table}_{Index}.idx 由 <see cref="PersistSecondaryIndex"/> 全量重写，代价与索引条目数成正比。
/// 逐行写入时每次都重写整份基线会造成 O(N²) 写放大，因此提交时只把本次操作产生的变更追加到增量日志
/// {Table}_{Index}.idxl：
///
/// 文件结构 = [FileHeader: 32B] + 若干定长头/变长数据记录：
/// [RecordLength: 4B][RecordType: 1B][KeyLength: 4B][Key][PkLength: 4B][Pk][Checksum: 4B]
/// 其中 RecordLength = 1 + 4 + KeyLength + 4 + PkLength + 4（不含 RecordLength 自身），
/// Checksum 为 RecordType 至 Pk 的 CRC32。
///
/// 启动时先用基线恢复索引，再顺序回放增量；基线缺失/版本不符/增量头部或记录损坏（含尾部截断）时，
/// 以行日志为准全表重建并丢弃增量文件。
/// 增量记录数超过 max(1000, 基线条目数/2) 或 <see cref="Dispose"/> 时，用基线全量重写合并增量并删除增量文件。
/// 兼容性：基线 .idx 格式（版本 2）保持不变；旧版本程序只读取基线、忽略 .idxl，属于已知限制。
/// </remarks>
public partial class NovaTable
{
    /// <summary>二级索引增量日志格式版本</summary>
    private const Byte SecondaryIndexDeltaFileVersion = 1;

    /// <summary>增量记录类型：Put（把主键加入索引键对应的列表）</summary>
    private const Byte SecondaryIndexDeltaRecordPut = 1;

    /// <summary>增量记录类型：Delete（从索引键对应的列表移除主键）</summary>
    private const Byte SecondaryIndexDeltaRecordDelete = 2;

    /// <summary>增量压缩的阈值下限，避免条目很少的索引频繁重写基线</summary>
    private const Int32 SecondaryIndexDeltaMinCompactionRecords = 1000;

    // 二级索引增量：索引名 → 自上次写基线以来追加的记录数
    private readonly Dictionary<String, Int32> _secondaryIndexDeltaCounts = new(StringComparer.OrdinalIgnoreCase);

    // 二级索引基线条目数：索引名 → 最近一次写基线时的条目数，用于计算压缩阈值
    private readonly Dictionary<String, Int32> _secondaryIndexBaselineCounts = new(StringComparer.OrdinalIgnoreCase);

    // 二级索引增量文件句柄：索引名 → 已打开的 .idxl 流
    private readonly Dictionary<String, FileStream> _secondaryIndexDeltaStreams = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>二级索引增量条目（提交时追加到 .idxl 的一条 Put 或 Delete）</summary>
    private readonly struct SecondaryIndexDeltaEntry
    {
        /// <summary>索引名称</summary>
        public readonly String IndexName;

        /// <summary>是否为 Put（true 加入索引，false 移出索引）</summary>
        public readonly Boolean IsPut;

        /// <summary>索引键值（已规范化的字符串或联合索引键数组）</summary>
        public readonly Object IndexKey;

        /// <summary>主键值</summary>
        public readonly Object PrimaryKey;

        public SecondaryIndexDeltaEntry(String indexName, Boolean isPut, Object indexKey, Object primaryKey)
        {
            IndexName = indexName;
            IsPut = isPut;
            IndexKey = indexKey;
            PrimaryKey = primaryKey;
        }
    }

    #region 增量写入

    /// <summary>提交二级索引增量：把本次事务产生的 Put/Delete 追加到 .idxl，记录数超阈值时压缩为新的基线</summary>
    /// <param name="deltas">本次操作在各二级索引上产生的增量</param>
    private void CommitSecondaryIndexDeltas(List<SecondaryIndexDeltaEntry> deltas)
    {
        if (deltas == null || deltas.Count == 0) return;

        lock (_lock)
        {
            if (_disposed) return;

            // 同一索引的记录可能因“先移除旧值再插入新值”而不连续，按索引分组整组写入，保持删除/插入的先后顺序
            var buckets = new List<KeyValuePair<String, List<SecondaryIndexDeltaEntry>>>();
            var groups = new Dictionary<String, List<SecondaryIndexDeltaEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var delta in deltas)
            {
                if (!groups.TryGetValue(delta.IndexName, out var list))
                {
                    list = [];
                    groups[delta.IndexName] = list;
                    buckets.Add(new KeyValuePair<String, List<SecondaryIndexDeltaEntry>>(delta.IndexName, list));
                }
                list.Add(delta);
            }

            foreach (var bucket in buckets)
            {
                if (AppendSecondaryIndexDelta(bucket.Key, bucket.Value))
                {
                    // 增量已落盘，同步推进哈希索引的已应用偏移
                    if (_secondaryIndexes.TryGetValue(bucket.Key, out var idx)) idx.Commit(_rowLogLength);

                    CheckSecondaryIndexDeltaThreshold(bucket.Key);
                }
            }
        }
    }

    /// <summary>把一批增量记录追加到指定索引的 .idxl 文件</summary>
    /// <param name="indexName">索引名称</param>
    /// <param name="entries">同一索引上的增量记录（保持调用顺序）</param>
    /// <returns>是否写入成功</returns>
    private Boolean AppendSecondaryIndexDelta(String indexName, List<SecondaryIndexDeltaEntry> entries)
    {
        if (entries.Count == 0) return false;

        var indexDef = _schema.GetIndex(indexName);
        if (indexDef == null) return false;

        try
        {
            var stream = GetSecondaryIndexDeltaStream(indexName);
            if (stream == null) return false;

            var keyType = GetSecondaryIndexKeyType(indexDef);
            var pkCol = _schema.GetPrimaryKeyColumn()!;

            // 整批先编码到内存再一次性写入，避免中途异常留下半条记录
            using var w = new PooledBufferWriter(entries.Count * 64);
            foreach (var entry in entries)
            {
                var keyBytes = _codec.Encode(entry.IndexKey, keyType);
                var pkBytes = _codec.Encode(entry.PrimaryKey, pkCol.DataType);

                var recordStart = w.WrittenCount;
                var recordLength = 1 + 4 + keyBytes.Length + 4 + pkBytes.Length + 4;
                w.WriteInt32(recordLength);
                w.WriteByte(entry.IsPut ? SecondaryIndexDeltaRecordPut : SecondaryIndexDeltaRecordDelete);
                w.WriteInt32(keyBytes.Length);
                w.WriteBytes(keyBytes);
                w.WriteInt32(pkBytes.Length);
                w.WriteBytes(pkBytes);

                // Checksum 覆盖本记录 [RecordType..Pk]，不含前面的 RecordLength 字段
                var checksum = Crc32.Compute(w.Buffer.AsSpan(recordStart + 4, recordLength - 4));
                w.WriteUInt32(checksum);
            }

            stream.Position = stream.Length;
            stream.Write(w.Buffer, 0, w.WrittenCount);

            // 不逐条刷新：一次提交只在后置动作里刷新一次，避免每行一次系统调用
            _secondaryIndexDeltaCounts[indexName] = GetSecondaryIndexDeltaCount(indexName) + entries.Count;
            return true;
        }
        catch
        {
            // 写入失败（如磁盘已满）时关闭句柄但保留现有文件：下次启动回放会在坏记录处停止并全表重建
            CloseSecondaryIndexDeltaStream(indexName);
            return false;
        }
    }

    /// <summary>获取（必要时创建）二级索引增量文件流</summary>
    /// <param name="indexName">索引名称</param>
    /// <returns>以读写方式打开并定位到末尾的增量文件流</returns>
    private FileStream? GetSecondaryIndexDeltaStream(String indexName)
    {
        if (_secondaryIndexDeltaStreams.TryGetValue(indexName, out var stream)) return stream;

        var path = _fileManager.GetSecondaryIndexDeltaFilePath(indexName);
        var isNew = !File.Exists(path) || new FileInfo(path).Length < FileHeader.HeaderSize;

        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (isNew)
        {
            // 增量文件头版本与基线版本不同，两者按文件名区分用途
            var header = new FileHeader
            {
                Version = SecondaryIndexDeltaFileVersion,
                FileType = FileType.Index,
                PageSize = (UInt32)_options.PageSize,
                CreateTime = DateTime.Now,
            };

            stream.SetLength(0);
            using var headerPk = header.ToPacket();
            if (headerPk.TryGetArray(out var segment))
                stream.Write(segment.Array!, segment.Offset, segment.Count);
            stream.Flush();
        }

        stream.Position = stream.Length;
        _secondaryIndexDeltaStreams[indexName] = stream;

        return stream;
    }

    /// <summary>关闭二级索引增量文件流</summary>
    /// <param name="indexName">索引名称</param>
    private void CloseSecondaryIndexDeltaStream(String indexName)
    {
        if (!_secondaryIndexDeltaStreams.TryGetValue(indexName, out var stream)) return;

        _secondaryIndexDeltaStreams.Remove(indexName);
        try { stream.Dispose(); } catch { }
    }

    /// <summary>删除二级索引增量文件并重置计数</summary>
    /// <remarks>基线已包含增量的全部效果（或增量不可信已被丢弃）时调用</remarks>
    /// <param name="indexName">索引名称</param>
    private void ResetSecondaryIndexDelta(String indexName)
    {
        CloseSecondaryIndexDeltaStream(indexName);
        _secondaryIndexDeltaCounts.Remove(indexName);

        var path = _fileManager.GetSecondaryIndexDeltaFilePath(indexName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件被占用时保留残留文件，下次压缩仍会尝试清理
        }
    }

    /// <summary>获取二级索引已追加的增量记录数</summary>
    /// <param name="indexName">索引名称</param>
    /// <returns>增量记录数</returns>
    private Int32 GetSecondaryIndexDeltaCount(String indexName) => _secondaryIndexDeltaCounts.TryGetValue(indexName, out var count) ? count : 0;

    /// <summary>把二级索引增量缓冲写入刷到文件</summary>
    /// <remarks>由事务后置动作调用，保证提交返回时增量已落到文件</remarks>
    internal void FlushSecondaryIndexDeltas()
    {
        lock (_lock)
        {
            foreach (var stream in _secondaryIndexDeltaStreams.Values)
            {
                try { stream.Flush(); } catch { }
            }
        }
    }

    /// <summary>获取二级索引的增量压缩阈值</summary>
    /// <param name="indexName">索引名称</param>
    /// <returns>max(1000, (基线条目数 + 增量记录数) / 2)</returns>
    /// <remarks>
    /// 阈值必须随索引当前规模增长：只看基线条目数时，新建索引的基线为 0、阈值恒为 1000，
    /// 批量装载 500 万行会触发上千次全量重写基线（O(N²)），实测把 100 万行装载从 40 秒拖到 145 秒。
    /// </remarks>
    private Int32 GetSecondaryIndexDeltaThreshold(String indexName)
    {
        var baseline = _secondaryIndexBaselineCounts.TryGetValue(indexName, out var count) ? count : 0;
        var half = baseline / 2;

        return half > SecondaryIndexDeltaMinCompactionRecords ? half : SecondaryIndexDeltaMinCompactionRecords;
    }

    /// <summary>增量记录数超过阈值时压缩索引</summary>
    /// <param name="indexName">索引名称</param>
    private void CheckSecondaryIndexDeltaThreshold(String indexName)
    {
        if (GetSecondaryIndexDeltaCount(indexName) > GetSecondaryIndexDeltaThreshold(indexName))
            CompactSecondaryIndex(indexName);
    }

    /// <summary>压缩二级索引：全量重写基线 .idx，并清空增量 .idxl</summary>
    /// <param name="indexName">索引名称</param>
    private void CompactSecondaryIndex(String indexName)
    {
        // 先写基线再丢弃增量：若中途崩溃，重启时增量会在新基线上重复回放（Put/Delete 幂等，结果一致）
        PersistSecondaryIndex(indexName);

        lock (_lock)
        {
            ResetSecondaryIndexDelta(indexName);

            if (_secondaryIndexes.TryGetValue(indexName, out var idx))
            {
                idx.CompactIfNeeded(_rowLogLength);
                idx.Commit(_rowLogLength);
                _secondaryIndexBaselineCounts[indexName] = (Int32)idx.KeyCount;
            }
        }
    }

    /// <summary>关闭表时压缩所有二级索引，避免下次启动回放增量</summary>
    private void CompactSecondaryIndexesOnClose()
    {
        if (_secondaryIndexes.Count == 0) return;

        var names = new List<String>(_secondaryIndexes.Keys);
        foreach (var name in names)
        {
            if (GetSecondaryIndexDeltaCount(name) > 0)
            {
                try
                {
                    CompactSecondaryIndex(name);
                }
                catch
                {
                    // 压缩失败时保留增量文件，下次启动仍可回放或回退重建
                    CloseSecondaryIndexDeltaStream(name);
                }
            }
            else
            {
                CloseSecondaryIndexDeltaStream(name);
            }
        }
    }

    #endregion

    #region 启动回放

    /// <summary>回放二级索引增量日志到哈希字典</summary>
    /// <param name="indexDef">索引定义</param>
    /// <param name="idx">目标索引</param>
    /// <returns>回放的记录数；增量文件缺失返回 0；头部或尾部记录损坏返回 -1（调用方应回退全表重建）</returns>
    private Int32 ReplaySecondaryIndexDelta(IndexDefinition indexDef, NovaSecondaryIndex idx)
    {
        var path = _fileManager.GetSecondaryIndexDeltaFilePath(indexDef.IndexName);
        if (!File.Exists(path)) return 0;

        var pkCol = _schema.GetPrimaryKeyColumn()!;
        var keyType = GetSecondaryIndexKeyType(indexDef);
        var count = 0;

        var buf = ArrayPool<Byte>.Shared.Rent(4096);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 只有文件头或空文件视为无增量
            if (fs.Length <= FileHeader.HeaderSize) return 0;

            var headerBytes = new Byte[FileHeader.HeaderSize];
            if (fs.Read(headerBytes, 0, headerBytes.Length) < FileHeader.HeaderSize) return -1;
            if (!IsSecondaryIndexDeltaHeader(headerBytes)) return -1;

            while (fs.Position < fs.Length)
            {
                // 尾部截断/记录头损坏时无法确认增量完整，交给全表重建
                if (!fs.TryReadLengthPrefixedBlock(ref buf, out var recordLength)) return -1;
                if (recordLength < 1 + 4 + 4 + 4) return -1;

                var recordType = buf[0];
                var payloadEnd = recordLength - 4;
                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(payloadEnd));
                var actualChecksum = Crc32.Compute(buf, 0, payloadEnd);
                if (expectedChecksum != actualChecksum) return -1;

                var pos = 1;
                if (pos + 4 > payloadEnd) return -1;
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
                pos += 4;
                if (keyLength <= 0 || pos + keyLength > payloadEnd) return -1;
                var key = _codec.Decode(buf, pos, keyType);
                pos += keyLength;

                if (pos + 4 > payloadEnd) return -1;
                var pkLength = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
                pos += 4;
                if (pkLength <= 0 || pos + pkLength > payloadEnd) return -1;
                var pk = _codec.Decode(buf, pos, pkCol.DataType);

                if (key == null || pk == null) return -1;

                if (recordType == SecondaryIndexDeltaRecordPut)
                    idx.Add(key, pk);
                else if (recordType == SecondaryIndexDeltaRecordDelete)
                    idx.Remove(key, pk);
                else
                    return -1;

                count++;
            }
        }
        catch
        {
            return -1;
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buf);
        }

        _secondaryIndexDeltaCounts[indexDef.IndexName] = count;

        return count;
    }

    /// <summary>校验增量文件头（版本与文件类型）</summary>
    /// <param name="headerBytes">文件头原始字节</param>
    /// <returns>是否为合法的增量文件头</returns>
    private static Boolean IsSecondaryIndexDeltaHeader(Byte[] headerBytes)
    {
        try
        {
            var header = FileHeader.Read(headerBytes);

            return header.Version == SecondaryIndexDeltaFileVersion && header.FileType == FileType.Index;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

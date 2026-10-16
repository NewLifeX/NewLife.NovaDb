using System.Buffers.Binary;
using NewLife.Security;

namespace NewLife.NovaDb.Engine.Flux;

/// <summary>FluxEngine 列式块存储逻辑（F13）</summary>
/// <remarks>
/// 分区封存后，条目按列组织写入 flux.col 文件，并从行式 AOF 中移除，避免同一份数据双写。
/// 文件头为 8 字节（"NFCL" + 版本 + 保留），随后是若干块：[块负载长度: 4B][块负载][CRC32: 4B]，
/// 块负载内容见 <see cref="FluxBlockCodec"/>。读取时忽略尾部不完整或校验失败的块，崩溃后仍可恢复；
/// 旧版本目录（仅含 flux.rlog）可直接读取，无需迁移。封存分区若再写入迟到数据，会重新回到行式日志，
/// 并在下次封存时重建块，读取侧按（时间戳, 序号）去重，保证数据不重复。
/// </remarks>
public partial class FluxEngine
{
    private static readonly Byte[] FluxColumnarMagic = [(Byte)'N', (Byte)'F', (Byte)'C', (Byte)'L'];
    private const Int32 FluxColumnarHeaderSize = 8;

    private readonly HashSet<String> _sealedPartitions = new(StringComparer.Ordinal);
    private readonly List<FluxBlockMeta> _columnarBlocks = [];
    private String _purgeCutoffKey = String.Empty;
    private Int32 _statBlockCount;
    private Int64 _statEntryCount;
    private Int64 _statRowBytes;
    private Int64 _statBlockBytes;
    private Int32 _statColumnCount;
    private Int32 _statCompressedColumns;

    #region 封存

    /// <summary>封存分区为列式块并从行式日志移除</summary>
    /// <param name="sealAll">是否连最新分区一起封存；false 时保留最新分区在行式日志中，便于继续追加</param>
    /// <returns>本次封存的分区数量</returns>
    public Int32 SealPartitions(Boolean sealAll = false)
    {
        lock (_lock)
        {
            var count = SealPartitionsNoLock(null, sealAll);
            if (count > 0) CompactFluxLogNoLock();

            return count;
        }
    }

    /// <summary>封存指定时间点之前的已完成分区，用于分区滚动时的自动封存</summary>
    /// <param name="newPartitionKey">新分区键，封存所有小于该键且未封存的分区</param>
    /// <returns>封存的分区数量</returns>
    private Int32 AutoSealPartitionsNoLock(String newPartitionKey)
    {
        var count = SealPartitionsNoLock(newPartitionKey, false);
        if (count > 0) CompactFluxLogNoLock();

        return count;
    }

    /// <summary>封存分区实现，须在持锁状态下调用</summary>
    /// <param name="beforeKey">仅封存小于该键的分区；为空时按 sealAll 规则选择</param>
    /// <param name="sealAll">是否连最新分区一起封存</param>
    /// <returns>封存的分区数量</returns>
    private Int32 SealPartitionsNoLock(String? beforeKey, Boolean sealAll)
    {
        if (_partitions.Count == 0) return 0;

        var latestKey = String.Empty;
        foreach (var key in _partitions.Keys)
        {
            latestKey = key;
        }

        var targets = new List<String>();
        foreach (var key in _partitions.Keys)
        {
            if (_sealedPartitions.Contains(key)) continue;
            if (beforeKey != null)
            {
                if (String.Compare(key, beforeKey, StringComparison.Ordinal) >= 0) continue;
            }
            else if (!sealAll && key == latestKey)
            {
                continue;
            }

            targets.Add(key);
        }

        if (targets.Count == 0) return 0;

        foreach (var key in targets)
        {
            SealPartitionNoLock(key, _partitions[key]);
        }

        return targets.Count;
    }

    /// <summary>封存单个分区</summary>
    /// <param name="partitionKey">分区键</param>
    /// <param name="entries">分区条目</param>
    private void SealPartitionNoLock(String partitionKey, List<FluxEntry> entries)
    {
        var payload = FluxBlockCodec.Encode(partitionKey, entries, out var stats);
        var rowBytes = 0L;
        foreach (var entry in entries)
        {
            rowBytes += FluxBlockCodec.MeasureRowBytes(entry);
        }

        AppendColumnarBlock(new FluxBlockMeta
        {
            PartitionKey = partitionKey,
            PayloadLength = payload.Length,
            EntryCount = entries.Count,
            RowBytes = rowBytes,
            ColumnCount = stats.ColumnCount,
            CompressedColumns = stats.CompressedColumnCount,
        }, payload);

        _sealedPartitions.Add(partitionKey);
        AccumulateStats(payload.Length + 8, 1, entries.Count, rowBytes, stats.ColumnCount, stats.CompressedColumnCount);
    }

    /// <summary>累计统计</summary>
    /// <param name="blockBytes">块字节数</param>
    /// <param name="blockCount">块数量</param>
    /// <param name="entryCount">条目数量</param>
    /// <param name="rowBytes">行式字节数</param>
    /// <param name="columnCount">列数量</param>
    /// <param name="compressedColumns">压缩列数量</param>
    private void AccumulateStats(Int64 blockBytes, Int32 blockCount, Int64 entryCount, Int64 rowBytes, Int32 columnCount, Int32 compressedColumns)
    {
        _statBlockBytes += blockBytes;
        _statBlockCount += blockCount;
        _statEntryCount += entryCount;
        _statRowBytes += rowBytes;
        _statColumnCount += columnCount;
        _statCompressedColumns += compressedColumns;
    }

    /// <summary>获取列式存储压缩统计</summary>
    /// <returns>压缩统计快照</returns>
    public FluxCompressionStats GetCompressionStats()
    {
        lock (_lock)
        {
            return new FluxCompressionStats
            {
                BlockCount = _statBlockCount,
                EntryCount = _statEntryCount,
                RowBytes = _statRowBytes,
                BlockBytes = _statBlockBytes,
                ColumnCount = _statColumnCount,
                CompressedColumnCount = _statCompressedColumns,
            };
        }
    }

    /// <summary>追加列式块到文件并记录元数据</summary>
    /// <param name="meta">块元数据</param>
    /// <param name="payload">块负载</param>
    private void AppendColumnarBlock(FluxBlockMeta meta, Byte[] payload)
    {
        var path = GetColumnarPath();
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length < FluxColumnarHeaderSize) WriteColumnarHeader(fs);

        fs.Seek(0, SeekOrigin.End);
        var offset = fs.Length;
        var record = new Byte[payload.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(record, payload.Length);
        payload.CopyTo(record, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4 + payload.Length), Crc32.Compute(payload));

        fs.Write(record, 0, record.Length);
        fs.Flush();

        meta.Offset = offset;
        meta.Length = record.Length;
        _columnarBlocks.Add(meta);
    }

    #endregion

    #region 启动加载

    /// <summary>判断是否为分区滚动：目标分区键晚于当前所有分区</summary>
    /// <param name="partitionKey">目标分区键</param>
    /// <returns>是否为新的更晚时间片</returns>
    private Boolean IsPartitionRolloverNoLock(String partitionKey)
    {
        var latest = String.Empty;
        foreach (var key in _partitions.Keys)
        {
            latest = key;
        }

        return latest.Length > 0 && String.Compare(partitionKey, latest, StringComparison.Ordinal) > 0;
    }

    /// <summary>加载列式块文件，与行式日志回放结果合并</summary>
    private void LoadColumnarBlocks()
    {
        var path = GetColumnarPath();
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        if (data.Length < FluxColumnarHeaderSize) return;
        if (data[0] != FluxColumnarMagic[0] || data[1] != FluxColumnarMagic[1] || data[2] != FluxColumnarMagic[2] || data[3] != FluxColumnarMagic[3]) return;

        var pos = FluxColumnarHeaderSize;
        while (pos + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
            if (length <= 0 || pos + 8 + length > data.Length) break;

            var payload = data.AsSpan(pos + 4, length);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4 + length));
            if (crc != Crc32.Compute(payload)) break;

            String partitionKey;
            List<FluxEntry> entries;
            try
            {
                entries = FluxBlockCodec.Decode(payload, out partitionKey);
            }
            catch (InvalidDataException)
            {
                break;
            }

            var rowBytes = 0L;
            foreach (var entry in entries)
            {
                rowBytes += FluxBlockCodec.MeasureRowBytes(entry);
            }

            var summary = FluxBlockCodec.ReadColumnSummary(payload);
            var meta = new FluxBlockMeta
            {
                PartitionKey = partitionKey,
                Offset = pos,
                Length = length + 8,
                EntryCount = entries.Count,
                RowBytes = rowBytes,
                ColumnCount = summary.ColumnCount,
                CompressedColumns = summary.CompressedCount,
            };
            _columnarBlocks.Add(meta);
            AccumulateStats(meta.Length, 1, entries.Count, rowBytes, summary.ColumnCount, summary.CompressedCount);
            MergeBlockNoLock(partitionKey, entries);
            pos += length + 8;
        }
    }

    /// <summary>合并块数据到内存分区，跳过已清理分区并按（时间戳, 序号）去重</summary>
    /// <param name="partitionKey">分区键</param>
    /// <param name="entries">块内条目</param>
    private void MergeBlockNoLock(String partitionKey, List<FluxEntry> entries)
    {
        if (_purgeCutoffKey.Length > 0 && String.Compare(partitionKey, _purgeCutoffKey, StringComparison.Ordinal) < 0) return;

        if (!_partitions.TryGetValue(partitionKey, out var list))
        {
            _partitions[partitionKey] = entries;
            _sealedPartitions.Add(partitionKey);
            return;
        }

        // 行式日志已含该分区：说明存在迟到追加或封存后崩溃，按消息标识去重
        var existing = new HashSet<(Int64, Int32)>();
        foreach (var entry in list)
        {
            existing.Add((entry.Timestamp, entry.SequenceId));
        }

        foreach (var entry in entries)
        {
            if (existing.Add((entry.Timestamp, entry.SequenceId))) list.Add(entry);
        }
    }

    #endregion

    #region 清理

    /// <summary>重写列式文件，丢弃截止分区键之前的块</summary>
    /// <param name="cutoffKey">截止分区键，小于该键的分区被清理</param>
    private void RewriteColumnarFileNoLock(String cutoffKey)
    {
        var path = GetColumnarPath();
        if (_columnarBlocks.Count == 0 || !File.Exists(path)) return;

        var kept = new List<FluxBlockMeta>();
        foreach (var meta in _columnarBlocks)
        {
            if (String.Compare(meta.PartitionKey, cutoffKey, StringComparison.Ordinal) >= 0) kept.Add(meta);
        }

        if (kept.Count == _columnarBlocks.Count) return;

        // 重建统计：仅保留存活块的贡献
        _statBlockCount = 0;
        _statEntryCount = 0;
        _statRowBytes = 0;
        _statBlockBytes = 0;
        _statColumnCount = 0;
        _statCompressedColumns = 0;
        foreach (var meta in kept)
        {
            AccumulateStats(meta.Length, 1, meta.EntryCount, meta.RowBytes, meta.ColumnCount, meta.CompressedColumns);
        }

        var temp = path + ".tmp";
        var buffer = new Byte[8192];
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteColumnarHeader(dst);
            var offset = (Int64)FluxColumnarHeaderSize;
            foreach (var meta in kept)
            {
                src.Position = meta.Offset;
                var remaining = (Int64)meta.Length;
                while (remaining > 0)
                {
                    var read = src.Read(buffer, 0, (Int32)Math.Min(remaining, buffer.Length));
                    if (read <= 0) break;

                    dst.Write(buffer, 0, read);
                    remaining -= read;
                }

                meta.Offset = offset;
                offset += meta.Length;
            }

            dst.Flush();
        }

        _columnarBlocks.Clear();
        _columnarBlocks.AddRange(kept);

        File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>清空列式块文件与相关状态</summary>
    private void ClearColumnarNoLock()
    {
        var path = GetColumnarPath();
        if (File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                fs.SetLength(0);
                WriteColumnarHeader(fs);
                fs.Flush();
            }
            catch (IOException) { }
        }

        _columnarBlocks.Clear();
        _sealedPartitions.Clear();
        _statBlockCount = 0;
        _statEntryCount = 0;
        _statRowBytes = 0;
        _statBlockBytes = 0;
        _statColumnCount = 0;
        _statCompressedColumns = 0;
    }

    #endregion

    #region 辅助

    /// <summary>获取列式块文件路径</summary>
    private String GetColumnarPath() => Path.Combine(_basePath, "flux.col");

    /// <summary>写入列式文件头</summary>
    /// <param name="fs">文件流</param>
    private static void WriteColumnarHeader(FileStream fs)
    {
        if (fs.Length < FluxColumnarHeaderSize)
        {
            fs.SetLength(FluxColumnarHeaderSize);
        }

        var header = new Byte[FluxColumnarHeaderSize];
        FluxColumnarMagic.CopyTo(header, 0);
        header[4] = 1; // Version

        fs.Position = 0;
        fs.Write(header, 0, header.Length);
        fs.Flush();
    }

    #endregion

    #region 内部类型

    /// <summary>列式块元数据</summary>
    private sealed class FluxBlockMeta
    {
        /// <summary>分区键</summary>
        public String PartitionKey { get; set; } = String.Empty;

        /// <summary>文件内偏移</summary>
        public Int64 Offset { get; set; }

        /// <summary>块记录总长度（含长度前缀与校验）</summary>
        public Int32 Length { get; set; }

        /// <summary>块负载长度</summary>
        public Int32 PayloadLength { get; set; }

        /// <summary>条目数量</summary>
        public Int32 EntryCount { get; set; }

        /// <summary>行式 AOF 等效字节数</summary>
        public Int64 RowBytes { get; set; }

        /// <summary>列数量</summary>
        public Int32 ColumnCount { get; set; }

        /// <summary>压缩列数量</summary>
        public Int32 CompressedColumns { get; set; }
    }

    #endregion
}

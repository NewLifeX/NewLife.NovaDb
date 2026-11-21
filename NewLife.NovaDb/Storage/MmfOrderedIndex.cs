using NewLife.NovaDb.Core;
using NewLife.Security;

namespace NewLife.NovaDb.Storage;

/// <summary>有序索引条目</summary>
internal struct OrderedIndexEntry
{
    /// <summary>已编码的索引键</summary>
    public Byte[] Key;

    /// <summary>可比较前缀（无前缀时为 0）</summary>
    public Int64 SortPrefix;

    /// <summary>是否有可比较前缀</summary>
    public Boolean HasPrefix;

    /// <summary>创建该版本的事务 ID</summary>
    public UInt64 CreatedTx;

    /// <summary>删除该版本的事务 ID，0 表示未删除</summary>
    public UInt64 DeletedTx;

    /// <summary>行载荷在行日志中的偏移</summary>
    public Int64 DataOffset;

    /// <summary>行载荷长度</summary>
    public Int32 DataLength;

    /// <summary>条目是否为存活版本</summary>
    public readonly Boolean IsLive => DeletedTx == 0;
}

/// <summary>MMF 磁盘有序段索引：按键有序的不可变 run 集合，支持范围扫描与排序扫描</summary>
/// <remarks>
/// 布局：
/// <code>
/// [FileHeader 32B][MetaSlot0 160B][MetaSlot1 160B][RunIndex 32B × 1024][Run0][Run1]...
/// Run = [RunHeader 40B][条目区][偏移目录 Int64 × EntryCount]
/// 条目 = [SortPrefix][CreatedTx][DeletedTx][DataOffset][DataLength][Flags][KeyLength][key]
/// </code>
/// 提交时条目先进内存写缓冲（有序），缓冲满或关闭时整体写出一个新 run；
/// 读取时对所有 run 与写缓冲做归并，同一键只输出最新的存活版本。
/// run 数量超过阈值时按体量分层合并少量 run（只重写同量级的小 run，避免全表重写造成秒级停顿）；
/// 文件垃圾占比过半时才做一次全量重写，回收空间并丢弃旧版本与已删除条目。
/// </remarks>
internal sealed class MmfOrderedIndex : IDisposable
{
    #region 常量
    private const UInt32 Magic = 0x584F4931;
    private const Byte FileVersion = 1;

    private const Int32 MetaSlot0 = FileHeader.HeaderSize;
    private const Int32 MetaSlot1 = MetaSlot0 + MetaSize;
    private const Int32 MetaSize = 160;
    private const Int32 RunIndexOffset = MetaSlot1 + MetaSize;
    private const Int32 RunIndexCapacity = 1024;
    private const Int32 RunIndexEntrySize = 32;
    private const Int32 ArenaStart = RunIndexOffset + RunIndexCapacity * RunIndexEntrySize;

    private const Int32 MetaMagic = 0;
    private const Int32 MetaVersion = 4;
    private const Int32 MetaKeyParts = 6;
    private const Int32 MetaKeyType = 7;
    private const Int32 MetaSequence = 8;
    private const Int32 MetaRunCount = 16;
    private const Int32 MetaArenaEnd = 24;
    private const Int32 MetaAppliedOffset = 32;
    private const Int32 MetaEntryCount = 40;
    private const Int32 MetaCrc = 152;

    private const UInt32 RunMagic = 0x314E5552;
    private const Int32 RunHeaderSize = 40;
    private const Int32 RunMagicOffset = 0;
    private const Int32 RunVersionOffset = 4;
    private const Int32 RunEntryCountOffset = 8;
    private const Int32 RunEntriesOffset = 16;
    private const Int32 RunEntriesLengthOffset = 24;
    private const Int32 RunCrcOffset = 32;

    private const Int32 EntrySortPrefix = 0;
    private const Int32 EntryCreatedTx = 8;
    private const Int32 EntryDeletedTx = 16;
    private const Int32 EntryDataOffset = 24;
    private const Int32 EntryDataLength = 32;
    private const Int32 EntryFlags = 36;
    private const Int32 EntryKeyLength = 40;
    private const Int32 EntryHeaderSize = 44;
    private const Int32 EntryFlagHasPrefix = 1;

    private const Int32 DefaultFlushEntries = 65536;
    private const Int32 MaxRuns = 12;
    private const Int32 MergeFanIn = 4;
    private const Int64 MinCompactGarbage = 8L << 20;
    #endregion

    #region 属性
    private readonly IndexKeyCodec _codec;
    private readonly Boolean _readOnly;
    private readonly Int32 _flushEntries;

    private MmfRegion _region;
    private OrderedMeta _meta;
    private readonly List<RunInfo> _runs = [];
    private readonly List<Int32> _runSlots = [];
    private readonly List<Int32> _freeSlots = [];
    private Int32 _runSlotCount;
    private readonly List<OrderedIndexEntry> _buffer = [];
    private Byte[] _probeBuffer = new Byte[256];
    private Byte[] _entryBuffer = new Byte[EntryHeaderSize + 64];
    private readonly Byte[] _entryHeader = new Byte[EntryHeaderSize];
    private Int64 _pendingOffset = -1;
    private Boolean _bufferSorted = true;
    private Boolean _disposed;

    /// <summary>索引文件路径</summary>
    public String FilePath => _region.FilePath;

    /// <summary>已应用的 rlog 偏移</summary>
    public Int64 AppliedOffset => _meta.AppliedOffset;

    /// <summary>run 数量</summary>
    public Int32 RunCount => _runs.Count;

    /// <summary>写缓冲条目数</summary>
    public Int32 BufferCount => _buffer.Count;

    /// <summary>run 中的条目总数（含旧版本）</summary>
    public Int64 EntryCount => _meta.EntryCount;

    #endregion

    #region 构造
    private MmfOrderedIndex(MmfRegion region, IndexKeyCodec codec, Boolean readOnly, Int32 flushEntries)
    {
        _region = region;
        _codec = codec;
        _readOnly = readOnly;
        _flushEntries = flushEntries > 0 ? flushEntries : DefaultFlushEntries;
    }

    /// <summary>打开或创建有序段索引</summary>
    /// <param name="path">文件路径</param>
    /// <param name="codec">键编解码器</param>
    /// <param name="readOnly">是否只读</param>
    /// <param name="flushEntries">写缓冲条目上限，达到即刷 run</param>
    /// <returns>索引实例</returns>
    public static MmfOrderedIndex Open(String path, IndexKeyCodec codec, Boolean readOnly = false, Int32 flushEntries = 0)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (codec == null) throw new ArgumentNullException(nameof(codec));

        var region = new MmfRegion(path, readOnly);
        var isNew = false;
        try
        {
            isNew = region.Open();
        }
        catch
        {
            region.Dispose();
            throw;
        }

        try
        {
            var index = new MmfOrderedIndex(region, codec, readOnly, flushEntries);
            if (isNew)
                index.Initialize();
            else
                index.Load();

            return index;
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>关闭索引，关闭前把写缓冲刷成一个 run</summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (!_readOnly)
        {
            try
            {
                Flush();
            }
            catch
            {
                // 关闭阶段刷失败时保留已有 run，下次打开按 appliedOffset 回放
            }
        }

        _region.Dispose();
        _disposed = true;
    }

    #endregion

    #region 写入
    /// <summary>写入条目到内存缓冲，缓冲满时自动刷 run</summary>
    /// <param name="entries">条目列表</param>
    /// <param name="appliedOffset">应用后的 rlog 偏移，-1 表示不推进</param>
    public void Add(IList<OrderedIndexEntry> entries, Int64 appliedOffset = -1)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfOrderedIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", FilePath);
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        foreach (var entry in entries)
        {
            if (entry.Key == null) throw new ArgumentNullException(nameof(entries));
            _buffer.Add(entry);
        }
        _bufferSorted = false;

        if (appliedOffset >= 0) _pendingOffset = appliedOffset;

        if (_runSlotCount >= RunIndexCapacity - 1)
        {
            Compact();
        }
        else if (_runs.Count >= MaxRuns)
            MergeRuns();
        else if (_buffer.Count >= _flushEntries)
            Flush();
    }

    /// <summary>设置已应用偏移（只推进内存值，随下次落 run 或关闭时一起持久化）</summary>
    /// <param name="appliedOffset">已应用的 rlog 偏移</param>
    public void SetAppliedOffset(Int64 appliedOffset)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfOrderedIndex));
        if (_readOnly) return;

        _pendingOffset = appliedOffset;
        _meta.AppliedOffset = appliedOffset;
    }

    /// <summary>把写缓冲刷成一个 run</summary>
    public void Flush(Boolean syncView = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfOrderedIndex));
        if (_readOnly) return;

        if (_buffer.Count == 0)
        {
            SaveMeta();
            _region.Flush(syncView);
            return;
        }

        SortBuffer();
        var entryCount = _buffer.Count;
        var entriesLength = 0L;
        for (var i = 0; i < entryCount; i++) entriesLength += Align(EntryHeaderSize + _buffer[i].Key.Length);

        var runOffset = Align(_meta.ArenaEnd);
        var entriesOffset = runOffset + RunHeaderSize;
        var directoryOffset = entriesOffset + entriesLength;
        var runLength = RunHeaderSize + entriesLength + entryCount * sizeof(Int64);
        var end = runOffset + runLength;

        _region.EnsureCapacity(end);

        // 先写 run 数据，再更新元数据：崩溃时元数据仍指向旧 run，重新回放即可
        var header = new Byte[RunHeaderSize];
        WriteUInt32(header, RunMagicOffset, RunMagic);
        header[RunVersionOffset] = FileVersion;
        WriteInt64(header, RunEntryCountOffset, entryCount);
        WriteInt64(header, RunEntriesOffset, entriesOffset);
        WriteInt64(header, RunEntriesLengthOffset, entriesLength);
        _region.Write(runOffset, header, 0, header.Length);

        var position = entriesOffset;
        for (var i = 0; i < entryCount; i++)
        {
            var entry = _buffer[i];
            WriteEntry(position, entry);
            _region.WriteInt64(directoryOffset + (Int64)i * sizeof(Int64), position);
            position += Align(EntryHeaderSize + entry.Key.Length);
        }

        _region.Flush(syncView);

        AppendRunSlot(new RunInfo(runOffset, entryCount, entriesOffset, entriesLength, directoryOffset));

        _meta.ArenaEnd = end;
        _meta.EntryCount += entryCount;
        if (_pendingOffset >= 0) _meta.AppliedOffset = _pendingOffset;
        SaveMeta();
        _region.Flush(syncView);

        _buffer.Clear();
        _bufferSorted = true;
    }

    /// <summary>清空所有 run 与写缓冲</summary>
    public void Truncate()
    {
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot truncate index file in read-only mode: {0}", FilePath);

        _buffer.Clear();
        _bufferSorted = true;
        _runs.Clear();
        _runSlots.Clear();
        _freeSlots.Clear();
        _runSlotCount = 0;
        _pendingOffset = -1;

        _meta.RunCount = 0;
        _meta.ArenaEnd = ArenaStart;
        _meta.EntryCount = 0;
        _meta.AppliedOffset = 0;
        SaveMeta();
        _region.Flush();
    }

    #endregion

    #region 读取
    /// <summary>扫描范围内的存活条目，按键升序或降序输出</summary>
    /// <param name="fromKey">下界键（已编码），null 表示不限</param>
    /// <param name="includeFrom">是否包含下界</param>
    /// <param name="toKey">上界键（已编码），null 表示不限</param>
    /// <param name="includeTo">是否包含上界</param>
    /// <param name="descending">是否降序</param>
    /// <param name="maxRows">最大输出条数，0 表示不限</param>
    /// <param name="visitor">条目访问器，返回 false 提前结束</param>
    public void Scan(Byte[]? fromKey, Boolean includeFrom, Byte[]? toKey, Boolean includeTo, Boolean descending, Int32 maxRows, Func<OrderedIndexEntry, Boolean> visitor)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfOrderedIndex));
        if (visitor == null) throw new ArgumentNullException(nameof(visitor));

        MergeScan(fromKey, includeFrom, toKey, includeTo, descending, maxRows, visitor);
    }

    /// <summary>查找指定键最新的存活条目</summary>
    /// <param name="key">已编码的键</param>
    /// <param name="entry">命中的条目</param>
    /// <returns>是否存在存活版本</returns>
    public Boolean TryGetLive(Byte[] key, out OrderedIndexEntry entry)
    {
        var found = false;
        var result = default(OrderedIndexEntry);

        Scan(key, true, key, true, false, 0, e =>
        {
            result = e;
            found = true;
            return false;
        });

        entry = result;

        return found;
    }

    #endregion

    #region 归并扫描
    /// <summary>归并扫描实现</summary>
    private void MergeScan(Byte[]? fromKey, Boolean includeFrom, Byte[]? toKey, Boolean includeTo, Boolean descending, Int32 maxRows, Func<OrderedIndexEntry, Boolean> visitor)
    {
        var cursors = new List<Cursor>(_runs.Count + 1);
        var advance = new List<Cursor>(_runs.Count + 1);
        var group = new List<OrderedIndexEntry>(8);
        var scratch = new Cursor(false);

        var fromPrefix = GetProbePrefix(fromKey, out var fromHasPrefix);
        var toPrefix = GetProbePrefix(toKey, out var toHasPrefix);

        foreach (var run in _runs)
        {
            var cursor = CreateRunCursor(run, descending ? toKey : fromKey, descending ? includeTo : includeFrom, descending, toPrefix, toHasPrefix, fromPrefix, fromHasPrefix);
            if (cursor != null) cursors.Add(cursor);
        }

        if (_buffer.Count > 0)
        {
            SortBuffer();
            var cursor = CreateBufferCursor(descending ? toKey : fromKey, descending ? includeTo : includeFrom, descending, toPrefix, toHasPrefix, fromPrefix, fromHasPrefix);
            if (cursor != null) cursors.Add(cursor);
        }

        if (cursors.Count == 0) return;

        var direction = descending ? -1 : 1;
        var emitted = 0;
        while (true)
        {
            // 找出当前最小（升序）或最大（降序）的键
            Cursor? best = null;
            foreach (var cursor in cursors)
            {
                if (!cursor.HasValue) continue;

                if (best == null || CompareCursor(cursor, best) * direction < 0)
                    best = cursor;
            }

            if (best == null) break;

            // 收集所有来源中与当前键相等的条目（同一来源内相邻重复也要一起处理）
            advance.Clear();
            group.Clear();
            foreach (var cursor in cursors)
            {
                if (!cursor.HasValue) continue;
                if (CompareCursor(cursor, best) != 0) continue;

                advance.Add(cursor);

                if (cursor.Run == null)
                {
                    group.Add(ToEntry(_buffer[cursor.Position]));

                    while (true)
                    {
                        var next = cursor.Position + direction;
                        if (next < 0 || next >= _buffer.Count) break;
                        if (CompareBufferEntry(_buffer[next], best) != 0) break;

                        group.Add(ToEntry(_buffer[next]));
                        cursor.Position = next;
                    }
                }
                else
                {
                    group.Add(CopyEntry(cursor.ToEntry()));

                    while (true)
                    {
                        var next = cursor.Position + direction;
                        if (next < 0 || next >= cursor.Run.EntryCount) break;
                        if (!LoadRunCursor(scratch, cursor.Run, next)) break;
                        if (CompareCursor(scratch, best) != 0) break;

                        group.Add(CopyEntry(scratch.ToEntry()));
                        cursor.Position = next;
                    }
                }
            }

            // 选版本：DeletedTx != 0 的条目是某版本的删除标记；剩余里取 CreatedTx 最大者
            var hasChosen = false;
            var chosen = default(OrderedIndexEntry);
            foreach (var entry in group)
            {
                if (entry.DeletedTx != 0) continue;

                var deleted = false;
                foreach (var other in group)
                {
                    if (other.DeletedTx != 0 && other.CreatedTx == entry.CreatedTx)
                    {
                        deleted = true;
                        break;
                    }
                }
                if (deleted) continue;

                if (!hasChosen || entry.CreatedTx > chosen.CreatedTx)
                {
                    chosen = entry;
                    hasChosen = true;
                }
            }

            if (hasChosen)
            {
                // 越过扫描方向的终点立即结束：范围扫描只走命中区间，不再遍历剩余数据
                if (!descending)
                {
                    if (toKey != null && IsPastEnd(chosen, toKey, toPrefix, toHasPrefix, includeTo, false)) return;
                }
                else if (fromKey != null && IsPastEnd(chosen, fromKey, fromPrefix, fromHasPrefix, includeFrom, true))
                    return;
            }

            if (hasChosen && InRange(chosen, fromKey, fromPrefix, fromHasPrefix, toKey, toPrefix, toHasPrefix, includeFrom, includeTo))
            {
                if (!visitor(chosen)) return;

                emitted++;
                if (maxRows > 0 && emitted >= maxRows) return;
            }

            foreach (var cursor in advance) Advance(cursor);
        }
    }

    /// <summary>比较写缓冲条目与游标当前键</summary>
    private Int32 CompareBufferEntry(in OrderedIndexEntry entry, Cursor cursor)
        => _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, cursor.Key, 0, cursor.KeyLength, cursor.HasPrefix, cursor.SortPrefix);

    /// <summary>复制条目，键单独分配</summary>
    private static OrderedIndexEntry CopyEntry(in OrderedIndexEntry entry)
    {
        var key = new Byte[entry.Key.Length];
        Array.Copy(entry.Key, key, key.Length);

        return new OrderedIndexEntry
        {
            Key = key,
            SortPrefix = entry.SortPrefix,
            HasPrefix = entry.HasPrefix,
            CreatedTx = entry.CreatedTx,
            DeletedTx = entry.DeletedTx,
            DataOffset = entry.DataOffset,
            DataLength = entry.DataLength,
        };
    }

    /// <summary>把写缓冲条目转换为输出结构（键引用缓冲，回调期间有效）</summary>
    private static OrderedIndexEntry ToEntry(in OrderedIndexEntry entry) => entry;

    /// <summary>比较两个游标的当前键（升序语义）</summary>
    private Int32 CompareCursor(Cursor a, Cursor b) => _codec.Compare(a.Key, 0, a.KeyLength, a.HasPrefix, a.SortPrefix, b.Key, 0, b.KeyLength, b.HasPrefix, b.SortPrefix);

    /// <summary>判断条目是否在区间内</summary>
    private Boolean InRange(in OrderedIndexEntry entry, Byte[]? fromKey, Int64 fromPrefix, Boolean fromHasPrefix, Byte[]? toKey, Int64 toPrefix, Boolean toHasPrefix, Boolean includeFrom, Boolean includeTo)
    {
        if (fromKey != null)
        {
            var cmp = _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, fromKey, 0, fromKey.Length, fromHasPrefix, fromPrefix);
            if (cmp < 0 || (cmp == 0 && !includeFrom)) return false;
        }

        if (toKey != null)
        {
            var cmp = _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, toKey, 0, toKey.Length, toHasPrefix, toPrefix);
            if (cmp > 0 || (cmp == 0 && !includeTo)) return false;
        }

        return true;
    }

    /// <summary>判断条目是否已越过扫描终点</summary>
    /// <param name="entry">当前条目</param>
    /// <param name="boundKey">边界键</param>
    /// <param name="boundPrefix">边界键的可比较前缀</param>
    /// <param name="boundHasPrefix">边界键是否有可比较前缀</param>
    /// <param name="inclusive">边界是否包含</param>
    /// <param name="descending">是否为降序扫描</param>
    /// <returns>已越过终点返回 true</returns>
    private Boolean IsPastEnd(in OrderedIndexEntry entry, Byte[] boundKey, Int64 boundPrefix, Boolean boundHasPrefix, Boolean inclusive, Boolean descending)
    {
        var cmp = _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, boundKey, 0, boundKey.Length, boundHasPrefix, boundPrefix);

        return descending ? cmp < 0 || (cmp == 0 && !inclusive) : cmp > 0 || (cmp == 0 && !inclusive);
    }

    /// <summary>获取探测键的可比较前缀</summary>
    private Int64 GetProbePrefix(Byte[]? key, out Boolean hasPrefix)
    {
        hasPrefix = false;
        if (key == null || !_codec.HasSortPrefix) return 0;

        var decoded = _codec.Decode(key, 0, key.Length);
        if (decoded == null) return 0;

        hasPrefix = _codec.TryGetSortPrefix(decoded, out var prefix);

        return hasPrefix ? prefix : 0;
    }

    /// <summary>创建 run 游标</summary>
    private Cursor? CreateRunCursor(RunInfo run, Byte[]? boundKey, Boolean inclusive, Boolean descending, Int64 toPrefix, Boolean toHasPrefix, Int64 fromPrefix, Boolean fromHasPrefix)
    {
        Int32 position;
        if (boundKey == null)
            position = descending ? (Int32)run.EntryCount - 1 : 0;
        else
        {
            var prefix = descending ? toPrefix : fromPrefix;
            var hasPrefix = descending ? toHasPrefix : fromHasPrefix;
            var index = descending ? UpperBound(run, boundKey, inclusive, prefix, hasPrefix) - 1 : LowerBound(run, boundKey, inclusive, prefix, hasPrefix);
            position = index;
        }

        if (position < 0 || position >= run.EntryCount) return null;

        var cursor = new Cursor(descending);
        if (!LoadRunCursor(cursor, run, position)) return null;

        return cursor;
    }

    /// <summary>创建写缓冲游标</summary>
    private Cursor? CreateBufferCursor(Byte[]? boundKey, Boolean inclusive, Boolean descending, Int64 toPrefix, Boolean toHasPrefix, Int64 fromPrefix, Boolean fromHasPrefix)
    {
        Int32 position;
        if (boundKey == null)
            position = descending ? _buffer.Count - 1 : 0;
        else
        {
            var prefix = descending ? toPrefix : fromPrefix;
            var hasPrefix = descending ? toHasPrefix : fromHasPrefix;
            var index = descending ? BufferUpperBound(boundKey, inclusive, prefix, hasPrefix) - 1 : BufferLowerBound(boundKey, inclusive, prefix, hasPrefix);
            position = index;
        }

        if (position < 0 || position >= _buffer.Count) return null;

        var cursor = new Cursor(descending);
        if (!LoadBufferCursor(cursor, position)) return null;

        return cursor;
    }

    /// <summary>前进/后退一个位置</summary>
    private void Advance(Cursor cursor)
    {
        var position = cursor.Position + cursor.Direction;

        if (cursor.Run != null)
        {
            if (position < 0 || position >= cursor.Run.EntryCount)
                cursor.HasValue = false;
            else if (!LoadRunCursor(cursor, cursor.Run, position))
                cursor.HasValue = false;
        }
        else
        {
            if (position < 0 || position >= _buffer.Count)
                cursor.HasValue = false;
            else if (!LoadBufferCursor(cursor, position))
                cursor.HasValue = false;
        }
    }

    /// <summary>加载 run 中指定位置的条目到游标</summary>
    private Boolean LoadRunCursor(Cursor cursor, RunInfo run, Int32 position)
    {
        var offset = _region.ReadInt64(run.DirectoryOffset + (Int64)position * sizeof(Int64));
        if (offset <= 0 || offset < run.EntriesOffset || offset >= run.EntriesOffset + run.EntriesLength) return false;

        // 固定头一次读入，避免逐字段访问映射视图
        _region.Read(offset, _entryHeader, 0, EntryHeaderSize);

        var keyLength = ReadInt32(_entryHeader, EntryKeyLength);
        if (keyLength < 0 || keyLength > 1 << 20) return false;

        cursor.EnsureKey(keyLength);
        _region.Read(offset + EntryHeaderSize, cursor.Key, 0, keyLength);

        cursor.Position = position;
        cursor.Run = run;
        cursor.KeyLength = keyLength;
        cursor.SortPrefix = ReadInt64(_entryHeader, EntrySortPrefix);
        cursor.HasPrefix = (ReadInt32(_entryHeader, EntryFlags) & EntryFlagHasPrefix) != 0;
        cursor.CreatedTx = (UInt64)ReadInt64(_entryHeader, EntryCreatedTx);
        cursor.DeletedTx = (UInt64)ReadInt64(_entryHeader, EntryDeletedTx);
        cursor.DataOffset = ReadInt64(_entryHeader, EntryDataOffset);
        cursor.DataLength = ReadInt32(_entryHeader, EntryDataLength);
        cursor.HasValue = true;

        return true;
    }

    /// <summary>加载写缓冲中指定位置的条目到游标</summary>
    private Boolean LoadBufferCursor(Cursor cursor, Int32 position)
    {
        var entry = _buffer[position];

        cursor.Position = position;
        cursor.Run = null;
        cursor.KeyLength = entry.Key.Length;
        cursor.EnsureKey(entry.Key.Length);
        Array.Copy(entry.Key, cursor.Key, entry.Key.Length);
        cursor.SortPrefix = entry.SortPrefix;
        cursor.HasPrefix = entry.HasPrefix;
        cursor.CreatedTx = entry.CreatedTx;
        cursor.DeletedTx = entry.DeletedTx;
        cursor.DataOffset = entry.DataOffset;
        cursor.DataLength = entry.DataLength;
        cursor.HasValue = true;

        return true;
    }

    /// <summary>run 内二分查找下界</summary>
    private Int32 LowerBound(RunInfo run, Byte[] key, Boolean inclusive, Int64 prefix, Boolean hasPrefix)
    {
        var low = 0;
        var high = (Int32)run.EntryCount;

        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var cmp = CompareRunEntry(run, mid, key, prefix, hasPrefix);

            if (cmp < 0 || (cmp == 0 && !inclusive))
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>run 内二分查找上界</summary>
    private Int32 UpperBound(RunInfo run, Byte[] key, Boolean inclusive, Int64 prefix, Boolean hasPrefix)
    {
        var low = 0;
        var high = (Int32)run.EntryCount;

        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var cmp = CompareRunEntry(run, mid, key, prefix, hasPrefix);

            if (cmp < 0 || (cmp == 0 && inclusive))
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>写缓冲内二分查找下界</summary>
    private Int32 BufferLowerBound(Byte[] key, Boolean inclusive, Int64 prefix, Boolean hasPrefix)
    {
        var low = 0;
        var high = _buffer.Count;

        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var entry = _buffer[mid];
            var cmp = _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, key, 0, key.Length, hasPrefix, prefix);

            if (cmp < 0 || (cmp == 0 && !inclusive))
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>写缓冲内二分查找上界</summary>
    private Int32 BufferUpperBound(Byte[] key, Boolean inclusive, Int64 prefix, Boolean hasPrefix)
    {
        var low = 0;
        var high = _buffer.Count;

        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var entry = _buffer[mid];
            var cmp = _codec.Compare(entry.Key, 0, entry.Key.Length, entry.HasPrefix, entry.SortPrefix, key, 0, key.Length, hasPrefix, prefix);

            if (cmp < 0 || (cmp == 0 && inclusive))
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>比较 run 内条目与探测键</summary>
    private Int32 CompareRunEntry(RunInfo run, Int32 index, Byte[] key, Int64 prefix, Boolean hasPrefix)
    {
        var offset = _region.ReadInt64(run.DirectoryOffset + (Int64)index * sizeof(Int64));
        var keyLength = _region.ReadInt32(offset + EntryKeyLength);
        var entryPrefix = _region.ReadInt64(offset + EntrySortPrefix);
        var entryHasPrefix = (_region.ReadInt32(offset + EntryFlags) & EntryFlagHasPrefix) != 0;

        EnsureProbeBuffer(keyLength);
        _region.Read(offset + EntryHeaderSize, _probeBuffer, 0, keyLength);

        return _codec.Compare(_probeBuffer, 0, keyLength, entryHasPrefix, entryPrefix, key, 0, key.Length, hasPrefix, prefix);
    }

    #endregion

    #region 压缩
    /// <summary>按体量分层合并少量 run</summary>
    /// <remarks>
    /// 全量重写会随表规模线性增长，大表上会出现秒级停顿。这里改为分层合并：
    /// 只在最小的量级里挑不超过 <see cref="MergeFanIn"/> 个 run，归并后追加为新 run，
    /// 旧 run 变成垃圾空间；等垃圾过半再触发一次全量重写回收。每个条目被重写的次数与层数成正比（对数级）。
    /// </remarks>
    private void MergeRuns()
    {
        if (_readOnly) return;

        Flush();
        if (_runs.Count < 2) return;

        var picks = SelectMergeGroup();
        if (picks.Count < 2) return;

        // 先写新 run 并登记到元数据，再把旧 run 标记失效：
        // 任何崩溃点都能从元数据引用的 run 里读回全部条目，最坏只是重复合并
        var merged = AppendMergedRun(picks);

        AppendRunSlot(merged);
        _meta.ArenaEnd = merged.EndOffset;
        SaveMeta();
        _region.Flush(false);

        foreach (var pick in picks)
        {
            var index = _runs.IndexOf(pick);
            if (index < 0) continue;

            var slotIndex = _runSlots[index];
            var slot = (Int64)RunIndexOffset + (Int64)slotIndex * RunIndexEntrySize;
            _region.WriteInt64(slot, 0);
            _region.WriteInt64(slot + 8, 0);
            _region.WriteInt64(slot + 16, 0);
            _region.WriteInt64(slot + 24, 0);
            _freeSlots.Add(slotIndex);
        }
        _region.Flush(false);

        foreach (var pick in picks)
        {
            var index = _runs.IndexOf(pick);
            if (index < 0) continue;

            _runs.RemoveAt(index);
            _runSlots.RemoveAt(index);
        }

        if (ShouldCompact()) Compact();
    }

    /// <summary>在 run 索引末尾登记一个 run，优先复用已失效的槽位</summary>
    private void AppendRunSlot(RunInfo run)
    {
        Int32 slotIndex;
        if (_freeSlots.Count > 0)
        {
            slotIndex = _freeSlots[^1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
        }
        else
        {
            slotIndex = _runSlotCount++;
            _meta.RunCount = _runSlotCount;
        }

        var slot = (Int64)RunIndexOffset + (Int64)slotIndex * RunIndexEntrySize;
        _region.WriteInt64(slot, run.Offset);
        _region.WriteInt64(slot + 8, run.EndOffset - run.Offset);
        _region.WriteInt64(slot + 16, run.EntryCount);
        _region.WriteInt64(slot + 24, 0);

        _runs.Add(run);
        _runSlots.Add(slotIndex);
    }

    /// <summary>挑选同一量级里最小的若干个 run，不足时退化为最小的两个</summary>
    private List<RunInfo> SelectMergeGroup()
    {
        var levels = new List<Int32>(_runs.Count);
        foreach (var run in _runs) levels.Add(SizeLevel(run.EntryCount));

        var bestLevel = Int32.MaxValue;
        foreach (var level in levels)
        {
            if (level < bestLevel && CountLevel(levels, level) >= 2) bestLevel = level;
        }

        var picks = new List<RunInfo>(MergeFanIn);
        if (bestLevel != Int32.MaxValue)
        {
            var indexes = new List<Int32>(_runs.Count);
            for (var i = 0; i < _runs.Count; i++)
            {
                if (levels[i] == bestLevel) indexes.Add(i);
            }

            // 同量级里取条目最少的，控制单次重写量
            indexes.Sort((a, b) => _runs[a].EntryCount.CompareTo(_runs[b].EntryCount));
            for (var i = 0; i < indexes.Count && picks.Count < MergeFanIn; i++) picks.Add(_runs[indexes[i]]);

            return picks;
        }

        // 各量级都只有一个 run：直接取最小的两个
        var first = -1;
        var second = -1;
        for (var i = 0; i < _runs.Count; i++)
        {
            if (first < 0 || _runs[i].EntryCount < _runs[first].EntryCount)
            {
                second = first;
                first = i;
            }
            else if (second < 0 || _runs[i].EntryCount < _runs[second].EntryCount)
                second = i;
        }

        if (first >= 0) picks.Add(_runs[first]);
        if (second >= 0) picks.Add(_runs[second]);

        return picks;
    }

    /// <summary>统计已出现过的指定量级个数</summary>
    private static Int32 CountLevel(List<Int32> levels, Int32 level)
    {
        var count = 0;
        foreach (var item in levels)
        {
            if (item == level) count++;
        }

        return count;
    }

    /// <summary>按条目数取量级（2 的幂次下标）</summary>
    private static Int32 SizeLevel(Int64 entryCount)
    {
        var level = 0;
        var size = 1L;
        while (size < entryCount && level < 62)
        {
            size <<= 1;
            level++;
        }

        return level;
    }

    /// <summary>把选中的 run 归并后追加为单个新 run，返回新 run 信息</summary>
    private RunInfo AppendMergedRun(List<RunInfo> sources)
    {
        // 第一遍：统计条目数与条目区长度
        var entryCount = 0;
        var entriesLength = 0L;
        MergeRunEntries(sources, cursor =>
        {
            entryCount++;
            entriesLength += Align(EntryHeaderSize + cursor.KeyLength);
            return true;
        });

        var runOffset = Align(_meta.ArenaEnd);
        var entriesOffset = runOffset + RunHeaderSize;
        var directoryOffset = entriesOffset + entriesLength;
        var runLength = RunHeaderSize + entriesLength + entryCount * sizeof(Int64);
        var end = runOffset + runLength;

        _region.EnsureCapacity(end);

        // 先写 run 数据，再改 run 索引与元数据：崩溃时元数据仍指向旧 run
        var header = new Byte[RunHeaderSize];
        WriteUInt32(header, RunMagicOffset, RunMagic);
        header[RunVersionOffset] = FileVersion;
        WriteInt64(header, RunEntryCountOffset, entryCount);
        WriteInt64(header, RunEntriesOffset, entriesOffset);
        WriteInt64(header, RunEntriesLengthOffset, entriesLength);
        _region.Write(runOffset, header, 0, header.Length);

        // 第二遍：写条目与偏移目录
        var position = entriesOffset;
        var index = 0;
        MergeRunEntries(sources, cursor =>
        {
            WriteCursorEntry(position, cursor);
            _region.WriteInt64(directoryOffset + (Int64)index * sizeof(Int64), position);
            position += Align(EntryHeaderSize + cursor.KeyLength);
            index++;
            return true;
        });

        _region.Flush(false);

        return new RunInfo(runOffset, entryCount, entriesOffset, entriesLength, directoryOffset);
    }

    /// <summary>对指定 run 做升序归并，逐条回调（保留全部版本，包括删除标记）</summary>
    private void MergeRunEntries(List<RunInfo> sources, Func<Cursor, Boolean> visitor)
    {
        var cursors = new List<Cursor>(sources.Count);
        foreach (var run in sources)
        {
            var cursor = CreateRunCursor(run, null, true, false, 0, false, 0, false);
            if (cursor != null) cursors.Add(cursor);
        }

        while (true)
        {
            Cursor? best = null;
            foreach (var cursor in cursors)
            {
                if (!cursor.HasValue) continue;
                if (best == null)
                {
                    best = cursor;
                    continue;
                }

                var cmp = CompareCursor(cursor, best);
                if (cmp < 0 || (cmp == 0 && cursor.CreatedTx < best.CreatedTx)) best = cursor;
            }

            if (best == null) break;
            if (!visitor(best)) return;

            Advance(best);
        }
    }

    /// <summary>把游标当前条目写入指定位置（键长取自游标，不写满容量）</summary>
    private void WriteCursorEntry(Int64 offset, Cursor cursor)
    {
        var size = Align(EntryHeaderSize + cursor.KeyLength);
        EnsureEntryBuffer((Int32)size);

        var buffer = _entryBuffer;
        WriteInt64(buffer, EntrySortPrefix, cursor.SortPrefix);
        WriteInt64(buffer, EntryCreatedTx, (Int64)cursor.CreatedTx);
        WriteInt64(buffer, EntryDeletedTx, (Int64)cursor.DeletedTx);
        WriteInt64(buffer, EntryDataOffset, cursor.DataOffset);
        WriteInt32(buffer, EntryDataLength, cursor.DataLength);
        WriteInt32(buffer, EntryFlags, cursor.HasPrefix ? EntryFlagHasPrefix : 0);
        WriteInt32(buffer, EntryKeyLength, cursor.KeyLength);
        Array.Copy(cursor.Key, 0, buffer, EntryHeaderSize, cursor.KeyLength);

        // 对齐区的残留字节清零，保证同一份数据重复写出结果一致
        var used = EntryHeaderSize + cursor.KeyLength;
        if (size > used) Array.Clear(buffer, used, (Int32)size - used);

        _region.Write(offset, buffer, 0, (Int32)size);
    }

    /// <summary>确保条目写缓冲足够大</summary>
    private void EnsureEntryBuffer(Int32 length)
    {
        if (_entryBuffer.Length >= length) return;

        var capacity = _entryBuffer.Length;
        while (capacity < length) capacity *= 2;
        _entryBuffer = new Byte[capacity];
    }
    /// <summary>垃圾空间是否已过半（含最小阈值），需要全量重写回收</summary>
    private Boolean ShouldCompact()
    {
        var live = (Int64)ArenaStart;
        foreach (var run in _runs) live += run.EndOffset - run.Offset;

        var garbage = _meta.ArenaEnd - live;
        if (garbage <= 0) return false;

        return garbage > Math.Max(MinCompactGarbage, live) * 3;
    }

    /// <summary>归并所有 run，丢弃旧版本与已删除条目，重写文件</summary>
    public void Compact()
    {
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot compact index file in read-only mode: {0}", FilePath);

        Flush();
        if (_runs.Count <= 1) return;

        var path = FilePath;
        var tempPath = path + ".compact";

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var target = Open(tempPath, _codec, readOnly: false, flushEntries: _flushEntries))
            {
                target.RewriteSingleRun(this, _meta.AppliedOffset);
            }

            _region.Dispose();
            try
            {
                File.Delete(path);
                File.Move(tempPath, path);
            }
            finally
            {
                _region = new MmfRegion(path, false);
                _region.Open();
                Load();
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 残留临时文件不影响下次压缩
            }
        }

    }

    /// <summary>把来源索引的存活条目重写为单个 run</summary>
    private void RewriteSingleRun(MmfOrderedIndex source, Int64 appliedOffset)
    {
        // 单遍写：先占位 run 头，边归并边写条目，最后写偏移目录并回填头部
        var runOffset = (Int64)ArenaStart;
        var entriesOffset = runOffset + RunHeaderSize;
        var position = entriesOffset;
        var entryCount = 0;
        var directory = new List<Int64>();

        _region.EnsureCapacity(entriesOffset + RunHeaderSize);
        var header = new Byte[RunHeaderSize];
        WriteUInt32(header, RunMagicOffset, RunMagic);
        header[RunVersionOffset] = FileVersion;
        _region.Write(runOffset, header, 0, header.Length);

        source.MergeScan(null, true, null, true, false, 0, entry =>
        {
            var size = Align(EntryHeaderSize + entry.Key.Length);
            _region.EnsureCapacity(position + size);

            WriteEntry(position, entry);
            directory.Add(position);
            position += size;
            entryCount++;

            return true;
        });

        var directoryOffset = position;
        var entriesLength = directoryOffset - entriesOffset;
        var runLength = RunHeaderSize + entriesLength + entryCount * sizeof(Int64);

        _region.EnsureCapacity(directoryOffset + entryCount * sizeof(Int64));
        for (var i = 0; i < directory.Count; i++) _region.WriteInt64(directoryOffset + (Int64)i * sizeof(Int64), directory[i]);

        WriteInt64(header, RunEntryCountOffset, entryCount);
        WriteInt64(header, RunEntriesOffset, entriesOffset);
        WriteInt64(header, RunEntriesLengthOffset, entriesLength);
        _region.Write(runOffset, header, 0, header.Length);

        var runIndexSlot = (Int64)RunIndexOffset;
        _region.WriteInt64(runIndexSlot, runOffset);
        _region.WriteInt64(runIndexSlot + 8, runLength);
        _region.WriteInt64(runIndexSlot + 16, entryCount);
        _region.WriteInt64(runIndexSlot + 24, 0);

        _runs.Clear();
        _runs.Add(new RunInfo(runOffset, entryCount, entriesOffset, entriesLength, directoryOffset));
        _runSlots.Clear();
        _runSlots.Add(0);
        _freeSlots.Clear();
        _runSlotCount = 1;
        _buffer.Clear();
        _bufferSorted = true;

        _meta.RunCount = 1;
        _meta.ArenaEnd = runOffset + runLength;
        _meta.EntryCount = entryCount;
        _meta.AppliedOffset = appliedOffset;
        SaveMeta();
        _region.Flush();
    }

    #endregion

    #region 元数据
    /// <summary>有序索引元数据</summary>
    private struct OrderedMeta
    {
        public Int64 Sequence;
        public Int64 RunCount;
        public Int64 ArenaEnd;
        public Int64 AppliedOffset;
        public Int64 EntryCount;
    }

    /// <summary>run 定位信息</summary>
    private sealed class RunInfo
    {
        public readonly Int64 Offset;
        public readonly Int64 EntryCount;
        public readonly Int64 EntriesOffset;
        public readonly Int64 EntriesLength;
        public readonly Int64 DirectoryOffset;

        /// <summary>run 在文件中的结束位置（指向偏移目录之后）</summary>
        public Int64 EndOffset => DirectoryOffset + EntryCount * sizeof(Int64);

        public RunInfo(Int64 offset, Int64 entryCount, Int64 entriesOffset, Int64 entriesLength, Int64 directoryOffset)
        {
            Offset = offset;
            EntryCount = entryCount;
            EntriesOffset = entriesOffset;
            EntriesLength = entriesLength;
            DirectoryOffset = directoryOffset;
        }
    }

    /// <summary>初始化新文件</summary>
    private void Initialize()
    {
        var header = new FileHeader
        {
            Version = 1,
            FileType = FileType.Index,
            PageSize = 4096,
            CreateTime = DateTime.Now,
        };
        var headerBytes = new Byte[FileHeader.HeaderSize];
        header.Write(headerBytes);
        _region.Write(0, headerBytes, 0, headerBytes.Length);

        _region.EnsureCapacity(ArenaStart);

        _meta = new OrderedMeta
        {
            Sequence = 1,
            RunCount = 0,
            ArenaEnd = ArenaStart,
            AppliedOffset = 0,
            EntryCount = 0,
        };

        SaveMeta();
        _region.Flush(false);
    }

    /// <summary>加载元数据与 run 目录</summary>
    private void Load()
    {
        var headerBytes = _region.ReadBytes(0, FileHeader.HeaderSize);
        try
        {
            var header = FileHeader.Read(headerBytes);
            if (header.FileType != FileType.Index)
                throw new NovaException(ErrorCode.IncompatibleFileFormat, "Not an index file: {0}", FilePath);
        }
        catch (Exception ex) when (ex is not NovaException { Code: ErrorCode.IncompatibleFileFormat })
        {
            throw new NovaException(ErrorCode.FileCorrupted, "Invalid ordered index header: {0}", ex, FilePath);
        }

        var slot0 = TryReadMeta(MetaSlot0, out var meta0);
        var slot1 = TryReadMeta(MetaSlot1, out var meta1);

        if (!slot0 && !slot1)
            throw new NovaException(ErrorCode.FileCorrupted, "Ordered index metadata is corrupted: {0}", FilePath);

        _meta = !slot0 ? meta1 : !slot1 ? meta0 : meta0.Sequence >= meta1.Sequence ? meta0 : meta1;

        if (_meta.RunCount < 0 || _meta.RunCount > RunIndexCapacity || _meta.ArenaEnd < ArenaStart)
            throw new NovaException(ErrorCode.FileCorrupted, "Ordered index metadata is inconsistent: {0}", FilePath);

        _runs.Clear();
        _runSlots.Clear();
        _freeSlots.Clear();
        _runSlotCount = (Int32)_meta.RunCount;
        for (var i = 0; i < _meta.RunCount; i++)
        {
            var slot = RunIndexOffset + i * RunIndexEntrySize;
            var offset = _region.ReadInt64(slot);
            var length = _region.ReadInt64(slot + 8);
            var count = _region.ReadInt64(slot + 16);

            // 增量合并会把旧 run 的槽位清零，读到 0 直接跳过并留作复用
            if (offset == 0 && length == 0 && count == 0)
            {
                _freeSlots.Add(i);
                continue;
            }

            if (offset < ArenaStart || length <= RunHeaderSize || offset + length > _meta.ArenaEnd || count < 0)
                throw new NovaException(ErrorCode.FileCorrupted, "Ordered index run {0} is corrupted: {1}", i, FilePath);

            if (_region.ReadUInt32(offset + RunMagicOffset) != RunMagic || _region.ReadByte(offset + RunVersionOffset) != FileVersion)
                throw new NovaException(ErrorCode.FileCorrupted, "Ordered index run {0} header is invalid: {1}", i, FilePath);

            var entriesOffset = _region.ReadInt64(offset + RunEntriesOffset);
            var entriesLength = _region.ReadInt64(offset + RunEntriesLengthOffset);
            var directoryOffset = entriesOffset + entriesLength;

            if (entriesOffset != offset + RunHeaderSize || directoryOffset + count * sizeof(Int64) > offset + length)
                throw new NovaException(ErrorCode.FileCorrupted, "Ordered index run {0} layout is invalid: {1}", i, FilePath);

            _runs.Add(new RunInfo(offset, count, entriesOffset, entriesLength, directoryOffset));
            _runSlots.Add(i);
        }
    }

    /// <summary>读取一个元数据槽</summary>
    private Boolean TryReadMeta(Int32 slotOffset, out OrderedMeta meta)
    {
        meta = default;

        var buffer = _region.ReadBytes(slotOffset, MetaSize);
        if (ReadUInt32(buffer, MetaMagic) != Magic) return false;
        if (buffer[MetaVersion] != FileVersion) return false;
        if (buffer[MetaKeyParts] != _codec.PartCount) return false;
        if (buffer[MetaKeyType] != (Byte)_codec.Types[0]) return false;

        var crc = ReadUInt32(buffer, MetaCrc);
        if (crc != Crc32.Compute(buffer, 0, MetaCrc)) return false;

        meta = new OrderedMeta
        {
            Sequence = ReadInt64(buffer, MetaSequence),
            RunCount = ReadInt64(buffer, MetaRunCount),
            ArenaEnd = ReadInt64(buffer, MetaArenaEnd),
            AppliedOffset = ReadInt64(buffer, MetaAppliedOffset),
            EntryCount = ReadInt64(buffer, MetaEntryCount),
        };

        return true;
    }

    /// <summary>保存元数据到槽（双槽交替）</summary>
    private void SaveMeta()
    {
        _meta.Sequence++;

        var slot = (_meta.Sequence & 1) == 0 ? MetaSlot0 : MetaSlot1;
        var buffer = new Byte[MetaSize];

        WriteUInt32(buffer, MetaMagic, Magic);
        buffer[MetaVersion] = FileVersion;
        buffer[MetaKeyParts] = (Byte)_codec.PartCount;
        buffer[MetaKeyType] = (Byte)_codec.Types[0];
        WriteInt64(buffer, MetaSequence, _meta.Sequence);
        WriteInt64(buffer, MetaRunCount, _meta.RunCount);
        WriteInt64(buffer, MetaArenaEnd, _meta.ArenaEnd);
        WriteInt64(buffer, MetaAppliedOffset, _meta.AppliedOffset);
        WriteInt64(buffer, MetaEntryCount, _meta.EntryCount);
        WriteUInt32(buffer, MetaCrc, Crc32.Compute(buffer, 0, MetaCrc));

        _region.Write(slot, buffer, 0, buffer.Length);
    }

    #endregion

    #region 辅助
    /// <summary>归并游标</summary>
    private sealed class Cursor
    {
        public Byte[] Key = new Byte[64];
        public Int32 KeyLength;
        public Int64 SortPrefix;
        public Boolean HasPrefix;
        public UInt64 CreatedTx;
        public UInt64 DeletedTx;
        public Int64 DataOffset;
        public Int32 DataLength;
        public Boolean HasValue;
        public Int32 Position;
        public RunInfo? Run;
        public readonly Int32 Direction;

        public Cursor(Boolean descending)
        {
            Direction = descending ? -1 : 1;
        }

        /// <summary>确保键缓冲足够大</summary>
        public void EnsureKey(Int32 length)
        {
            if (Key.Length >= length) return;

            var capacity = Key.Length;
            while (capacity < length) capacity *= 2;
            Key = new Byte[capacity];
        }

        /// <summary>转换为条目。键引用游标缓冲，仅在回调期间有效</summary>
        public OrderedIndexEntry ToEntry() => new()
        {
            Key = Key,
            SortPrefix = SortPrefix,
            HasPrefix = HasPrefix,
            CreatedTx = CreatedTx,
            DeletedTx = DeletedTx,
            DataOffset = DataOffset,
            DataLength = DataLength,
        };
    }

    /// <summary>把写缓冲按索引键排序</summary>
    private void SortBuffer()
    {
        if (_bufferSorted) return;

        _buffer.Sort((a, b) =>
        {
            var cmp = _codec.Compare(a.Key, 0, a.Key.Length, a.HasPrefix, a.SortPrefix, b.Key, 0, b.Key.Length, b.HasPrefix, b.SortPrefix);
            if (cmp != 0) return cmp;

            return a.CreatedTx.CompareTo(b.CreatedTx);
        });
        _bufferSorted = true;
    }

    /// <summary>确保探测缓冲足够大</summary>
    private void EnsureProbeBuffer(Int32 length)
    {
        if (_probeBuffer.Length >= length) return;

        var capacity = _probeBuffer.Length;
        while (capacity < length) capacity *= 2;
        _probeBuffer = new Byte[capacity];
    }

    /// <summary>写入一个条目</summary>
    private void WriteEntry(Int64 offset, in OrderedIndexEntry entry)
    {
        var size = Align(EntryHeaderSize + entry.Key.Length);
        EnsureEntryBuffer((Int32)size);

        var buffer = _entryBuffer;
        WriteInt64(buffer, EntrySortPrefix, entry.SortPrefix);
        WriteInt64(buffer, EntryCreatedTx, (Int64)entry.CreatedTx);
        WriteInt64(buffer, EntryDeletedTx, (Int64)entry.DeletedTx);
        WriteInt64(buffer, EntryDataOffset, entry.DataOffset);
        WriteInt32(buffer, EntryDataLength, entry.DataLength);
        WriteInt32(buffer, EntryFlags, entry.HasPrefix ? EntryFlagHasPrefix : 0);
        WriteInt32(buffer, EntryKeyLength, entry.Key.Length);
        Array.Copy(entry.Key, 0, buffer, EntryHeaderSize, entry.Key.Length);

        var used = EntryHeaderSize + entry.Key.Length;
        if (size > used) Array.Clear(buffer, used, (Int32)size - used);

        _region.Write(offset, buffer, 0, (Int32)size);
    }

    /// <summary>8 字节对齐</summary>
    private static Int64 Align(Int64 length) => (length + 7) & ~7L;

    /// <summary>小端读取 Int32</summary>
    private static Int32 ReadInt32(Byte[] buffer, Int32 offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    /// <summary>小端读取 Int64</summary>
    private static Int64 ReadInt64(Byte[] buffer, Int32 offset) =>
        buffer[offset] | ((Int64)buffer[offset + 1] << 8) | ((Int64)buffer[offset + 2] << 16) | ((Int64)buffer[offset + 3] << 24) |
        ((Int64)buffer[offset + 4] << 32) | ((Int64)buffer[offset + 5] << 40) | ((Int64)buffer[offset + 6] << 48) | ((Int64)buffer[offset + 7] << 56);

    /// <summary>小端写入 Int32</summary>
    private static void WriteInt32(Byte[] buffer, Int32 offset, Int32 value)
    {
        for (var i = 0; i < 4; i++) buffer[offset + i] = (Byte)(value >> (i * 8));
    }

    /// <summary>小端写入 Int64</summary>
    private static void WriteInt64(Byte[] buffer, Int32 offset, Int64 value)
    {
        for (var i = 0; i < 8; i++) buffer[offset + i] = (Byte)(value >> (i * 8));
    }

    /// <summary>小端读取 UInt32</summary>
    private static UInt32 ReadUInt32(Byte[] buffer, Int32 offset) =>
        (UInt32)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    /// <summary>小端写入 UInt32</summary>
    private static void WriteUInt32(Byte[] buffer, Int32 offset, UInt32 value)
    {
        buffer[offset] = (Byte)value;
        buffer[offset + 1] = (Byte)(value >> 8);
        buffer[offset + 2] = (Byte)(value >> 16);
        buffer[offset + 3] = (Byte)(value >> 24);
    }

    #endregion
}






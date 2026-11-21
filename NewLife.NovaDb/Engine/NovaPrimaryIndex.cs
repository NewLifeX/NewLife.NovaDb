using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Engine;

/// <summary>一次主键索引提交</summary>
internal readonly struct PrimaryCommit
{
    /// <summary>主键值</summary>
    public readonly Object Key;

    /// <summary>新版本；为空表示纯删除</summary>
    public readonly RowVersion? Put;

    /// <summary>被替换或删除的已提交版本；为空表示新增</summary>
    public readonly RowVersion? Replace;

    /// <summary>事务 ID</summary>
    public readonly UInt64 TxId;

    /// <summary>实例化提交项</summary>
    /// <param name="key">主键值</param>
    /// <param name="put">新版本</param>
    /// <param name="replace">被替换版本</param>
    /// <param name="txId">事务 ID</param>
    public PrimaryCommit(Object key, RowVersion? put, RowVersion? replace, UInt64 txId)
    {
        Key = key;
        Put = put;
        Replace = replace;
        TxId = txId;
    }
}

/// <summary>主键索引：MMF 哈希字典（等值）+ 磁盘有序段（范围/排序）</summary>
/// <remarks>
/// 已提交版本住在两个索引文件里，托管堆上不再保留全表结构：
/// - <c>.hidx</c>：主键 → 版本链，等值点查 O(1)
/// - <c>.oidx</c>：按键有序的不可变 run，范围/ORDER BY/全表顺序扫描
/// 版本记录只在事务提交时写入，读取时存活判定为"删除标记为 0"；
/// 未提交版本由 <see cref="NovaTable"/> 的写集负责，不在本类中。
/// </remarks>
internal sealed class NovaPrimaryIndex : IDisposable
{
    /// <summary>版本记录长度</summary>
    public const Int32 VersionRecordSize = 32;

    private readonly IndexKeyCodec _codec;
    private readonly Boolean _readOnly;

    private MmfHashIndex _hash;
    private MmfOrderedIndex _ordered;
    private Boolean _disposed;

    /// <summary>哈希索引文件路径</summary>
    public String HashPath => _hash.FilePath;

    /// <summary>有序索引文件路径</summary>
    public String OrderedPath => _ordered.FilePath;

    /// <summary>已提交主键数量</summary>
    public Int64 KeyCount => _hash.KeyCount;

    /// <summary>主键数据类型</summary>
    public DataType PrimaryKeyType => _codec.Types[0];

    /// <summary>哈希索引已应用偏移</summary>
    public Int64 HashAppliedOffset => _hash.AppliedOffset;

    /// <summary>有序索引已应用偏移</summary>
    public Int64 OrderedAppliedOffset => _ordered.AppliedOffset;

    private NovaPrimaryIndex(MmfHashIndex hash, MmfOrderedIndex ordered, IndexKeyCodec codec, DataType pkType, Boolean readOnly)
    {
        _hash = hash;
        _ordered = ordered;
        _codec = codec;
        _readOnly = readOnly;
    }

    /// <summary>打开主键索引，文件缺失或损坏时抛异常，由调用方重建</summary>
    /// <param name="hashPath">哈希索引文件路径</param>
    /// <param name="orderedPath">有序索引文件路径</param>
    /// <param name="dataCodec">值编解码器</param>
    /// <param name="pkType">主键数据类型</param>
    /// <param name="readOnly">是否只读</param>
    /// <returns>索引实例</returns>
    public static NovaPrimaryIndex Open(String hashPath, String orderedPath, IDataCodec dataCodec, DataType pkType, Boolean readOnly = false)
    {
        var codec = new IndexKeyCodec(dataCodec, [pkType], ComparableObject.CompareValues);

        var hash = MmfHashIndex.Open(hashPath, codec, readOnly);
        try
        {
            var ordered = MmfOrderedIndex.Open(orderedPath, codec, readOnly);

            return new NovaPrimaryIndex(hash, ordered, codec, pkType, readOnly);
        }
        catch
        {
            hash.Dispose();
            throw;
        }
    }

    /// <summary>重建索引文件（删除后重新创建）</summary>
    /// <param name="hashPath">哈希索引文件路径</param>
    /// <param name="orderedPath">有序索引文件路径</param>
    /// <param name="dataCodec">值编解码器</param>
    /// <param name="pkType">主键数据类型</param>
    /// <returns>空索引实例</returns>
    public static NovaPrimaryIndex Recreate(String hashPath, String orderedPath, IDataCodec dataCodec, DataType pkType)
    {
        if (File.Exists(hashPath)) File.Delete(hashPath);
        if (File.Exists(orderedPath)) File.Delete(orderedPath);

        return Open(hashPath, orderedPath, dataCodec, pkType);
    }

    /// <summary>编码主键</summary>
    /// <param name="key">主键值</param>
    /// <returns>编码后的键</returns>
    public Byte[] EncodeKey(Object key)
    {
        var buffer = new Byte[_codec.GetEncodedLength(key)];
        _codec.Encode(key, buffer, 0);

        return buffer;
    }

    /// <summary>解码主键</summary>
    /// <param name="key">编码后的键</param>
    /// <returns>主键值</returns>
    public Object DecodeKey(Byte[] key)
    {
        var value = _codec.Decode(key, 0, key.Length);
        if (value == null) throw new NovaException(ErrorCode.FileCorrupted, "Primary key decoded as NULL: {0}", _hash.FilePath);

        return value;
    }

    /// <summary>查找已提交的存活版本</summary>
    /// <param name="key">主键值</param>
    /// <param name="version">命中的版本</param>
    /// <returns>是否存在</returns>
    public Boolean TryGetCommitted(Object key, out RowVersion version)
    {
        version = null!;

        var keyBytes = EncodeKey(key);
        var value = _hash.GetFirstValue(keyBytes);
        while (value != 0)
        {
            var record = _hash.ReadValue(value);
            var deletedTx = ReadUInt64(record, 8);
            if (deletedTx == 0)
            {
                version = new RowVersion(ReadUInt64(record, 0), key, null)
                {
                    DataOffset = ReadInt64(record, 16),
                    DataLength = ReadInt32(record, 24),
                };
                version.IndexValueOffset = value;

                return true;
            }

            value = _hash.GetNextValue(value);
        }

        return false;
    }

    /// <summary>扫描已提交的存活版本，按键升序或降序输出</summary>
    /// <param name="from">下界主键值，null 表示不限</param>
    /// <param name="includeFrom">是否包含下界</param>
    /// <param name="to">上界主键值，null 表示不限</param>
    /// <param name="includeTo">是否包含上界</param>
    /// <param name="descending">是否降序</param>
    /// <param name="maxRows">最大输出条数，0 表示不限</param>
    /// <param name="onVersion">版本回调（主键值、版本），返回 false 提前结束</param>
    public void Scan(Object? from, Boolean includeFrom, Object? to, Boolean includeTo, Boolean descending, Int32 maxRows, Func<Object, RowVersion, Boolean> onVersion)
    {
        if (onVersion == null) throw new ArgumentNullException(nameof(onVersion));

        var fromKey = from == null ? null : EncodeKey(from);
        var toKey = to == null ? null : EncodeKey(to);

        _ordered.Scan(fromKey, includeFrom, toKey, includeTo, descending, maxRows, entry =>
        {
            var key = DecodeKey(entry.Key);
            var version = new RowVersion(entry.CreatedTx, key, null)
            {
                DeletedByTx = entry.DeletedTx,
                DataOffset = entry.DataOffset,
                DataLength = entry.DataLength,
            };

            return onVersion(key, version);
        });
    }

    /// <summary>批量应用已提交变更：写哈希索引、有序段，并给被替换版本打删除标记</summary>
    /// <param name="commits">提交项</param>
    /// <param name="appliedOffset">应用后的 rlog 偏移</param>
    public void Apply(IList<PrimaryCommit> commits, Int64 appliedOffset)
    {
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write primary index in read-only mode: {0}", _hash.FilePath);
        if (commits == null) throw new ArgumentNullException(nameof(commits));
        if (commits.Count == 0) return;

        var hashChanges = new List<MmfHashChange>(commits.Count * 2);
        var orderedEntries = new List<OrderedIndexEntry>(commits.Count * 2);

        foreach (var commit in commits)
        {
            var key = EncodeKey(commit.Key);
            var (hasPrefix, sortPrefix) = GetSortInfo(commit.Key);

            if (commit.Replace != null && commit.Replace.IndexValueOffset > 0)
            {
                // 旧版本打删除标记：哈希记录原地补丁，有序段追加墓碑
                _hash.WriteValue(commit.Replace.IndexValueOffset, 8, EncodeUInt64(commit.TxId), 0, 8);
                orderedEntries.Add(new OrderedIndexEntry
                {
                    Key = key,
                    SortPrefix = sortPrefix,
                    HasPrefix = hasPrefix,
                    CreatedTx = commit.Replace.CreatedByTx,
                    DeletedTx = commit.TxId,
                    DataOffset = 0,
                    DataLength = 0,
                });
            }

            if (commit.Put != null)
            {
                var put = commit.Put;
                hashChanges.Add(MmfHashChange.Add(key, EncodeVersion(put), unique: true));
                orderedEntries.Add(new OrderedIndexEntry
                {
                    Key = key,
                    SortPrefix = sortPrefix,
                    HasPrefix = hasPrefix,
                    CreatedTx = put.CreatedByTx,
                    DeletedTx = 0,
                    DataOffset = put.DataOffset,
                    DataLength = put.DataLength,
                });
            }
        }

        if (hashChanges.Count > 0) _hash.Apply(hashChanges, appliedOffset);
        if (orderedEntries.Count > 0) _ordered.Add(orderedEntries, appliedOffset);
    }

    /// <summary>把索引完整刷新到磁盘（含有序段写缓冲）</summary>
    public void Flush()
    {
        _hash.Flush();
        _ordered.Flush();
    }

    /// <summary>只刷新哈希索引元数据，用于每次提交</summary>
    /// <remarks>有序段的写缓冲按条目数成批落盘，它的已应用偏移只在落 run 时推进</remarks>
    public void FlushMeta() => _hash.Flush();

    /// <summary>标记已应用偏移（写缓冲为空时立即落盘）</summary>
    /// <param name="appliedOffset">已应用的 rlog 偏移</param>
    public void MarkApplied(Int64 appliedOffset)
    {
        _hash.Apply([], appliedOffset);
        _ordered.SetAppliedOffset(appliedOffset);
    }

    /// <summary>清空索引</summary>
    public void Clear()
    {
        if (_readOnly) return;

        _hash.Truncate();
        _ordered.Truncate();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _hash.Dispose();
        _ordered.Dispose();
        _disposed = true;
    }

    #region 辅助
    /// <summary>编码版本记录</summary>
    private static Byte[] EncodeVersion(RowVersion version)
    {
        var buffer = new Byte[VersionRecordSize];
        WriteUInt64(buffer, 0, version.CreatedByTx);
        WriteUInt64(buffer, 8, version.DeletedByTx);
        WriteInt64(buffer, 16, version.DataOffset);
        WriteInt32(buffer, 24, version.DataLength);

        return buffer;
    }

    /// <summary>编码为 8 字节小端数组</summary>
    private static Byte[] EncodeUInt64(UInt64 value)
    {
        var buffer = new Byte[8];
        WriteUInt64(buffer, 0, value);

        return buffer;
    }

    /// <summary>获取主键的可比较前缀</summary>
    private (Boolean Has, Int64 Prefix) GetSortInfo(Object key)
    {
        if (_codec.HasSortPrefix && _codec.TryGetSortPrefix(key, out var prefix)) return (true, prefix);

        return (false, 0);
    }

    private static void WriteUInt64(Byte[] buffer, Int32 offset, UInt64 value) => WriteInt64(buffer, offset, (Int64)value);

    private static UInt64 ReadUInt64(Byte[] buffer, Int32 offset) => (UInt64)ReadInt64(buffer, offset);

    private static void WriteInt64(Byte[] buffer, Int32 offset, Int64 value)
    {
        for (var i = 0; i < 8; i++) buffer[offset + i] = (Byte)(value >> (i * 8));
    }

    private static Int64 ReadInt64(Byte[] buffer, Int32 offset) =>
        buffer[offset] | ((Int64)buffer[offset + 1] << 8) | ((Int64)buffer[offset + 2] << 16) | ((Int64)buffer[offset + 3] << 24) |
        ((Int64)buffer[offset + 4] << 32) | ((Int64)buffer[offset + 5] << 40) | ((Int64)buffer[offset + 6] << 48) | ((Int64)buffer[offset + 7] << 56);

    private static void WriteInt32(Byte[] buffer, Int32 offset, Int32 value)
    {
        buffer[offset] = (Byte)value;
        buffer[offset + 1] = (Byte)(value >> 8);
        buffer[offset + 2] = (Byte)(value >> 16);
        buffer[offset + 3] = (Byte)(value >> 24);
    }

    private static Int32 ReadInt32(Byte[] buffer, Int32 offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    #endregion
}

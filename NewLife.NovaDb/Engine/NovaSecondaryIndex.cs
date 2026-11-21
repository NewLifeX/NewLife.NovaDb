using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;

namespace NewLife.NovaDb.Engine;

/// <summary>二级索引：MMF 哈希字典（等值） + 按需惰性构建的内存跳表（范围/排序）</summary>
/// <remarks>
/// - 等值、唯一性检查、索引计数直接读 `.hidx`，不依赖内存字典；
/// - 范围、前缀、`ORDER BY` 首次使用时才从哈希字典重建跳表，之后随写入增量维护；
/// - 写入立即生效（与旧实现一致：索引条目的可见性最终由主键可见性复核），回滚由调用方执行反向操作。
/// </remarks>
internal sealed class NovaSecondaryIndex : IDisposable
{
    private readonly IDataCodec _codec;
    private readonly IndexKeyCodec _keyCodec;
    private readonly DataType[] _keyTypes;
    private readonly DataType _pkType;
    private readonly Boolean _readOnly;

    private MmfHashIndex _hash;
    private SkipList<ComparableObject, List<ComparableObject>>? _ordered;
    private Boolean _disposed;

    /// <summary>索引名称</summary>
    public String Name { get; }

    /// <summary>索引定义</summary>
    public IndexDefinition Definition { get; }

    /// <summary>是否为唯一索引</summary>
    public Boolean IsUnique => Definition.IsUnique;

    /// <summary>索引键数量</summary>
    public Int64 KeyCount => _hash.KeyCount;

    /// <summary>索引文件已应用偏移</summary>
    public Int64 AppliedOffset => _hash.AppliedOffset;

    /// <summary>判断索引键与主键的声明类型是否与当前一致</summary>
    /// <param name="keyTypes">当前索引列类型</param>
    /// <param name="pkType">当前主键类型</param>
    /// <returns>类型一致返回 true，否则说明旧键编码不再可用，需要重建</returns>
    public Boolean MatchesTypes(DataType[] keyTypes, DataType pkType)
    {
        if (_pkType != pkType || _keyTypes.Length != keyTypes.Length) return false;

        for (var i = 0; i < keyTypes.Length; i++)
        {
            if (_keyTypes[i] != keyTypes[i]) return false;
        }

        return true;
    }

    /// <summary>内存有序表是否已经构建</summary>
    public Boolean IsOrderedLoaded => _ordered != null;

    private NovaSecondaryIndex(MmfHashIndex hash, IndexDefinition definition, IDataCodec codec, IndexKeyCodec keyCodec, DataType[] keyTypes, DataType pkType, Boolean readOnly)
    {
        _hash = hash;
        Definition = definition;
        Name = definition.IndexName;
        _codec = codec;
        _keyCodec = keyCodec;
        _keyTypes = [.. keyTypes];
        _pkType = pkType;
        _readOnly = readOnly;
    }

    /// <summary>打开二级索引</summary>
    /// <param name="path">哈希索引文件路径</param>
    /// <param name="definition">索引定义</param>
    /// <param name="codec">值编解码器</param>
    /// <param name="keyTypes">索引列类型</param>
    /// <param name="pkType">主键类型</param>
    /// <param name="readOnly">是否只读</param>
    /// <returns>索引实例</returns>
    public static NovaSecondaryIndex Open(String path, IndexDefinition definition, IDataCodec codec, DataType[] keyTypes, DataType pkType, Boolean readOnly = false)
    {
        var keyCodec = new IndexKeyCodec(codec, keyTypes, ComparableObject.CompareValues);
        var hash = MmfHashIndex.Open(path, keyCodec, readOnly);

        return new NovaSecondaryIndex(hash, definition, codec, keyCodec, keyTypes, pkType, readOnly);
    }

    /// <summary>重建索引文件</summary>
    /// <param name="path">哈希索引文件路径</param>
    /// <param name="definition">索引定义</param>
    /// <param name="codec">值编解码器</param>
    /// <param name="keyTypes">索引列类型</param>
    /// <param name="pkType">主键类型</param>
    /// <returns>空索引实例</returns>
    public static NovaSecondaryIndex Recreate(String path, IndexDefinition definition, IDataCodec codec, DataType[] keyTypes, DataType pkType)
    {
        if (File.Exists(path)) File.Delete(path);

        return Open(path, definition, codec, keyTypes, pkType);
    }

    #region 写入
    /// <summary>加入一条索引条目</summary>
    /// <param name="key">索引键（已规范化，单列为标量、联合索引为 Object[]）</param>
    /// <param name="primaryKey">主键值</param>
    public void Add(Object key, Object primaryKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaSecondaryIndex));

        _hash.Apply([MmfHashChange.Add(EncodeKey(key), EncodePrimaryKey(primaryKey), unique: true)]);

        if (_ordered != null) AddOrdered(key, primaryKey);
    }

    /// <summary>移除一条索引条目</summary>
    /// <param name="key">索引键</param>
    /// <param name="primaryKey">主键值</param>
    public void Remove(Object key, Object primaryKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaSecondaryIndex));

        _hash.Apply([MmfHashChange.RemoveValue(EncodeKey(key), EncodePrimaryKey(primaryKey))]);

        if (_ordered != null) RemoveOrdered(key, primaryKey);
    }

    /// <summary>批量加入（建索引/重建用）</summary>
    /// <param name="entries">条目集合</param>
    /// <param name="appliedOffset">应用后的 rlog 偏移，-1 表示不推进</param>
    public void AddRange(IEnumerable<(Object Key, Object PrimaryKey)> entries, Int64 appliedOffset = -1)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaSecondaryIndex));

        var changes = new List<MmfHashChange>(4096);
        foreach (var (key, pk) in entries)
        {
            changes.Add(MmfHashChange.Add(EncodeKey(key), EncodePrimaryKey(pk), unique: true));

            if (changes.Count >= 4096)
            {
                _hash.Apply(changes);
                changes.Clear();
            }
        }

        _hash.Apply(changes, appliedOffset);
    }

    /// <summary>提交：推进已应用偏移并刷盘</summary>
    /// <param name="appliedOffset">已应用的 rlog 偏移</param>
    public void Commit(Int64 appliedOffset)
    {
        if (_readOnly) return;

        _hash.Apply([], appliedOffset);
        _hash.Flush();
    }

    /// <summary>清空索引</summary>
    public void Clear()
    {
        if (_readOnly) return;

        _hash.Truncate();
        _ordered?.Clear();
    }

    /// <summary>死条目占比过半时重写索引文件，回收追加区空间</summary>
    /// <param name="appliedOffset">压缩后的 rlog 偏移</param>
    /// <param name="minDeadBytes">触发压缩的最小死字节数</param>
    /// <returns>是否执行了压缩</returns>
    /// <remarks>只能在全量基线刚写完时调用：压缩期间崩溃可由基线 + 空增量重新构建</remarks>
    public Boolean CompactIfNeeded(Int64 appliedOffset, Int64 minDeadBytes = 1 << 20)
    {
        if (_readOnly || _disposed) return false;

        var dead = _hash.DeadBytes;
        if (dead < minDeadBytes || dead * 2 < _hash.ArenaBytes) return false;

        _hash.Compact(null, appliedOffset);

        return true;
    }

    #endregion

    #region 读取
    /// <summary>按索引键取主键列表</summary>
    /// <param name="key">索引键</param>
    /// <returns>主键列表，未命中返回 null</returns>
    public List<Object>? TryGet(Object key)
    {
        var value = _hash.GetFirstValue(EncodeKey(key));
        if (value == 0) return null;

        var result = new List<Object>();
        while (value != 0)
        {
            result.Add(DecodePrimaryKey(_hash.ReadValue(value)));
            value = _hash.GetNextValue(value);
        }

        return result;
    }

    /// <summary>统计索引键下的条目数</summary>
    /// <param name="key">索引键</param>
    /// <returns>条目数</returns>
    public Int32 CountKey(Object key) => _hash.GetValueCount(EncodeKey(key));

    /// <summary>确保内存有序表已构建（首次范围/排序查询时从哈希字典重建）</summary>
    /// <returns>有序表</returns>
    public SkipList<ComparableObject, List<ComparableObject>> EnsureOrdered()
    {
        if (_ordered != null) return _ordered;

        var ordered = new SkipList<ComparableObject, List<ComparableObject>>();
        foreach (var (key, pk) in EnumerateAll())
        {
            var indexKey = new ComparableObject(key);
            var primaryKey = new ComparableObject(pk);

            if (!ordered.TryGetValue(indexKey, out var pkList))
            {
                pkList = [];
                ordered.Insert(indexKey, pkList);
            }

            pkList!.Add(primaryKey);
        }

        _ordered = ordered;

        return ordered;
    }

    /// <summary>枚举全部索引条目</summary>
    /// <returns>索引键与主键</returns>
    public IEnumerable<(Object Key, Object PrimaryKey)> EnumerateAll()
    {
        foreach (var (key, value) in _hash.Enumerate())
        {
            var indexKey = _keyCodec.Decode(key, 0, key.Length);
            if (indexKey == null) continue;

            yield return (indexKey, DecodePrimaryKey(value));
        }
    }

    /// <summary>按索引键分组枚举原始字节（持久化基线用）</summary>
    /// <returns>索引键字节与主键字节列表</returns>
    /// <remarks>哈希表同一键的值链连续，顺序扫描即可成组</remarks>
    public IEnumerable<(Byte[] Key, List<Byte[]> Values)> EnumerateGroups()
    {
        Byte[]? current = null;
        var values = new List<Byte[]>();

        foreach (var (key, value) in _hash.Enumerate())
        {
            if (current != null && !BytesEqual(current, key))
            {
                yield return (current, values);
                values = [];
            }

            current = key;
            values.Add(value);
        }

        if (current != null) yield return (current, values);
    }

    #endregion

    #region 有序表维护
    /// <summary>向内存有序表插入条目</summary>
    private void AddOrdered(Object key, Object primaryKey)
    {
        var ordered = _ordered!;
        var indexKey = new ComparableObject(key);
        var pk = new ComparableObject(primaryKey);

        if (!ordered.TryGetValue(indexKey, out var pkList))
        {
            pkList = [];
            ordered.Insert(indexKey, pkList);
        }

        pkList!.Add(pk);
    }

    /// <summary>从内存有序表移除条目</summary>
    private void RemoveOrdered(Object key, Object primaryKey)
    {
        var ordered = _ordered!;
        var indexKey = new ComparableObject(key);
        var pk = new ComparableObject(primaryKey);

        if (!ordered.TryGetValue(indexKey, out var pkList)) return;

        for (var i = pkList!.Count - 1; i >= 0; i--)
        {
            if (CompareKeys(pkList[i], pk) == 0)
            {
                pkList.RemoveAt(i);
                break;
            }
        }

        if (pkList.Count == 0) ordered.Remove(indexKey);
    }

    /// <summary>比较两个索引键</summary>
    private static Int32 CompareKeys(ComparableObject a, ComparableObject b) => a.CompareTo(b);

    #endregion

    #region 辅助
    /// <summary>编码索引键</summary>
    private Byte[] EncodeKey(Object key)
    {
        var buffer = new Byte[_keyCodec.GetEncodedLength(key)];
        _keyCodec.Encode(key, buffer, 0);

        return buffer;
    }

    /// <summary>编码主键</summary>
    private Byte[] EncodePrimaryKey(Object primaryKey) => _codec.Encode(primaryKey, _pkType);

    /// <summary>解码主键</summary>
    private Object DecodePrimaryKey(Byte[] value)
    {
        var pk = _codec.Decode(value, 0, _pkType);
        if (pk == null) throw new NovaException(ErrorCode.FileCorrupted, "Secondary index '{0}' contains a NULL primary key", Name);

        return pk;
    }

    /// <summary>比较两个字节数组</summary>
    private static Boolean BytesEqual(Byte[] a, Byte[] b)
    {
        if (a.Length != b.Length) return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        try { _hash.Flush(); } catch { }
        _hash.Dispose();
        _disposed = true;
    }

    #endregion
}

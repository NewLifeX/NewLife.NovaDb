using NewLife.NovaDb.Core;
using NewLife.Security;

namespace NewLife.NovaDb.Storage;

/// <summary>哈希索引变更类型</summary>
internal enum MmfHashChangeKind : Byte
{
    /// <summary>加入一个值</summary>
    Add = 1,

    /// <summary>移除指定的值</summary>
    RemoveValue = 2,

    /// <summary>移除整个键</summary>
    RemoveKey = 3,
}

/// <summary>哈希索引的一次变更</summary>
internal readonly struct MmfHashChange
{
    /// <summary>变更类型</summary>
    public readonly MmfHashChangeKind Kind;

    /// <summary>已编码的索引键</summary>
    public readonly Byte[] Key;

    /// <summary>值负载；<see cref="MmfHashChangeKind.RemoveKey"/> 时可为空</summary>
    public readonly Byte[]? Payload;

    /// <summary>同名值已存在时跳过（回放幂等用）</summary>
    public readonly Boolean Unique;

    /// <summary>实例化变更</summary>
    /// <param name="kind">变更类型</param>
    /// <param name="key">已编码的索引键</param>
    /// <param name="payload">值负载</param>
    /// <param name="unique">同名值已存在时跳过</param>
    public MmfHashChange(MmfHashChangeKind kind, Byte[] key, Byte[]? payload, Boolean unique = false)
    {
        Kind = kind;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Payload = payload;
        Unique = unique;
    }

    /// <summary>创建"加入值"变更</summary>
    /// <param name="key">已编码的索引键</param>
    /// <param name="payload">值负载</param>
    /// <param name="unique">同名值已存在时跳过</param>
    /// <returns>变更</returns>
    public static MmfHashChange Add(Byte[] key, Byte[] payload, Boolean unique = false) => new(MmfHashChangeKind.Add, key, payload, unique);

    /// <summary>创建"移除值"变更</summary>
    /// <param name="key">已编码的索引键</param>
    /// <param name="payload">值负载</param>
    /// <returns>变更</returns>
    public static MmfHashChange RemoveValue(Byte[] key, Byte[] payload) => new(MmfHashChangeKind.RemoveValue, key, payload);

    /// <summary>创建"移除键"变更</summary>
    /// <param name="key">已编码的索引键</param>
    /// <returns>变更</returns>
    public static MmfHashChange RemoveKey(Byte[] key) => new(MmfHashChangeKind.RemoveKey, key, null);
}

/// <summary>MMF 哈希字典：桶数组、键节点、值链全部住在映射文件里</summary>
/// <remarks>
/// 布局：
/// <code>
/// [FileHeader 32B][MetaSlot0 160B][MetaSlot1 160B][桶数组 Int64 × capacity][节点区（追加写）]
/// 键节点：[NextKey][Hash][KeyLength][ValueCount][ValueHead][ValueTail][key]
/// 值节点：[NextValue][Length][Flags][payload]
/// </code>
/// 查找全程只读映射视图，不构造托管节点对象；写入走"先预留节点空间、再挂链"的顺序，
/// 保证崩溃后旧结构仍然完整，回放时按幂等语义重放即可。
/// </remarks>
internal sealed class MmfHashIndex : IDisposable
{
    #region 常量
    private const UInt32 Magic = 0x58494831;

    // 版本 2 起元数据新增存活值计数，旧索引文件启动时按行日志重建一次
    private const Byte FileVersion = 2;

    /// <summary>元数据标志：存在存活值计数</summary>
    private const Byte MetaFlagLiveCount = 2;

    private const Int32 MetaSlot0 = FileHeader.HeaderSize;
    private const Int32 MetaSlot1 = MetaSlot0 + MetaSize;
    private const Int32 MetaSize = 160;
    private const Int32 BucketRegionStart = MetaSlot1 + MetaSize;

    private const Int32 MetaMagic = 0;
    private const Int32 MetaVersion = 4;
    private const Int32 MetaFlags = 5;
    private const Int32 MetaKeyParts = 6;
    private const Int32 MetaKeyType = 7;
    private const Int32 MetaSequence = 8;
    private const Int32 MetaCapacity = 16;
    private const Int32 MetaBucketOffset = 24;
    private const Int32 MetaArenaEnd = 32;
    private const Int32 MetaKeyCount = 40;
    private const Int32 MetaValueCount = 48;
    private const Int32 MetaDeadBytes = 56;
    private const Int32 MetaAppliedOffset = 64;
    private const Int32 MetaLiveCount = 72;
    private const Int32 MetaCrc = 152;

    private const Int32 KeyNextKey = 0;
    private const Int32 KeyHash = 8;
    private const Int32 KeyLength = 16;
    private const Int32 KeyValueCount = 20;
    private const Int32 KeyValueHead = 24;
    private const Int32 KeyValueTail = 32;
    private const Int32 KeyHeaderSize = 40;

    private const Int32 ValueNextValue = 0;
    private const Int32 ValueLength = 8;
    private const Int32 ValueFlags = 12;
    private const Int32 ValueHeaderSize = 16;

    /// <summary>值标志：已被标记删除（主键索引的旧版本）</summary>
    private const Int32 ValueFlagDeleted = 1;

    private const Int32 DefaultCapacity = 1024;
    private const Int32 MinCapacity = 16;
    private const Int32 GrowNumerator = 3;
    private const Int32 GrowDenominator = 4;
    #endregion

    #region 属性
    private readonly IndexKeyCodec _codec;
    private readonly Boolean _readOnly;
    private MmfRegion _region;
    private HashMeta _meta;
    private Int64 _writePosition;
    private Int64 _persistedOffset;
    private Byte[]? _keyBuffer;
    private Byte[]? _payloadBuffer;
    private Byte[] _nodeBuffer = new Byte[128];
    private Boolean _disposed;

    /// <summary>索引文件路径</summary>
    public String FilePath => _region.FilePath;

    /// <summary>存活键数量</summary>
    public Int64 KeyCount => _meta.KeyCount;

    /// <summary>存活值数量</summary>
    public Int64 ValueCount => _meta.ValueCount;

    /// <summary>已应用的 rlog 偏移</summary>
    public Int64 AppliedOffset => _meta.AppliedOffset;

    /// <summary>存活值数量（未被标记删除的值）</summary>
    public Int64 LiveCount => _meta.LiveCount;

    /// <summary>死字节数</summary>
    public Int64 DeadBytes => _meta.DeadBytes;

    /// <summary>追加区当前写入位置（含桶数组，用于判断死条目占比）</summary>
    public Int64 ArenaBytes => _writePosition;

    /// <summary>是否只读</summary>
    public Boolean ReadOnly => _readOnly;

    #endregion

    #region 构造
    private MmfHashIndex(MmfRegion region, IndexKeyCodec codec, HashMeta meta, Boolean readOnly)
    {
        _region = region;
        _codec = codec;
        _meta = meta;
        _writePosition = meta.ArenaEnd;
        _readOnly = readOnly;
    }

    /// <summary>打开或创建哈希索引</summary>
    /// <param name="path">文件路径</param>
    /// <param name="codec">键编解码器</param>
    /// <param name="readOnly">是否只读</param>
    /// <returns>索引实例</returns>
    public static MmfHashIndex Open(String path, IndexKeyCodec codec, Boolean readOnly = false)
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
            var index = new MmfHashIndex(region, codec, default, readOnly);
            if (isNew)
            {
                index.Initialize();
            }
            else
            {
                index.Load();
            }

            return index;
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>关闭索引</summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (!_readOnly) Flush(true);

        _region.Dispose();
        _disposed = true;
    }

    #endregion

    #region 读取
    /// <summary>查找键节点偏移</summary>
    /// <param name="key">已编码的键</param>
    /// <param name="nodeOffset">键节点偏移，未找到为 0</param>
    /// <returns>是否存在</returns>
    public Boolean TryGetKeyOffset(Byte[] key, out Int64 nodeOffset)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        nodeOffset = FindKey(key, ComputeHash(key));

        return nodeOffset != 0;
    }

    /// <summary>获取键的第一个值节点偏移</summary>
    /// <param name="key">已编码的键</param>
    /// <returns>值节点偏移，未找到返回 0</returns>
    public Int64 GetFirstValue(Byte[] key)
    {
        var node = FindKey(key, ComputeHash(key));

        return node == 0 ? 0 : _region.ReadInt64(node + KeyValueHead);
    }

    /// <summary>获取下一个值节点偏移</summary>
    /// <param name="valueOffset">当前值节点偏移</param>
    /// <returns>下一个值节点偏移，结束返回 0</returns>
    public Int64 GetNextValue(Int64 valueOffset)
    {
        if (valueOffset <= 0) return 0;

        return _region.ReadInt64(valueOffset + ValueNextValue);
    }

    /// <summary>获取值负载长度</summary>
    /// <param name="valueOffset">值节点偏移</param>
    /// <returns>负载字节数</returns>
    public Int32 GetValueLength(Int64 valueOffset)
    {
        if (valueOffset <= 0) return 0;

        return _region.ReadInt32(valueOffset + ValueLength);
    }

    /// <summary>读取值负载</summary>
    /// <param name="valueOffset">值节点偏移</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">目标起始下标</param>
    /// <param name="count">字节数</param>
    public void ReadValue(Int64 valueOffset, Byte[] buffer, Int32 offset, Int32 count)
    {
        if (valueOffset <= 0) throw new ArgumentOutOfRangeException(nameof(valueOffset));

        var length = _region.ReadInt32(valueOffset + ValueLength);
        if (count < 0 || count > length) throw new ArgumentOutOfRangeException(nameof(count));

        _region.Read(valueOffset + ValueHeaderSize, buffer, offset, count);
    }

    /// <summary>读取值负载并返回数组</summary>
    /// <param name="valueOffset">值节点偏移</param>
    /// <returns>负载数据</returns>
    public Byte[] ReadValue(Int64 valueOffset)
    {
        var buffer = new Byte[GetValueLength(valueOffset)];
        ReadValue(valueOffset, buffer, 0, buffer.Length);

        return buffer;
    }

    /// <summary>原地改写值负载的一部分</summary>
    /// <param name="valueOffset">值节点偏移</param>
    /// <param name="payloadOffset">负载内偏移</param>
    /// <param name="buffer">源缓冲区</param>
    /// <param name="offset">源起始下标</param>
    /// <param name="count">字节数</param>
    public void WriteValue(Int64 valueOffset, Int32 payloadOffset, Byte[] buffer, Int32 offset, Int32 count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfHashIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", FilePath);
        if (valueOffset <= 0) throw new ArgumentOutOfRangeException(nameof(valueOffset));

        var length = _region.ReadInt32(valueOffset + ValueLength);
        if (payloadOffset < 0 || count < 0 || payloadOffset + count > length)
            throw new ArgumentOutOfRangeException(nameof(payloadOffset));

        _region.Write(valueOffset + ValueHeaderSize + payloadOffset, buffer, offset, count);
    }

    /// <summary>清空全部键值（保留桶数量与文件）</summary>
    public void Truncate()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfHashIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot truncate index file in read-only mode: {0}", FilePath);

        ZeroRegion(_meta.BucketOffset, _meta.Capacity * sizeof(Int64));

        _meta.KeyCount = 0;
        _meta.ValueCount = 0;
        _meta.LiveCount = 0;
        _meta.DeadBytes = 0;
        _meta.AppliedOffset = 0;
        _persistedOffset = 0;
        _meta.ArenaEnd = _meta.BucketOffset + _meta.Capacity * sizeof(Int64);
        _writePosition = _meta.ArenaEnd;

        SaveMeta();
        _region.Flush();
    }

    /// <summary>获取键的存活值数量</summary>
    /// <param name="key">已编码的键</param>
    /// <returns>值数量</returns>
    public Int32 GetValueCount(Byte[] key)
    {
        var node = FindKey(key, ComputeHash(key));

        return node == 0 ? 0 : _region.ReadInt32(node + KeyValueCount);
    }

    #endregion

    #region 写入
    /// <summary>批量应用变更</summary>
    /// <param name="changes">变更列表</param>
    /// <param name="appliedOffset">应用后的 rlog 偏移，-1 表示不推进</param>
    /// <remarks>
    /// 顺序：先按最坏情况预留节点空间并落盘元数据，再写节点与挂链，最后落最终元数据。
    /// 崩溃发生在第二步时旧结构完好，回放会重新应用；发生在第三步时仅浪费预留空间。
    /// </remarks>
    public void Apply(IList<MmfHashChange> changes, Int64 appliedOffset = -1)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfHashIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", FilePath);
        if (changes == null) throw new ArgumentNullException(nameof(changes));

        if (changes.Count == 0)
        {
            if (appliedOffset >= 0)
            {
                // 只推进内存偏移；持久化偏移等到 Flush 时与节点一起落盘，避免每提交一次刷盘
                _meta.AppliedOffset = appliedOffset;
            }
            return;
        }

        // 1) 预留空间并落盘，保证回放不会覆盖已挂链的节点
        var reserved = 0L;
        foreach (var change in changes)
        {
            if (change.Kind != MmfHashChangeKind.Add) continue;

            reserved += Align(KeyHeaderSize + change.Key.Length) + Align(ValueHeaderSize + change.Payload!.Length);
        }

        var reservedStart = _writePosition;
        if (reserved > 0)
        {
            _writePosition += reserved;
            _meta.ArenaEnd = _writePosition;
            SaveMeta();

            // 节点就写在预留区里；预留区之外不允许上移，否则每批都会留下一整份死区
            _writePosition = reservedStart;
        }

        // 2) 写节点并挂链
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case MmfHashChangeKind.Add:
                    ApplyAdd(change);
                    break;

                case MmfHashChangeKind.RemoveValue:
                    ApplyRemoveValue(change);
                    break;

                case MmfHashChangeKind.RemoveKey:
                    ApplyRemoveKey(change);
                    break;
            }
        }

        // 预留多于实际使用时记为死区，由压缩回收；_writePosition 已停在真实使用量处
        if (reserved > 0)
        {
            var used = _writePosition - reservedStart;
            if (used < reserved)
            {
                var wasted = reserved - used;
                _meta.DeadBytes += wasted;
                _meta.ArenaEnd = _writePosition;
            }
        }

        // 3) 载重过高时扩容
        if (_meta.KeyCount >= _meta.Capacity * GrowNumerator / GrowDenominator)
            Rehash(_meta.Capacity * 2);

        // 4) 落最终元数据
        if (appliedOffset >= 0) _meta.AppliedOffset = appliedOffset;
        _meta.ArenaEnd = _writePosition;
        SaveMeta();
    }

    /// <summary>移除键</summary>
    /// <param name="key">已编码的键</param>
    /// <returns>是否移除成功</returns>
    public Boolean RemoveKey(Byte[] key)
    {
        Apply([MmfHashChange.RemoveKey(key)]);

        return true;
    }

    /// <summary>把映射内容刷到磁盘，并推进持久化的已应用偏移</summary>
    /// <remarks>元数据中的偏移只在刷盘时前进：崩溃后偏移落后于行日志，启动时按行日志重建</remarks>
    public void Flush(Boolean syncView = false)
    {
        if (_persistedOffset != _meta.AppliedOffset)
        {
            _persistedOffset = _meta.AppliedOffset;
            SaveMeta();
        }

        _region.Flush(syncView);
    }

    /// <summary>只推进内存中的已应用偏移，不刷盘</summary>
    /// <param name="appliedOffset">已应用的 rlog 偏移</param>
    public void MarkApplied(Int64 appliedOffset)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfHashIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", FilePath);
        if (appliedOffset >= 0) _meta.AppliedOffset = appliedOffset;
    }

    #endregion

    #region 压缩
    /// <summary>压缩索引：只保留需要的条目并重写文件</summary>
    /// <param name="keep">保留判定，返回 false 的条目被丢弃；为空表示全部保留</param>
    /// <param name="appliedOffset">压缩后的 rlog 偏移，-1 表示沿用当前值</param>
    /// <remarks>哈希表不知道负载语义，逻辑删除的版本必须由调用方通过 <paramref name="keep"/> 过滤，否则会被当成存活条目重写</remarks>
    public void Compact(Func<Byte[], Byte[], Boolean>? keep = null, Int64 appliedOffset = -1)
    {
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot compact index file in read-only mode: {0}", FilePath);

        var path = FilePath;
        var tempPath = path + ".compact";

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            var batch = new List<MmfHashChange>(4096);
            var total = 0L;
            var lastOffset = -1L;
            using (var target = Build(tempPath, _codec))
            {
                foreach (var (key, payload) in Enumerate())
                {
                    if (keep != null && !keep(key, payload)) continue;

                    batch.Add(MmfHashChange.Add(key, payload));
                    total++;

                    if (batch.Count >= 4096)
                    {
                        target.Apply(batch);
                        batch.Clear();
                    }
                }

                if (appliedOffset >= 0) lastOffset = appliedOffset;
                target.Apply(batch, lastOffset);
                target.Flush();
            }

            // Windows 下存在映射视图时不能替换文件，先释放
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

    /// <summary>枚举全部键值（压缩与重建用）</summary>
    /// <returns>键值序列</returns>
    /// <remarks>每次产出一份键与负载的副本，仅在低频路径使用</remarks>
    public IEnumerable<(Byte[] Key, Byte[] Payload)> Enumerate()
    {
        for (var bucket = 0L; bucket < _meta.Capacity; bucket++)
        {
            var node = _region.ReadInt64(_meta.BucketOffset + bucket * sizeof(Int64));

            while (node != 0)
            {
                var keyLength = _region.ReadInt32(node + KeyLength);
                var key = _region.ReadBytes(node + KeyHeaderSize, keyLength);

                var value = _region.ReadInt64(node + KeyValueHead);
                while (value != 0)
                {
                    var payload = ReadValue(value);
                    yield return (key, payload);

                    value = _region.ReadInt64(value + ValueNextValue);
                }

                node = _region.ReadInt64(node + KeyNextKey);
            }
        }
    }

    /// <summary>创建一个空的哈希索引文件</summary>
    /// <param name="path">文件路径</param>
    /// <param name="codec">键编解码器</param>
    /// <returns>索引实例</returns>
    public static MmfHashIndex Build(String path, IndexKeyCodec codec)
    {
        if (File.Exists(path)) File.Delete(path);

        return Open(path, codec);
    }

    #endregion

    #region 元数据
    /// <summary>哈希索引元数据</summary>
    private struct HashMeta
    {
        public Int64 Sequence;
        public Int64 Capacity;
        public Int64 BucketOffset;
        public Int64 ArenaEnd;
        public Int64 KeyCount;
        public Int64 ValueCount;
        public Int64 DeadBytes;
        public Int64 AppliedOffset;
        public Int64 LiveCount;
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

        var capacity = DefaultCapacity;
        var bucketOffset = (Int64)BucketRegionStart;
        var arenaEnd = bucketOffset + capacity * sizeof(Int64);

        _region.EnsureCapacity(arenaEnd);
        ZeroRegion(bucketOffset, capacity * sizeof(Int64));

        _meta = new HashMeta
        {
            Sequence = 1,
            Capacity = capacity,
            BucketOffset = bucketOffset,
            ArenaEnd = arenaEnd,
            KeyCount = 0,
            ValueCount = 0,
            DeadBytes = 0,
            AppliedOffset = 0,
            LiveCount = 0,
        };
        _writePosition = arenaEnd;
        _persistedOffset = 0;

        SaveMeta();
        _region.Flush();
    }

    /// <summary>加载元数据</summary>
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
            throw new NovaException(ErrorCode.FileCorrupted, "Invalid hash index header: {0}", ex, FilePath);
        }

        var slot0 = TryReadMeta(MetaSlot0, out var meta0);
        var slot1 = TryReadMeta(MetaSlot1, out var meta1);

        if (!slot0 && !slot1)
            throw new NovaException(ErrorCode.FileCorrupted, "Hash index metadata is corrupted: {0}", FilePath);

        _meta = !slot0 ? meta1 : !slot1 ? meta0 : meta0.Sequence >= meta1.Sequence ? meta0 : meta1;

        if (_meta.Capacity < MinCapacity || (Int64)BucketRegionStart > _meta.BucketOffset || _meta.ArenaEnd < _meta.BucketOffset + _meta.Capacity * sizeof(Int64))
            throw new NovaException(ErrorCode.FileCorrupted, "Hash index metadata is inconsistent: {0}", FilePath);

        _writePosition = _meta.ArenaEnd;
        _persistedOffset = _meta.AppliedOffset;
    }

    /// <summary>读取一个元数据槽</summary>
    /// <param name="slotOffset">槽偏移</param>
    /// <param name="meta">读到的元数据</param>
    /// <returns>是否有效</returns>
    private Boolean TryReadMeta(Int32 slotOffset, out HashMeta meta)
    {
        meta = default;

        var buffer = _region.ReadBytes(slotOffset, MetaSize);
        if (ReadUInt32(buffer, MetaMagic) != Magic) return false;
        if (buffer[MetaVersion] != FileVersion) return false;
        if (buffer[MetaKeyParts] != _codec.PartCount) return false;
        if (buffer[MetaKeyType] != (Byte)_codec.Types[0]) return false;

        var crc = ReadUInt32(buffer, MetaCrc);
        if (crc != Crc32.Compute(buffer, 0, MetaCrc)) return false;

        meta = new HashMeta
        {
            Sequence = ReadInt64(buffer, MetaSequence),
            Capacity = ReadInt64(buffer, MetaCapacity),
            BucketOffset = ReadInt64(buffer, MetaBucketOffset),
            ArenaEnd = ReadInt64(buffer, MetaArenaEnd),
            KeyCount = ReadInt64(buffer, MetaKeyCount),
            ValueCount = ReadInt64(buffer, MetaValueCount),
            DeadBytes = ReadInt64(buffer, MetaDeadBytes),
            AppliedOffset = ReadInt64(buffer, MetaAppliedOffset),
            LiveCount = (buffer[MetaFlags] & MetaFlagLiveCount) != 0 ? ReadInt64(buffer, MetaLiveCount) : 0,
        };

        return true;
    }

    /// <summary>保存元数据到槽（双槽交替，避免单槽损坏即全量重建）</summary>
    private void SaveMeta()
    {
        _meta.Sequence++;

        var slot = (_meta.Sequence & 1) == 0 ? MetaSlot0 : MetaSlot1;
        var buffer = new Byte[MetaSize];

        WriteUInt32(buffer, MetaMagic, Magic);
        buffer[MetaVersion] = FileVersion;
        buffer[MetaFlags] = (Byte)((_codec.HasSortPrefix ? 1 : 0) | MetaFlagLiveCount);
        buffer[MetaKeyParts] = (Byte)_codec.PartCount;
        buffer[MetaKeyType] = (Byte)_codec.Types[0];
        WriteInt64(buffer, MetaSequence, _meta.Sequence);
        WriteInt64(buffer, MetaCapacity, _meta.Capacity);
        WriteInt64(buffer, MetaBucketOffset, _meta.BucketOffset);
        WriteInt64(buffer, MetaArenaEnd, _meta.ArenaEnd);
        WriteInt64(buffer, MetaKeyCount, _meta.KeyCount);
        WriteInt64(buffer, MetaValueCount, _meta.ValueCount);
        WriteInt64(buffer, MetaDeadBytes, _meta.DeadBytes);
        WriteInt64(buffer, MetaAppliedOffset, _persistedOffset);
        WriteInt64(buffer, MetaLiveCount, _meta.LiveCount);
        WriteUInt32(buffer, MetaCrc, Crc32.Compute(buffer, 0, MetaCrc));

        _region.Write(slot, buffer, 0, buffer.Length);
    }

    #endregion

    #region 核心算法
    /// <summary>应用"加入值"</summary>
    private void ApplyAdd(in MmfHashChange change)
    {
        var key = change.Key;
        var payload = change.Payload!;
        var hash = ComputeHash(key);

        var node = FindKey(key, hash);
        if (node == 0)
            node = CreateKeyNode(key, hash);

        if (change.Unique && ContainsPayload(node, payload))
            return;

        var value = CreateValueNode(payload);
        LinkValue(node, value);
    }

    /// <summary>应用"移除值"</summary>
    private void ApplyRemoveValue(in MmfHashChange change)
    {
        var node = FindKey(change.Key, ComputeHash(change.Key));
        if (node == 0) return;

        var payload = change.Payload!;
        var previous = 0L;
        var current = _region.ReadInt64(node + KeyValueHead);

        while (current != 0)
        {
            var length = _region.ReadInt32(current + ValueLength);
            if (length == payload.Length && PayloadEquals(current, payload))
            {
                var next = _region.ReadInt64(current + ValueNextValue);
                if (previous == 0)
                    _region.WriteInt64(node + KeyValueHead, next);
                else
                    _region.WriteInt64(previous + ValueNextValue, next);

                if (_region.ReadInt64(node + KeyValueTail) == current)
                    _region.WriteInt64(node + KeyValueTail, previous);

                var count = _region.ReadInt32(node + KeyValueCount) - 1;
                _region.WriteInt32(node + KeyValueCount, count);
                _meta.ValueCount--;
                if ((_region.ReadInt32(current + ValueFlags) & ValueFlagDeleted) == 0) _meta.LiveCount--;
                _meta.DeadBytes += Align(ValueHeaderSize + length);

                if (count <= 0) UnlinkKeyNode(node, change.Key, hash: ComputeHash(change.Key));

                return;
            }

            previous = current;
            current = _region.ReadInt64(current + ValueNextValue);
        }
    }

    /// <summary>应用"移除键"</summary>
    private void ApplyRemoveKey(in MmfHashChange change)
    {
        var hash = ComputeHash(change.Key);
        var node = FindKey(change.Key, hash);
        if (node == 0) return;

        UnlinkKeyNode(node, change.Key, hash);
    }

    /// <summary>把键节点从桶链摘除并统计死区</summary>
    private void UnlinkKeyNode(Int64 node, Byte[] key, Int64 hash)
    {
        var bucket = (Int64)((UInt64)hash & (UInt64)(_meta.Capacity - 1));
        var slot = _meta.BucketOffset + bucket * sizeof(Int64);
        var current = _region.ReadInt64(slot);
        var previous = 0L;

        while (current != 0)
        {
            if (current == node)
            {
                var next = _region.ReadInt64(current + KeyNextKey);
                if (previous == 0)
                    _region.WriteInt64(slot, next);
                else
                    _region.WriteInt64(previous + KeyNextKey, next);

                break;
            }

            previous = current;
            current = _region.ReadInt64(current + KeyNextKey);
        }

        var valueCount = _region.ReadInt32(node + KeyValueCount);
        _meta.KeyCount--;
        _meta.ValueCount -= valueCount;
        _meta.DeadBytes += Align(KeyHeaderSize + _region.ReadInt32(node + KeyLength));

        var value = _region.ReadInt64(node + KeyValueHead);
        while (value != 0)
        {
            if ((_region.ReadInt32(value + ValueFlags) & ValueFlagDeleted) == 0) _meta.LiveCount--;

            _meta.DeadBytes += Align(ValueHeaderSize + _region.ReadInt32(value + ValueLength));
            value = _region.ReadInt64(value + ValueNextValue);
        }
    }

    /// <summary>把值标记为已删除（幂等），并推进元数据中的存活计数</summary>
    /// <param name="valueOffset">值节点偏移</param>
    /// <param name="deletedTx">删除事务 ID</param>
    /// <param name="deletedTxOffset">删除事务 ID 在负载内的偏移</param>
    public void MarkValueDeleted(Int64 valueOffset, UInt64 deletedTx, Int32 deletedTxOffset)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfHashIndex));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", FilePath);
        if (valueOffset <= 0) throw new ArgumentOutOfRangeException(nameof(valueOffset));

        var flags = _region.ReadInt32(valueOffset + ValueFlags);
        if ((flags & ValueFlagDeleted) != 0) return;

        var length = _region.ReadInt32(valueOffset + ValueLength);
        if (deletedTxOffset < 0 || deletedTxOffset + 8 > length) throw new ArgumentOutOfRangeException(nameof(deletedTxOffset));

        var buffer = new Byte[8];
        WriteInt64(buffer, 0, (Int64)deletedTx);
        _region.Write(valueOffset + ValueHeaderSize + deletedTxOffset, buffer, 0, buffer.Length);
        _region.WriteInt32(valueOffset + ValueFlags, flags | ValueFlagDeleted);

        _meta.LiveCount--;
    }

    /// <summary>查找键节点</summary>
    private Int64 FindKey(Byte[] key, Int64 hash)
    {
        var bucket = (Int64)((UInt64)hash & (UInt64)(_meta.Capacity - 1));
        var node = _region.ReadInt64(_meta.BucketOffset + bucket * sizeof(Int64));

        while (node != 0)
        {
            if (_region.ReadInt64(node + KeyHash) == hash && KeyEquals(node, key))
                return node;

            node = _region.ReadInt64(node + KeyNextKey);
        }

        return 0;
    }

    /// <summary>比较节点键与目标键</summary>
    private Boolean KeyEquals(Int64 node, Byte[] key)
    {
        var length = _region.ReadInt32(node + KeyLength);
        if (length != key.Length) return false;

        var buffer = _keyBuffer ??= new Byte[256];
        if (length <= buffer.Length)
        {
            _region.Read(node + KeyHeaderSize, buffer, 0, length);

            return BytesEqual(buffer, 0, key, 0, length);
        }

        // 超长键分块比较，避免为每次查找分配大缓冲
        var offset = 0;
        while (offset < length)
        {
            var count = Math.Min(buffer.Length, length - offset);
            _region.Read(node + KeyHeaderSize + offset, buffer, 0, count);
            if (!BytesEqual(buffer, 0, key, offset, count)) return false;

            offset += count;
        }

        return true;
    }

    /// <summary>判断键节点下是否已有相同负载</summary>
    private Boolean ContainsPayload(Int64 node, Byte[] payload)
    {
        var value = _region.ReadInt64(node + KeyValueHead);
        while (value != 0)
        {
            if (PayloadEquals(value, payload)) return true;

            value = _region.ReadInt64(value + ValueNextValue);
        }

        return false;
    }

    /// <summary>比较值节点负载</summary>
    private Boolean PayloadEquals(Int64 valueOffset, Byte[] payload)
    {
        var length = _region.ReadInt32(valueOffset + ValueLength);
        if (length != payload.Length) return false;

        var buffer = _payloadBuffer ??= new Byte[256];
        if (length <= buffer.Length)
        {
            _region.Read(valueOffset + ValueHeaderSize, buffer, 0, length);

            return BytesEqual(buffer, 0, payload, 0, length);
        }

        var offset = 0;
        while (offset < length)
        {
            var count = Math.Min(buffer.Length, length - offset);
            _region.Read(valueOffset + ValueHeaderSize + offset, buffer, 0, count);
            if (!BytesEqual(buffer, 0, payload, offset, count)) return false;

            offset += count;
        }

        return true;
    }

    /// <summary>创建键节点</summary>
    private Int64 CreateKeyNode(Byte[] key, Int64 hash)
    {
        var size = Align(KeyHeaderSize + key.Length);
        var node = _writePosition;
        _writePosition += size;

        _region.EnsureCapacity(_writePosition);

        var bucket = (Int64)((UInt64)hash & (UInt64)(_meta.Capacity - 1));
        var slot = _meta.BucketOffset + bucket * sizeof(Int64);
        var head = _region.ReadInt64(slot);

        // 键节点一次写出：逐字段写映射视图在批量装载时是主要开销
        EnsureNodeBuffer((Int32)size);
        var buffer = _nodeBuffer;
        WriteInt64(buffer, KeyNextKey, head);
        WriteInt64(buffer, KeyHash, hash);
        WriteInt32(buffer, KeyLength, key.Length);
        WriteInt32(buffer, KeyValueCount, 0);
        WriteInt64(buffer, KeyValueHead, 0);
        WriteInt64(buffer, KeyValueTail, 0);
        Array.Copy(key, 0, buffer, KeyHeaderSize, key.Length);
        ClearPadding(buffer, KeyHeaderSize + key.Length, (Int32)size);
        _region.Write(node, buffer, 0, (Int32)size);

        _region.WriteInt64(slot, node);

        _meta.KeyCount++;

        return node;
    }

    /// <summary>创建值节点</summary>
    private Int64 CreateValueNode(Byte[] payload)
    {
        var size = Align(ValueHeaderSize + payload.Length);
        var node = _writePosition;
        _writePosition += size;

        _region.EnsureCapacity(_writePosition);

        EnsureNodeBuffer((Int32)size);
        var buffer = _nodeBuffer;
        WriteInt64(buffer, ValueNextValue, 0);
        WriteInt32(buffer, ValueLength, payload.Length);
        WriteInt32(buffer, ValueFlags, 0);
        Array.Copy(payload, 0, buffer, ValueHeaderSize, payload.Length);
        ClearPadding(buffer, ValueHeaderSize + payload.Length, (Int32)size);
        _region.Write(node, buffer, 0, (Int32)size);

        return node;
    }

    /// <summary>确保节点写缓冲足够大</summary>
    private void EnsureNodeBuffer(Int32 length)
    {
        if (_nodeBuffer.Length >= length) return;

        var capacity = _nodeBuffer.Length;
        while (capacity < length) capacity *= 2;
        _nodeBuffer = new Byte[capacity];
    }

    /// <summary>清零对齐区残留字节</summary>
    private static void ClearPadding(Byte[] buffer, Int32 used, Int32 size)
    {
        if (size > used) Array.Clear(buffer, used, size - used);
    }

    /// <summary>把值节点挂到键节点链尾</summary>
    private void LinkValue(Int64 keyNode, Int64 valueNode)
    {
        var tail = _region.ReadInt64(keyNode + KeyValueTail);
        if (tail == 0)
            _region.WriteInt64(keyNode + KeyValueHead, valueNode);
        else
            _region.WriteInt64(tail + ValueNextValue, valueNode);

        _region.WriteInt64(keyNode + KeyValueTail, valueNode);
        _region.WriteInt32(keyNode + KeyValueCount, _region.ReadInt32(keyNode + KeyValueCount) + 1);
        _meta.ValueCount++;
        _meta.LiveCount++;
    }

    /// <summary>扩容：追加新桶数组并把键节点复制过去</summary>
    /// <param name="newCapacity">新桶数量</param>
    /// <remarks>
    /// 只复制键节点（值链共享），旧键节点不修改，保证扩容中途崩溃时旧结构仍然可用。
    /// </remarks>
    private void Rehash(Int64 newCapacity)
    {
        var bucketBytes = newCapacity * sizeof(Int64);
        var bucketOffset = Align(_writePosition);
        _writePosition = bucketOffset + bucketBytes;
        _meta.ArenaEnd = _writePosition;

        _region.EnsureCapacity(_writePosition);
        ZeroRegion(bucketOffset, bucketBytes);

        var oldCapacity = _meta.Capacity;
        var oldBucketOffset = _meta.BucketOffset;

        // 原地重链：只改每个键节点的 NextKey 指针，不复制节点，避免 O(N) 拷贝与键数组分配
        for (var bucket = 0L; bucket < oldCapacity; bucket++)
        {
            var node = _region.ReadInt64(oldBucketOffset + bucket * sizeof(Int64));
            while (node != 0)
            {
                var next = _region.ReadInt64(node + KeyNextKey);
                var hash = _region.ReadInt64(node + KeyHash);

                var newBucket = (Int64)((UInt64)hash & (UInt64)(newCapacity - 1));
                var newSlot = bucketOffset + newBucket * sizeof(Int64);
                var head = _region.ReadInt64(newSlot);

                _region.WriteInt64(node + KeyNextKey, head);
                _region.WriteInt64(newSlot, node);

                node = next;
            }
        }

        _meta.Capacity = newCapacity;
        _meta.BucketOffset = bucketOffset;
        _meta.ArenaEnd = _writePosition;
        if (oldBucketOffset > 0) _meta.DeadBytes += oldCapacity * sizeof(Int64);
    }

    #endregion

    #region 辅助
    /// <summary>计算键哈希</summary>
    private static Int64 ComputeHash(Byte[] key) => (Int64)IndexKeyCodec.Hash(key, 0, key.Length);

    /// <summary>8 字节对齐</summary>
    private static Int64 Align(Int64 length) => (length + 7) & ~7L;

    /// <summary>清零一段区域</summary>
    private void ZeroRegion(Int64 offset, Int64 length)
    {
        var buffer = new Byte[4096];
        var position = offset;
        var end = offset + length;
        while (position < end)
        {
            var count = (Int32)Math.Min(buffer.Length, end - position);
            _region.Write(position, buffer, 0, count);
            position += count;
        }
    }

    /// <summary>比较两个字节区间</summary>
    private static Boolean BytesEqual(Byte[] a, Int32 aOffset, Byte[] b, Int32 bOffset, Int32 count)
    {
        for (var i = 0; i < count; i++)
        {
            if (a[aOffset + i] != b[bOffset + i]) return false;
        }

        return true;
    }

    /// <summary>小端读取 Int64</summary>
    private static Int64 ReadInt64(Byte[] buffer, Int32 offset) =>
        buffer[offset] | ((Int64)buffer[offset + 1] << 8) | ((Int64)buffer[offset + 2] << 16) | ((Int64)buffer[offset + 3] << 24) |
        ((Int64)buffer[offset + 4] << 32) | ((Int64)buffer[offset + 5] << 40) | ((Int64)buffer[offset + 6] << 48) | ((Int64)buffer[offset + 7] << 56);

    /// <summary>小端写入 Int64</summary>
    private static void WriteInt64(Byte[] buffer, Int32 offset, Int64 value)
    {
        for (var i = 0; i < 8; i++) buffer[offset + i] = (Byte)(value >> (i * 8));
    }

    /// <summary>小端写入 Int32</summary>
    private static void WriteInt32(Byte[] buffer, Int32 offset, Int32 value)
    {
        for (var i = 0; i < 4; i++) buffer[offset + i] = (Byte)(value >> (i * 8));
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

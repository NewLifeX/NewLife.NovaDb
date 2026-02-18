using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine;

/// <summary>分片管理器，负责自动切分与分片路由</summary>
public class ShardManager
{
    private readonly DbOptions _options;
    private readonly String _tablePath;
    private readonly List<ShardInfo> _shards = [];
    private readonly Object _lock = new();

    /// <summary>分片数量</summary>
    public Int32 ShardCount
    {
        get
        {
            lock (_lock)
            {
                return _shards.Count;
            }
        }
    }

    /// <summary>创建分片管理器</summary>
    /// <param name="options">数据库选项</param>
    /// <param name="tablePath">表目录路径</param>
    public ShardManager(DbOptions options, String tablePath)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tablePath = tablePath ?? throw new ArgumentNullException(nameof(tablePath));
    }

    /// <summary>获取当前写入分片（最后一个非只读分片）</summary>
    /// <returns>写入分片，如果没有可用分片则返回 null</returns>
    public ShardInfo? GetWriteShard()
    {
        lock (_lock)
        {
            for (var i = _shards.Count - 1; i >= 0; i--)
            {
                if (!_shards[i].IsReadOnly)
                    return _shards[i];
            }

            return null;
        }
    }

    /// <summary>获取所有分片</summary>
    /// <returns>分片列表副本</returns>
    public List<ShardInfo> GetAllShards()
    {
        lock (_lock)
        {
            return [.. _shards];
        }
    }

    /// <summary>根据 ID 查找分片</summary>
    /// <param name="shardId">分片 ID</param>
    /// <returns>分片信息，如果未找到则返回 null</returns>
    public ShardInfo? GetShardById(Int32 shardId)
    {
        lock (_lock)
        {
            foreach (var shard in _shards)
            {
                if (shard.ShardId == shardId)
                    return shard;
            }

            return null;
        }
    }

    /// <summary>添加分片</summary>
    /// <param name="shard">分片信息</param>
    public void AddShard(ShardInfo shard)
    {
        if (shard == null) throw new ArgumentNullException(nameof(shard));

        lock (_lock)
        {
            // 检查 ID 是否重复
            foreach (var existing in _shards)
            {
                if (existing.ShardId == shard.ShardId)
                    throw new NovaDbException(ErrorCode.InvalidArgument, $"Shard with ID {shard.ShardId} already exists");
            }

            _shards.Add(shard);
        }
    }

    /// <summary>记录写入操作，更新分片统计</summary>
    /// <param name="shardId">分片 ID</param>
    /// <param name="bytesWritten">写入字节数</param>
    public void RecordWrite(Int32 shardId, Int64 bytesWritten)
    {
        lock (_lock)
        {
            var shard = FindShardByIdLocked(shardId);
            if (shard == null)
                throw new NovaDbException(ErrorCode.ShardNotFound, $"Shard {shardId} not found");

            shard.RowCount++;
            shard.SizeBytes += bytesWritten;
        }
    }

    /// <summary>检查分片是否需要切分</summary>
    /// <param name="shardId">分片 ID</param>
    /// <returns>是否需要切分</returns>
    public Boolean ShouldSplit(Int32 shardId)
    {
        lock (_lock)
        {
            var shard = FindShardByIdLocked(shardId);
            if (shard == null)
                throw new NovaDbException(ErrorCode.ShardNotFound, $"Shard {shardId} not found");

            if (shard.IsReadOnly)
                return false;

            // 优先按大小判断
            if (shard.SizeBytes >= _options.ShardSizeThreshold)
                return true;

            // 再按行数判断
            if (shard.RowCount >= _options.ShardRowThreshold)
                return true;

            return false;
        }
    }

    /// <summary>切分分片，将当前分片标记为只读并创建新分片</summary>
    /// <param name="shardId">待切分的分片 ID</param>
    /// <returns>新创建的分片</returns>
    public ShardInfo Split(Int32 shardId)
    {
        lock (_lock)
        {
            var shard = FindShardByIdLocked(shardId);
            if (shard == null)
                throw new NovaDbException(ErrorCode.ShardNotFound, $"Shard {shardId} not found");

            // 标记当前分片为只读
            shard.IsReadOnly = true;

            // 计算新分片 ID
            var newShardId = 0;
            foreach (var s in _shards)
            {
                if (s.ShardId >= newShardId)
                    newShardId = s.ShardId + 1;
            }

            // 创建新分片
            var newShard = new ShardInfo
            {
                ShardId = newShardId,
                DataFilePath = Path.Combine(_tablePath, $"{newShardId}.data"),
                CreatedAt = DateTime.UtcNow
            };

            _shards.Add(newShard);
            return newShard;
        }
    }

    /// <summary>根据键查找可能包含该键的分片列表</summary>
    /// <param name="key">查询键</param>
    /// <returns>可能包含该键的分片列表</returns>
    public List<ShardInfo> GetShardsForKey(Object key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            var result = new List<ShardInfo>();
            var comparableKey = new ComparableObject(key);

            foreach (var shard in _shards)
            {
                // 无范围信息的分片（新分片或未设置范围）需要被搜索
                if (shard.MinKey == null || shard.MaxKey == null)
                {
                    result.Add(shard);
                    continue;
                }

                var minKey = new ComparableObject(shard.MinKey);
                var maxKey = new ComparableObject(shard.MaxKey);

                // 键在 [MinKey, MaxKey] 范围内
                if (comparableKey.CompareTo(minKey) >= 0 && comparableKey.CompareTo(maxKey) <= 0)
                    result.Add(shard);
            }

            return result;
        }
    }

    #region 辅助

    /// <summary>在锁内按 ID 查找分片</summary>
    private ShardInfo? FindShardByIdLocked(Int32 shardId)
    {
        foreach (var shard in _shards)
        {
            if (shard.ShardId == shardId)
                return shard;
        }

        return null;
    }

    #endregion
}

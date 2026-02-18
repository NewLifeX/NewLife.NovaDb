using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine;

/// <summary>
/// 热段配置
/// </summary>
public class HotSegmentConfig
{
    /// <summary>
    /// 热数据窗口（秒），默认 600 秒（10 分钟）
    /// </summary>
    public Int32 HotWindowSeconds { get; set; } = 600;

    /// <summary>
    /// 冷数据淘汰阈值（秒），默认 1800 秒（30 分钟）
    /// </summary>
    public Int32 ColdEvictionSeconds { get; set; } = 1800;

    /// <summary>
    /// 热段最大行数，默认 100 万行
    /// </summary>
    public Int32 MaxHotRows { get; set; } = 1_000_000;

    /// <summary>
    /// 热度检查间隔（秒），默认 60 秒
    /// </summary>
    public Int32 HeatCheckIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// 索引段元数据
/// </summary>
public class IndexSegment
{
    /// <summary>
    /// 段 ID
    /// </summary>
    public Int32 SegmentId { get; set; }

    /// <summary>
    /// 是否为热段
    /// </summary>
    public Boolean IsHot { get; set; }

    /// <summary>
    /// 最小键
    /// </summary>
    public Object? MinKey { get; set; }

    /// <summary>
    /// 最大键
    /// </summary>
    public Object? MaxKey { get; set; }

    /// <summary>
    /// 行数
    /// </summary>
    public Int32 RowCount { get; set; }

    /// <summary>
    /// 最后访问时间
    /// </summary>
    public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 数据文件路径
    /// </summary>
    public String? DataFilePath { get; set; }
}

/// <summary>
/// 热索引管理器
/// </summary>
public class HotIndexManager
{
    private readonly HotSegmentConfig _config;
    private readonly SkipList<ComparableObject, IndexSegment> _hotSegments;
    private readonly Object _lock = new();
    private DateTime _lastHeatCheck = DateTime.UtcNow;

    /// <summary>
    /// 配置
    /// </summary>
    public HotSegmentConfig Config => _config;

    /// <summary>
    /// 热段数量
    /// </summary>
    public Int32 HotSegmentCount
    {
        get
        {
            lock (_lock)
            {
                return _hotSegments.Count;
            }
        }
    }

    /// <summary>
    /// 创建热索引管理器
    /// </summary>
    /// <param name="config">配置</param>
    public HotIndexManager(HotSegmentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _hotSegments = new SkipList<ComparableObject, IndexSegment>();
    }

    /// <summary>
    /// 访问键（更新热度）
    /// </summary>
    /// <param name="key">键</param>
    public void AccessKey(Object key)
    {
        if (key == null)
            return;

        lock (_lock)
        {
            var comparableKey = new ComparableObject(key);
            if (_hotSegments.TryGetValue(comparableKey, out var segment))
            {
                segment!.LastAccessTime = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// 添加热段
    /// </summary>
    /// <param name="segment">段信息</param>
    public void AddHotSegment(IndexSegment segment)
    {
        if (segment == null)
            throw new ArgumentNullException(nameof(segment));
        if (segment.MinKey == null)
            throw new NovaException(ErrorCode.InvalidArgument, "Segment MinKey cannot be null");

        lock (_lock)
        {
            var comparableKey = new ComparableObject(segment.MinKey);
            segment.IsHot = true;
            segment.LastAccessTime = DateTime.UtcNow;
            _hotSegments.Insert(comparableKey, segment);
        }
    }

    /// <summary>
    /// 移除热段
    /// </summary>
    /// <param name="minKey">段最小键</param>
    /// <returns>是否移除成功</returns>
    public Boolean RemoveHotSegment(Object minKey)
    {
        if (minKey == null)
            return false;

        lock (_lock)
        {
            var comparableKey = new ComparableObject(minKey);
            return _hotSegments.Remove(comparableKey);
        }
    }

    /// <summary>
    /// 检查并淘汰冷段
    /// </summary>
    /// <returns>被淘汰的冷段列表</returns>
    public List<IndexSegment> EvictColdSegments()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var coldThreshold = TimeSpan.FromSeconds(_config.ColdEvictionSeconds);
            var evictedSegments = new List<IndexSegment>();

            var allSegments = _hotSegments.GetAll();
            foreach (var entry in allSegments)
            {
                var segment = entry.Value;
                var timeSinceAccess = now - segment.LastAccessTime;

                if (timeSinceAccess > coldThreshold)
                {
                    segment.IsHot = false;
                    evictedSegments.Add(segment);
                    _hotSegments.Remove(entry.Key);
                }
            }

            _lastHeatCheck = now;
            return evictedSegments;
        }
    }

    /// <summary>
    /// 是否需要进行热度检查
    /// </summary>
    /// <returns>是否需要检查</returns>
    public Boolean ShouldCheckHeat()
    {
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(_config.HeatCheckIntervalSeconds);
        return (now - _lastHeatCheck) > interval;
    }

    /// <summary>
    /// 查找键所属的段
    /// </summary>
    /// <param name="key">键</param>
    /// <returns>段信息，如果未找到则返回 null</returns>
    public IndexSegment? FindSegment(Object key)
    {
        if (key == null)
            return null;

        lock (_lock)
        {
            var comparableKey = new ComparableObject(key);
            if (_hotSegments.TryGetValue(comparableKey, out var segment))
            {
                segment!.LastAccessTime = DateTime.UtcNow;
                return segment;
            }

            return null;
        }
    }

    /// <summary>
    /// 获取所有热段
    /// </summary>
    /// <returns>热段列表</returns>
    public List<IndexSegment> GetAllHotSegments()
    {
        lock (_lock)
        {
            return _hotSegments.GetAll().Select(x => x.Value).ToList();
        }
    }

    /// <summary>
    /// 清空所有热段
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _hotSegments.Clear();
            _lastHeatCheck = DateTime.UtcNow;
        }
    }
}

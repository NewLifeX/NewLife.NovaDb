namespace NewLife.NovaDb.Storage;

/// <summary>页缓存（LRU 策略）</summary>
/// <remarks>
/// 基于 LinkedList + Dictionary 实现 O(1) 的 LRU 缓存：
/// - TryGet: 命中时将页移至链表头部（最近访问）
/// - Put: 容量满时淘汰链表尾部（最久未访问）
/// - 线程安全：所有操作均在锁保护下执行
/// </remarks>
public class PageCache
{
    private readonly Int32 _capacity;
    private readonly Dictionary<UInt64, CacheEntry> _cache;
    private readonly LinkedList<UInt64> _lruList;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>缓存容量（最大页数）</summary>
    public Int32 Capacity => _capacity;

    /// <summary>当前缓存页数</summary>
    public Int32 Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>命中次数</summary>
    public Int64 HitCount { get; private set; }

    /// <summary>未命中次数</summary>
    public Int64 MissCount { get; private set; }

    /// <summary>实例化页缓存</summary>
    /// <param name="capacity">最大缓存页数（必须为正整数）</param>
    /// <exception cref="ArgumentOutOfRangeException">capacity 不为正整数时抛出</exception>
    public PageCache(Int32 capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _capacity = capacity;
        _cache = new Dictionary<UInt64, CacheEntry>(capacity);
        _lruList = new LinkedList<UInt64>();
    }

    /// <summary>尝试获取缓存的页</summary>
    /// <param name="pageId">页 ID</param>
    /// <param name="data">命中时返回页数据，未命中返回 null</param>
    /// <returns>是否命中缓存</returns>
    public Boolean TryGet(UInt64 pageId, out Byte[]? data)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageId, out var entry))
            {
                // 移动到 LRU 链表头部（最近访问）
                _lruList.Remove(entry.LruNode);
                entry.LruNode = _lruList.AddFirst(pageId);

                data = entry.Data;
                HitCount++;
                return true;
            }

            data = null;
            MissCount++;
            return false;
        }
    }

    /// <summary>添加或更新页到缓存</summary>
    /// <param name="pageId">页 ID</param>
    /// <param name="data">页数据</param>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出</exception>
    public void Put(UInt64 pageId, Byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        lock (_lock)
        {
            // 已存在则更新数据并移到头部
            if (_cache.TryGetValue(pageId, out var entry))
            {
                entry.Data = data;
                _lruList.Remove(entry.LruNode);
                entry.LruNode = _lruList.AddFirst(pageId);
                return;
            }

            // 容量满时淘汰最久未使用的页
            if (_cache.Count >= _capacity)
            {
                var lruPageId = _lruList.Last!.Value;
                _lruList.RemoveLast();
                _cache.Remove(lruPageId);
            }

            // 添加新页到头部
            var node = _lruList.AddFirst(pageId);
            _cache[pageId] = new CacheEntry
            {
                Data = data,
                LruNode = node
            };
        }
    }

    /// <summary>从缓存中移除指定页</summary>
    /// <param name="pageId">页 ID</param>
    /// <returns>是否成功移除（页不存在时返回 false）</returns>
    public Boolean Remove(UInt64 pageId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageId, out var entry))
            {
                _lruList.Remove(entry.LruNode);
                _cache.Remove(pageId);
                return true;
            }
            return false;
        }
    }

    /// <summary>清空全部缓存</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
            HitCount = 0;
            MissCount = 0;
        }
    }

    private class CacheEntry
    {
        public Byte[] Data { get; set; } = [];

        public LinkedListNode<UInt64> LruNode { get; set; } = null!;
    }
}

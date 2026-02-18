namespace NewLife.NovaDb.Storage;

/// <summary>
/// 页缓存（LRU 策略）
/// </summary>
public class PageCache
{
    private readonly Int32 _capacity;
    private readonly Dictionary<UInt64, CacheEntry> _cache;
    private readonly LinkedList<UInt64> _lruList;
    private readonly Object _lock = new();

    /// <summary>
    /// 缓存大小（页数）
    /// </summary>
    public Int32 Capacity => _capacity;

    /// <summary>
    /// 当前缓存页数
    /// </summary>
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

    public PageCache(Int32 capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _capacity = capacity;
        _cache = new Dictionary<UInt64, CacheEntry>(capacity);
        _lruList = new LinkedList<UInt64>();
    }

    /// <summary>
    /// 尝试获取缓存的页
    /// </summary>
    public Boolean TryGet(UInt64 pageId, out Byte[]? data)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageId, out var entry))
            {
                // 移动到 LRU 列表头部
                _lruList.Remove(entry.LruNode);
                entry.LruNode = _lruList.AddFirst(pageId);

                data = entry.Data;
                return true;
            }

            data = null;
            return false;
        }
    }

    /// <summary>
    /// 添加页到缓存
    /// </summary>
    public void Put(UInt64 pageId, Byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        lock (_lock)
        {
            // 如果已存在，更新数据并移到头部
            if (_cache.TryGetValue(pageId, out var entry))
            {
                entry.Data = data;
                _lruList.Remove(entry.LruNode);
                entry.LruNode = _lruList.AddFirst(pageId);
                return;
            }

            // 如果达到容量，淘汰最久未使用的页
            if (_cache.Count >= _capacity)
            {
                var lruPageId = _lruList.Last!.Value;
                _lruList.RemoveLast();
                _cache.Remove(lruPageId);
            }

            // 添加新页
            var node = _lruList.AddFirst(pageId);
            _cache[pageId] = new CacheEntry
            {
                Data = data,
                LruNode = node
            };
        }
    }

    /// <summary>
    /// 移除页从缓存
    /// </summary>
    public void Remove(UInt64 pageId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageId, out var entry))
            {
                _lruList.Remove(entry.LruNode);
                _cache.Remove(pageId);
            }
        }
    }

    /// <summary>
    /// 清空缓存
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }

    private class CacheEntry
    {
        public Byte[] Data { get; set; } = Array.Empty<Byte>();
        public LinkedListNode<UInt64> LruNode { get; set; } = null!;
    }
}

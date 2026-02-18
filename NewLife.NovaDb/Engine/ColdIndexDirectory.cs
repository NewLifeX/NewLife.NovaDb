using System.Linq;

namespace NewLife.NovaDb.Engine;

/// <summary>
/// 冷段目录项（稀疏索引）
/// </summary>
public class ColdDirectoryEntry
{
    /// <summary>
    /// 锚点键
    /// </summary>
    public Object? Key { get; set; }

    /// <summary>
    /// 页 ID
    /// </summary>
    public UInt64 PageId { get; set; }

    /// <summary>
    /// 页内偏移
    /// </summary>
    public Int32 Offset { get; set; }
}

/// <summary>
/// 冷索引目录（稀疏索引，每 N 行一个锚点）
/// </summary>
public class ColdIndexDirectory
{
    private readonly Int32 _anchorInterval;
    private readonly List<ColdDirectoryEntry> _anchors = new();
    private readonly Object _lock = new();

    /// <summary>
    /// 锚点间隔（行数），默认 1000 行一个锚点
    /// </summary>
    public Int32 AnchorInterval => _anchorInterval;

    /// <summary>
    /// 锚点数量
    /// </summary>
    public Int32 AnchorCount
    {
        get
        {
            lock (_lock)
            {
                return _anchors.Count;
            }
        }
    }

    /// <summary>
    /// 创建冷索引目录
    /// </summary>
    /// <param name="anchorInterval">锚点间隔（行数），默认 1000</param>
    public ColdIndexDirectory(Int32 anchorInterval = 1000)
    {
        if (anchorInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchorInterval), "Anchor interval must be positive");

        _anchorInterval = anchorInterval;
    }

    /// <summary>
    /// 添加锚点
    /// </summary>
    /// <param name="key">锚点键</param>
    /// <param name="pageId">页 ID</param>
    /// <param name="offset">页内偏移</param>
    public void AddAnchor(Object key, UInt64 pageId, Int32 offset)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            _anchors.Add(new ColdDirectoryEntry
            {
                Key = key,
                PageId = pageId,
                Offset = offset
            });

            // 保持锚点按键排序（假设按插入顺序已经有序）
            // 如果需要支持无序插入，可以在这里排序
        }
    }

    /// <summary>
    /// 查找键的起始位置（找到小于等于 key 的最大锚点）
    /// </summary>
    /// <param name="key">要查找的键</param>
    /// <returns>目录项，如果未找到则返回 null</returns>
    public ColdDirectoryEntry? FindStartPosition(Object key)
    {
        if (key == null)
            return null;

        lock (_lock)
        {
            if (_anchors.Count == 0)
                return null;

            // 二分查找：找到小于等于 key 的最大锚点
            var left = 0;
            var right = _anchors.Count - 1;
            ColdDirectoryEntry? result = null;

            while (left <= right)
            {
                var mid = left + (right - left) / 2;
                var anchor = _anchors[mid];

                if (anchor.Key == null)
                {
                    left = mid + 1;
                    continue;
                }

                var cmp = CompareKeys(anchor.Key, key);

                if (cmp <= 0)
                {
                    result = anchor;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 获取范围内的锚点
    /// </summary>
    /// <param name="minKey">最小键（包含）</param>
    /// <param name="maxKey">最大键（包含）</param>
    /// <returns>范围内的锚点列表</returns>
    public List<ColdDirectoryEntry> GetRange(Object? minKey, Object? maxKey)
    {
        lock (_lock)
        {
            if (_anchors.Count == 0)
                return new List<ColdDirectoryEntry>();

            var result = new List<ColdDirectoryEntry>();

            foreach (var anchor in _anchors)
            {
                if (anchor.Key == null)
                    continue;

                var inRange = true;

                if (minKey != null && CompareKeys(anchor.Key, minKey) < 0)
                    inRange = false;

                if (maxKey != null && CompareKeys(anchor.Key, maxKey) > 0)
                    inRange = false;

                if (inRange)
                    result.Add(anchor);
            }

            return result;
        }
    }

    /// <summary>
    /// 清空所有锚点
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _anchors.Clear();
        }
    }

    /// <summary>
    /// 获取所有锚点
    /// </summary>
    /// <returns>锚点列表</returns>
    public List<ColdDirectoryEntry> GetAllAnchors()
    {
        lock (_lock)
        {
            return new List<ColdDirectoryEntry>(_anchors);
        }
    }

    #region 辅助

    /// <summary>
    /// 比较两个键
    /// </summary>
    private Int32 CompareKeys(Object key1, Object key2)
    {
        if (key1.Equals(key2))
            return 0;

        if (key1 is IComparable comparable1 && key2 is IComparable)
        {
            return comparable1.CompareTo(key2);
        }

        // 使用 HashCode 比较
        return key1.GetHashCode().CompareTo(key2.GetHashCode());
    }

    #endregion
}

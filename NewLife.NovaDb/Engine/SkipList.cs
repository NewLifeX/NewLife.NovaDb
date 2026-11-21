namespace NewLife.NovaDb.Engine;

/// <summary>跳表节点</summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
internal class SkipListNode<TKey, TValue> where TKey : IComparable<TKey>
{
    public TKey Key { get; set; }
    public TValue Value { get; set; }
    public SkipListNode<TKey, TValue>?[] Forward { get; set; }

    /// <summary>第 0 层前驱指针，供反向遍历使用。头节点为 null</summary>
    public SkipListNode<TKey, TValue>? Back { get; set; }

    public SkipListNode(TKey key, TValue value, Int32 level)
    {
        Key = key;
        Value = value;
        Forward = new SkipListNode<TKey, TValue>?[level + 1];
    }
}

/// <summary>跳表实现（用于主键索引）</summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
public class SkipList<TKey, TValue> where TKey : IComparable<TKey>
{
    private const Int32 MaxLevel = 16;
    private const Double P = 0.5;

    private readonly SkipListNode<TKey, TValue> _head;
    private Int32 _level;
    private Int32 _count;
    private readonly Random _random = new();
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>元素数量</summary>
    public Int32 Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>创建跳表</summary>
    public SkipList()
    {
        _head = new SkipListNode<TKey, TValue>(default!, default!, MaxLevel);
        _level = 0;
        _count = 0;
    }

    /// <summary>生成随机层级</summary>
    private Int32 RandomLevel()
    {
        var level = 0;
        while (_random.NextDouble() < P && level < MaxLevel)
        {
            level++;
        }
        return level;
    }

    /// <summary>插入或更新键值对</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    public void Insert(TKey key, TValue value)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            var update = new SkipListNode<TKey, TValue>?[MaxLevel + 1];
            var current = _head;

            // 从最高层开始向下查找插入位置
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null && current.Forward[i]!.Key.CompareTo(key) < 0)
                {
                    current = current.Forward[i]!;
                }
                update[i] = current;
            }

            current = current.Forward[0]!;

            // 如果键已存在，更新值
            if (current != null && current.Key.CompareTo(key) == 0)
            {
                current.Value = value;
                return;
            }

            // 插入新节点
            var newLevel = RandomLevel();
            if (newLevel > _level)
            {
                for (var i = _level + 1; i <= newLevel; i++)
                {
                    update[i] = _head;
                }
                _level = newLevel;
            }

            var newNode = new SkipListNode<TKey, TValue>(key, value, newLevel);
            for (var i = 0; i <= newLevel; i++)
            {
                newNode.Forward[i] = update[i]!.Forward[i];
                update[i]!.Forward[i] = newNode;
            }

            // 维护第 0 层前驱指针，供反向遍历使用
            newNode.Back = update[0];
            if (newNode.Forward[0] != null) newNode.Forward[0]!.Back = newNode;

            _count++;
        }
    }

    /// <summary>查找键对应的值</summary>
    /// <param name="key">键</param>
    /// <param name="value">输出值</param>
    /// <returns>是否找到</returns>
    public Boolean TryGetValue(TKey key, out TValue? value)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            var current = _head;

            // 从最高层开始向下查找
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null && current.Forward[i]!.Key.CompareTo(key) < 0)
                {
                    current = current.Forward[i]!;
                }
            }

            current = current.Forward[0]!;

            if (current != null && current.Key.CompareTo(key) == 0)
            {
                value = current.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>删除键</summary>
    /// <param name="key">键</param>
    /// <returns>是否删除成功</returns>
    public Boolean Remove(TKey key)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));

        lock (_lock)
        {
            var update = new SkipListNode<TKey, TValue>?[MaxLevel + 1];
            var current = _head;

            // 查找要删除的节点
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null && current.Forward[i]!.Key.CompareTo(key) < 0)
                {
                    current = current.Forward[i]!;
                }
                update[i] = current;
            }

            current = current.Forward[0]!;

            if (current == null || current.Key.CompareTo(key) != 0)
                return false;

            // 先修复第 0 层前驱指针，再从各层摘除节点
            if (current.Forward[0] != null) current.Forward[0]!.Back = current.Back;

            // 删除节点
            for (var i = 0; i <= _level; i++)
            {
                if (update[i]!.Forward[i] != current)
                    break;

                update[i]!.Forward[i] = current.Forward[i];
            }

            // 更新层级
            while (_level > 0 && _head.Forward[_level] == null)
            {
                _level--;
            }

            _count--;
            return true;
        }
    }

    /// <summary>清空跳表</summary>
    public void Clear()
    {
        lock (_lock)
        {
            for (var i = 0; i <= _level; i++)
            {
                _head.Forward[i] = null;
            }
            _level = 0;
            _count = 0;
        }
    }

    /// <summary>检查键是否存在</summary>
    /// <param name="key">键</param>
    /// <returns>是否存在</returns>
    public Boolean ContainsKey(TKey key)
    {
        return TryGetValue(key, out _);
    }

    /// <summary>按键顺序获取指定范围内的键值对</summary>
    /// <param name="from">区间下界键，null 表示不限</param>
    /// <param name="includeFrom">是否包含下界键</param>
    /// <param name="to">区间上界键，null 表示不限</param>
    /// <param name="includeTo">是否包含上界键</param>
    /// <param name="maxCount">最大返回数量，0 表示不限制</param>
    /// <param name="descending">是否按降序返回；区间上下界语义不变，只是遍历方向相反</param>
    /// <returns>按键升序或降序排列的键值对列表</returns>
    /// <remarks>降序从头节点定位区间上界，再沿第 0 层前驱指针回退，无需遍历整个区间</remarks>
    public List<KeyValuePair<TKey, TValue>> GetRange(TKey? from, Boolean includeFrom, TKey? to, Boolean includeTo, Int32 maxCount = 0, Boolean descending = false)
    {
        lock (_lock)
        {
            return descending
                ? GetRangeDescending(from, includeFrom, to, includeTo, maxCount)
                : GetRangeAscending(from, includeFrom, to, includeTo, maxCount);
        }
    }

    /// <summary>正向遍历指定区间</summary>
    private List<KeyValuePair<TKey, TValue>> GetRangeAscending(TKey? from, Boolean includeFrom, TKey? to, Boolean includeTo, Int32 maxCount)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        var current = _head;

        if (from != null)
        {
            // 定位第一个不小于 from 的节点
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null && current.Forward[i]!.Key.CompareTo(from) < 0)
                {
                    current = current.Forward[i]!;
                }
            }

            current = current.Forward[0]!;
            if (!includeFrom && current != null && current.Key.CompareTo(from) == 0)
                current = current.Forward[0]!;
        }
        else
        {
            current = _head.Forward[0]!;
        }

        while (current != null)
        {
            if (to != null)
            {
                var cmp = current.Key.CompareTo(to);
                if (cmp > 0 || (cmp == 0 && !includeTo)) break;
            }

            result.Add(new KeyValuePair<TKey, TValue>(current.Key, current.Value));
            if (maxCount > 0 && result.Count >= maxCount) break;

            current = current.Forward[0]!;
        }

        return result;
    }

    /// <summary>反向遍历指定区间</summary>
    private List<KeyValuePair<TKey, TValue>> GetRangeDescending(TKey? from, Boolean includeFrom, TKey? to, Boolean includeTo, Int32 maxCount)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        var current = _head;

        if (to != null)
        {
            // 定位最后一个不大于 to 的节点
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null && current.Forward[i]!.Key.CompareTo(to) <= 0)
                {
                    current = current.Forward[i]!;
                }
            }

            if (!includeTo && current != _head && current.Key.CompareTo(to) == 0)
                current = current.Back!;
        }
        else
        {
            // 定位最后一个节点
            for (var i = _level; i >= 0; i--)
            {
                while (current.Forward[i] != null)
                {
                    current = current.Forward[i]!;
                }
            }
        }

        while (current != null && current != _head)
        {
            if (from != null)
            {
                var cmp = current.Key.CompareTo(from);
                if (cmp < 0 || (cmp == 0 && !includeFrom)) break;
            }

            result.Add(new KeyValuePair<TKey, TValue>(current.Key, current.Value));
            if (maxCount > 0 && result.Count >= maxCount) break;

            current = current.Back;
        }

        return result;
    }

    /// <summary>获取所有键值对（按键排序）</summary>
    /// <returns>键值对列表</returns>
    public List<KeyValuePair<TKey, TValue>> GetAll()
    {
        lock (_lock)
        {
            var result = new List<KeyValuePair<TKey, TValue>>();
            var current = _head.Forward[0];

            while (current != null)
            {
                result.Add(new KeyValuePair<TKey, TValue>(current.Key, current.Value));
                current = current.Forward[0];
            }

            return result;
        }
    }
}

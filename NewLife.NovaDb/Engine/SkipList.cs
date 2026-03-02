namespace NewLife.NovaDb.Engine;

/// <summary>跳表节点</summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
internal class SkipListNode<TKey, TValue> where TKey : IComparable<TKey>
{
    public TKey Key { get; set; }
    public TValue Value { get; set; }
    public SkipListNode<TKey, TValue>?[] Forward { get; set; }

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

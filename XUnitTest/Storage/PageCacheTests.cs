using System;
using System.Threading.Tasks;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class PageCacheTests
{
    #region 构造函数
    [Fact]
    public void TestConstructorValidCapacity()
    {
        var cache = new PageCache(100);
        Assert.Equal(100, cache.Capacity);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TestConstructorInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageCache(-1));
    }

    [Fact]
    public void TestConstructorMinCapacity()
    {
        var cache = new PageCache(1);
        Assert.Equal(1, cache.Capacity);

        cache.Put(1, [1]);
        cache.Put(2, [2]); // 淘汰 1

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
    }
    #endregion

    #region Put / TryGet
    [Fact]
    public void TestPutAndGet()
    {
        var cache = new PageCache(3);

        var data1 = new Byte[] { 1, 2, 3 };
        var data2 = new Byte[] { 4, 5, 6 };

        cache.Put(1, data1);
        cache.Put(2, data2);

        Assert.True(cache.TryGet(1, out var retrievedData1));
        Assert.Equal(data1, retrievedData1);

        Assert.True(cache.TryGet(2, out var retrievedData2));
        Assert.Equal(data2, retrievedData2);

        Assert.False(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestPutNullData()
    {
        var cache = new PageCache(3);
        Assert.Throws<ArgumentNullException>(() => cache.Put(1, null!));
    }

    [Fact]
    public void TestPutUpdateExisting()
    {
        var cache = new PageCache(3);

        cache.Put(1, [10]);
        cache.Put(1, [20]); // 更新同一个 key

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(1, out var data));
        Assert.Equal(20, data![0]);
    }

    [Fact]
    public void TestGetMissReturnsNull()
    {
        var cache = new PageCache(3);

        Assert.False(cache.TryGet(999, out var data));
        Assert.Null(data);
    }
    #endregion

    #region LRU 淘汰
    [Fact]
    public void TestLruEviction()
    {
        var cache = new PageCache(2);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        // 缓存已满，添加第三个应淘汰第一个（最久未访问）
        cache.Put(3, [3]);

        Assert.False(cache.TryGet(1, out _)); // 已淘汰
        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestLruEvictionAfterAccess()
    {
        var cache = new PageCache(2);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        // 访问 key=1，让它变成最近使用
        cache.TryGet(1, out _);

        // 添加第三个，应淘汰 key=2（最久未访问）
        cache.Put(3, [3]);

        Assert.True(cache.TryGet(1, out _));  // 被访问过，不淘汰
        Assert.False(cache.TryGet(2, out _)); // 被淘汰
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestLruEvictionAfterUpdate()
    {
        var cache = new PageCache(2);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        // 更新 key=1，让它变成最近使用
        cache.Put(1, [10]);

        // 添加第三个，应淘汰 key=2
        cache.Put(3, [3]);

        Assert.True(cache.TryGet(1, out var data));
        Assert.Equal(10, data![0]); // 确认是更新后的值
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestLruEvictionSequence()
    {
        var cache = new PageCache(3);

        cache.Put(1, [1]);
        cache.Put(2, [2]);
        cache.Put(3, [3]);

        // 此时 LRU 序：3(头) → 2 → 1(尾)
        // 再加入 4，应淘汰 1
        cache.Put(4, [4]);
        Assert.False(cache.TryGet(1, out _));

        // 再加入 5，应淘汰 2
        cache.Put(5, [5]);
        Assert.False(cache.TryGet(2, out _));

        Assert.True(cache.TryGet(3, out _));
        Assert.True(cache.TryGet(4, out _));
        Assert.True(cache.TryGet(5, out _));
    }
    #endregion

    #region Remove
    [Fact]
    public void TestRemove()
    {
        var cache = new PageCache(3);

        cache.Put(1, [1, 2, 3]);
        Assert.True(cache.TryGet(1, out _));

        var removed = cache.Remove(1);
        Assert.True(removed);
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void TestRemoveNonExistent()
    {
        var cache = new PageCache(3);

        var removed = cache.Remove(999);
        Assert.False(removed);
    }

    [Fact]
    public void TestRemoveFreesCapacity()
    {
        var cache = new PageCache(2);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        // 移除一个后再添加不应触发淘汰
        cache.Remove(1);
        cache.Put(3, [3]);

        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
        Assert.Equal(2, cache.Count);
    }
    #endregion

    #region Clear
    [Fact]
    public void TestClear()
    {
        var cache = new PageCache(3);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        Assert.Equal(2, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
    }

    [Fact]
    public void TestClearResetsHitMissCounters()
    {
        var cache = new PageCache(3);

        cache.Put(1, [1]);
        cache.TryGet(1, out _); // hit
        cache.TryGet(2, out _); // miss

        Assert.Equal(1, cache.HitCount);
        Assert.Equal(1, cache.MissCount);

        cache.Clear();

        Assert.Equal(0, cache.HitCount);
        Assert.Equal(0, cache.MissCount);
    }

    [Fact]
    public void TestClearThenReuse()
    {
        var cache = new PageCache(2);

        cache.Put(1, [1]);
        cache.Put(2, [2]);
        cache.Clear();

        // 清空后可正常复用
        cache.Put(3, [3]);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(3, out _));
    }
    #endregion

    #region 统计
    [Fact]
    public void TestHitMissCounters()
    {
        var cache = new PageCache(3);

        cache.Put(1, [1]);
        cache.Put(2, [2]);

        cache.TryGet(1, out _); // hit
        cache.TryGet(2, out _); // hit
        cache.TryGet(3, out _); // miss
        cache.TryGet(4, out _); // miss
        cache.TryGet(1, out _); // hit

        Assert.Equal(3, cache.HitCount);
        Assert.Equal(2, cache.MissCount);
    }
    #endregion

    #region 并发安全
    [Fact]
    public void TestConcurrentAccess()
    {
        var cache = new PageCache(100);

        // 并发写入和读取不应抛异常
        Parallel.For(0, 1000, i =>
        {
            cache.Put((UInt64)i, [(Byte)(i % 256)]);
            cache.TryGet((UInt64)(i / 2), out _);
        });

        Assert.True(cache.Count <= 100); // 不超过容量
        Assert.True(cache.Count > 0);
    }
    #endregion
}

using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class PageCacheTests
{
    [Fact]
    public void TestPutAndGet()
    {
        var cache = new PageCache(3);

        var data1 = new byte[] { 1, 2, 3 };
        var data2 = new byte[] { 4, 5, 6 };

        cache.Put(1, data1);
        cache.Put(2, data2);

        Assert.True(cache.TryGet(1, out var retrievedData1));
        Assert.Equal(data1, retrievedData1);

        Assert.True(cache.TryGet(2, out var retrievedData2));
        Assert.Equal(data2, retrievedData2);

        Assert.False(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestLruEviction()
    {
        var cache = new PageCache(2);

        var data1 = new byte[] { 1 };
        var data2 = new byte[] { 2 };
        var data3 = new byte[] { 3 };

        cache.Put(1, data1);
        cache.Put(2, data2);

        // 缓存已满，添加第三个应淘汰第一个
        cache.Put(3, data3);

        Assert.False(cache.TryGet(1, out _)); // 应被淘汰
        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void TestRemove()
    {
        var cache = new PageCache(3);

        var data = new byte[] { 1, 2, 3 };
        cache.Put(1, data);

        Assert.True(cache.TryGet(1, out _));

        cache.Remove(1);

        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void TestClear()
    {
        var cache = new PageCache(3);

        cache.Put(1, new byte[] { 1 });
        cache.Put(2, new byte[] { 2 });

        Assert.Equal(2, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
    }
}

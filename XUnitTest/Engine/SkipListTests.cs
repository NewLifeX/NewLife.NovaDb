using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NewLife.NovaDb.Engine;

namespace XUnitTest.Engine;

/// <summary>
/// 跳表单元测试
/// </summary>
public class SkipListTests
{
    [Fact(DisplayName = "测试插入和查询")]
    public void TestInsertAndGet()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(1, "one");
        skipList.Insert(2, "two");
        skipList.Insert(3, "three");

        Assert.True(skipList.TryGetValue(1, out var value1));
        Assert.Equal("one", value1);

        Assert.True(skipList.TryGetValue(2, out var value2));
        Assert.Equal("two", value2);

        Assert.True(skipList.TryGetValue(3, out var value3));
        Assert.Equal("three", value3);
    }

    [Fact(DisplayName = "测试更新值")]
    public void TestUpdate()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(1, "one");
        skipList.Insert(1, "ONE");

        Assert.True(skipList.TryGetValue(1, out var value));
        Assert.Equal("ONE", value);
        Assert.Equal(1, skipList.Count);
    }

    [Fact(DisplayName = "测试删除")]
    public void TestRemove()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(1, "one");
        skipList.Insert(2, "two");
        skipList.Insert(3, "three");

        Assert.True(skipList.Remove(2));
        Assert.False(skipList.TryGetValue(2, out _));
        Assert.Equal(2, skipList.Count);

        Assert.False(skipList.Remove(99));
    }

    [Fact(DisplayName = "测试清空")]
    public void TestClear()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(1, "one");
        skipList.Insert(2, "two");
        skipList.Insert(3, "three");

        Assert.Equal(3, skipList.Count);

        skipList.Clear();

        Assert.Equal(0, skipList.Count);
        Assert.False(skipList.TryGetValue(1, out _));
    }

    [Fact(DisplayName = "测试包含键")]
    public void TestContainsKey()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(1, "one");

        Assert.True(skipList.ContainsKey(1));
        Assert.False(skipList.ContainsKey(2));
    }

    [Fact(DisplayName = "测试获取所有键值对")]
    public void TestGetAll()
    {
        var skipList = new SkipList<Int32, String>();

        skipList.Insert(3, "three");
        skipList.Insert(1, "one");
        skipList.Insert(2, "two");

        var all = skipList.GetAll();

        Assert.Equal(3, all.Count);
        // 跳表按键排序
        Assert.Equal(1, all[0].Key);
        Assert.Equal("one", all[0].Value);
        Assert.Equal(2, all[1].Key);
        Assert.Equal("two", all[1].Value);
        Assert.Equal(3, all[2].Key);
        Assert.Equal("three", all[2].Value);
    }

    [Fact(DisplayName = "测试大量数据")]
    public void TestLargeDataSet()
    {
        var skipList = new SkipList<Int32, String>();
        var count = 1000;

        // 插入
        for (var i = 0; i < count; i++)
        {
            skipList.Insert(i, $"value-{i}");
        }

        Assert.Equal(count, skipList.Count);

        // 查询
        for (var i = 0; i < count; i++)
        {
            Assert.True(skipList.TryGetValue(i, out var value));
            Assert.Equal($"value-{i}", value);
        }

        // 删除一半
        for (var i = 0; i < count / 2; i++)
        {
            Assert.True(skipList.Remove(i));
        }

        Assert.Equal(count / 2, skipList.Count);

        // 验证剩余的
        for (var i = count / 2; i < count; i++)
        {
            Assert.True(skipList.TryGetValue(i, out var value));
            Assert.Equal($"value-{i}", value);
        }
    }

    [Fact(DisplayName = "测试空键异常")]
    public void TestNullKeyException()
    {
        var skipList = new SkipList<String, Int32>();

        Assert.Throws<ArgumentNullException>(() => skipList.Insert(null!, 1));
        Assert.Throws<ArgumentNullException>(() => skipList.TryGetValue(null!, out _));
        Assert.Throws<ArgumentNullException>(() => skipList.Remove(null!));
    }
    private static ComparableObject K(Int32 value) => new(value);

    private static Int32[] KeysOf(List<KeyValuePair<ComparableObject, String>> entries)
        => entries.Select(e => Convert.ToInt32(e.Key.Value)).ToArray();

    [Fact(DisplayName = "降序范围遍历：空表与单元素")]
    public void TestGetRangeDescending_EmptyAndSingle()
    {
        var skipList = new SkipList<ComparableObject, String>();

        Assert.Empty(skipList.GetRange(null, true, null, true, 0, descending: true));
        Assert.Empty(skipList.GetRange(K(1), true, K(5), true, 0, descending: true));

        skipList.Insert(K(7), "seven");

        var single = skipList.GetRange(null, true, null, true, 0, descending: true);
        Assert.Single(single);
        Assert.Equal(7, Convert.ToInt32(single[0].Key.Value));

        // 单元素在区间内
        Assert.Equal(new[] { 7 }, KeysOf(skipList.GetRange(K(7), true, K(9), true, 0, descending: true)));
        Assert.Empty(skipList.GetRange(K(1), true, K(6), true, 0, descending: true));
    }

    [Fact(DisplayName = "降序范围遍历：开闭区间")]
    public void TestGetRangeDescending_Bounds()
    {
        var skipList = new SkipList<ComparableObject, String>();
        for (var i = 1; i <= 6; i++) skipList.Insert(K(i), $"v{i}");

        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, KeysOf(skipList.GetRange(null, true, null, true, 0, descending: true)));
        Assert.Equal(new[] { 5, 4, 3 }, KeysOf(skipList.GetRange(K(3), true, K(5), true, 0, descending: true)));
        Assert.Equal(new[] { 5, 4 }, KeysOf(skipList.GetRange(K(3), false, K(5), true, 0, descending: true)));
        Assert.Equal(new[] { 4, 3 }, KeysOf(skipList.GetRange(K(3), true, K(5), false, 0, descending: true)));
        Assert.Equal(new[] { 4 }, KeysOf(skipList.GetRange(K(3), false, K(5), false, 0, descending: true)));
        Assert.Equal(new[] { 2, 1 }, KeysOf(skipList.GetRange(K(1), true, K(3), false, 0, descending: true)));
        Assert.Empty(skipList.GetRange(K(7), true, null, true, 0, descending: true));
        Assert.Empty(skipList.GetRange(null, true, K(0), true, 0, descending: true));
        Assert.Empty(skipList.GetRange(K(4), true, K(2), true, 0, descending: true));
    }

    [Fact(DisplayName = "降序范围遍历：maxCount 从大端截断")]
    public void TestGetRangeDescending_MaxCount()
    {
        var skipList = new SkipList<ComparableObject, String>();
        for (var i = 1; i <= 10; i++) skipList.Insert(K(i), $"v{i}");

        Assert.Equal(new[] { 10, 9, 8 }, KeysOf(skipList.GetRange(null, true, null, true, 3, descending: true)));
        Assert.Equal(new[] { 8, 7 }, KeysOf(skipList.GetRange(K(2), true, K(8), true, 2, descending: true)));
        Assert.Equal(10, skipList.GetRange(null, true, null, true, 0, descending: true).Count);
        Assert.Empty(skipList.GetRange(K(11), true, K(20), true, 3, descending: true));
    }

    [Fact(DisplayName = "降序范围遍历：删除后前驱链仍完整")]
    public void TestGetRangeDescending_AfterRemove()
    {
        var skipList = new SkipList<ComparableObject, String>();
        for (var i = 1; i <= 6; i++) skipList.Insert(K(i), $"v{i}");

        // 删除中间与首尾节点后，第 0 层前驱链仍需完整
        Assert.True(skipList.Remove(K(3)));
        Assert.True(skipList.Remove(K(1)));
        Assert.True(skipList.Remove(K(6)));

        Assert.Equal(new[] { 5, 4, 2 }, KeysOf(skipList.GetRange(null, true, null, true, 0, descending: true)));
        Assert.Equal(new[] { 2, 4, 5 }, KeysOf(skipList.GetRange(null, true, null, true, 0)));

        skipList.Insert(K(3), "v3");
        Assert.Equal(new[] { 5, 4, 3, 2 }, KeysOf(skipList.GetRange(null, true, null, true, 0, descending: true)));

        skipList.Clear();
        Assert.Empty(skipList.GetRange(null, true, null, true, 0, descending: true));
        Assert.Empty(skipList.GetRange(null, true, null, true, 0));
    }

    [Fact(DisplayName = "降序范围遍历：大量数据与正向互逆")]
    public void TestGetRangeDescending_LargeDataSet()
    {
        var skipList = new SkipList<ComparableObject, String>();
        for (var i = 0; i < 500; i++) skipList.Insert(K(i), $"v{i}");

        var ascending = skipList.GetRange(K(100), true, K(399), true, 0);
        var descending = skipList.GetRange(K(100), true, K(399), true, 0, descending: true);

        Assert.Equal(300, ascending.Count);
        Assert.Equal(300, descending.Count);
        for (var i = 0; i < ascending.Count; i++)
        {
            Assert.Equal(ascending[i].Key.Value, descending[ascending.Count - 1 - i].Key.Value);
        }

        // 分块截断（每次只取一部分）也不能重复或漏项
        var paged = new List<Int32>();
        var upper = 399;
        while (true)
        {
            var chunk = skipList.GetRange(K(100), true, K(upper), true, 37, descending: true);
            if (chunk.Count == 0) break;

            foreach (var entry in chunk) paged.Add(Convert.ToInt32(entry.Key.Value));
            if (chunk.Count < 37) break;

            upper = Convert.ToInt32(chunk[^1].Key.Value) - 1;
        }

        Assert.Equal(KeysOf(descending), paged.ToArray());
    }
}

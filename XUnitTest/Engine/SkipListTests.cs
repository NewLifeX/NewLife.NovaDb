using System;
using System.Collections.Generic;
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
}

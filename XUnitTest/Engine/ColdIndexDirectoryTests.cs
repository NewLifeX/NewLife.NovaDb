using System;
using System.Linq;
using Xunit;
using NewLife.NovaDb.Engine;

namespace XUnitTest.Engine;

/// <summary>
/// 冷索引目录单元测试
/// </summary>
public class ColdIndexDirectoryTests
{
    [Fact(DisplayName = "测试创建冷索引目录")]
    public void TestCreateColdIndexDirectory()
    {
        var directory = new ColdIndexDirectory(1000);

        Assert.Equal(1000, directory.AnchorInterval);
        Assert.Equal(0, directory.AnchorCount);
    }

    [Fact(DisplayName = "测试添加锚点")]
    public void TestAddAnchor()
    {
        var directory = new ColdIndexDirectory();

        directory.AddAnchor(100, 1, 0);
        directory.AddAnchor(200, 2, 0);
        directory.AddAnchor(300, 3, 0);

        Assert.Equal(3, directory.AnchorCount);
    }

    [Fact(DisplayName = "测试查找起始位置")]
    public void TestFindStartPosition()
    {
        var directory = new ColdIndexDirectory();

        directory.AddAnchor(100, 1, 0);
        directory.AddAnchor(200, 2, 0);
        directory.AddAnchor(300, 3, 0);
        directory.AddAnchor(400, 4, 0);

        // 查找 250，应该返回锚点 200
        var entry = directory.FindStartPosition(250);
        Assert.NotNull(entry);
        Assert.Equal(200, entry!.Key);
        Assert.Equal(2UL, entry.PageId);

        // 查找 150，应该返回锚点 100
        entry = directory.FindStartPosition(150);
        Assert.NotNull(entry);
        Assert.Equal(100, entry!.Key);

        // 查找 50，应该返回 null（小于最小锚点）
        entry = directory.FindStartPosition(50);
        Assert.Null(entry);

        // 查找 500，应该返回锚点 400（大于最大锚点）
        entry = directory.FindStartPosition(500);
        Assert.NotNull(entry);
        Assert.Equal(400, entry!.Key);
    }

    [Fact(DisplayName = "测试获取范围")]
    public void TestGetRange()
    {
        var directory = new ColdIndexDirectory();

        directory.AddAnchor(100, 1, 0);
        directory.AddAnchor(200, 2, 0);
        directory.AddAnchor(300, 3, 0);
        directory.AddAnchor(400, 4, 0);
        directory.AddAnchor(500, 5, 0);

        // 获取 [200, 400] 范围
        var range = directory.GetRange(200, 400);
        Assert.Equal(3, range.Count);
        Assert.Equal(200, range[0].Key);
        Assert.Equal(300, range[1].Key);
        Assert.Equal(400, range[2].Key);

        // 获取 [250, 450] 范围
        range = directory.GetRange(250, 450);
        Assert.Equal(2, range.Count);
        Assert.Equal(300, range[0].Key);
        Assert.Equal(400, range[1].Key);

        // 获取全部
        range = directory.GetRange(null, null);
        Assert.Equal(5, range.Count);
    }

    [Fact(DisplayName = "测试清空锚点")]
    public void TestClear()
    {
        var directory = new ColdIndexDirectory();

        directory.AddAnchor(100, 1, 0);
        directory.AddAnchor(200, 2, 0);
        directory.AddAnchor(300, 3, 0);

        Assert.Equal(3, directory.AnchorCount);

        directory.Clear();

        Assert.Equal(0, directory.AnchorCount);
    }

    [Fact(DisplayName = "测试获取所有锚点")]
    public void TestGetAllAnchors()
    {
        var directory = new ColdIndexDirectory();

        directory.AddAnchor(100, 1, 0);
        directory.AddAnchor(200, 2, 0);
        directory.AddAnchor(300, 3, 0);

        var anchors = directory.GetAllAnchors();

        Assert.Equal(3, anchors.Count);
        Assert.Equal(100, anchors[0].Key);
        Assert.Equal(200, anchors[1].Key);
        Assert.Equal(300, anchors[2].Key);
    }

    [Fact(DisplayName = "测试空锚点异常")]
    public void TestNullKeyException()
    {
        var directory = new ColdIndexDirectory();

        Assert.Throws<ArgumentNullException>(() => directory.AddAnchor(null!, 1, 0));
    }

    [Fact(DisplayName = "测试无效锚点间隔")]
    public void TestInvalidAnchorInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColdIndexDirectory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColdIndexDirectory(-1));
    }

    [Fact(DisplayName = "测试大量锚点性能")]
    public void TestLargeNumberOfAnchors()
    {
        var directory = new ColdIndexDirectory();

        // 添加 10000 个锚点
        for (var i = 0; i < 10000; i++)
        {
            directory.AddAnchor(i * 100, (UInt64)i, 0);
        }

        Assert.Equal(10000, directory.AnchorCount);

        // 测试查找性能（二分查找应该很快）
        var entry = directory.FindStartPosition(500000);
        Assert.NotNull(entry);
        Assert.Equal(500000, entry!.Key);

        // 测试范围查询
        var range = directory.GetRange(100000, 200000);
        Assert.Equal(1001, range.Count);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using NewLife.NovaDb.Engine;

namespace XUnitTest.Engine;

/// <summary>
/// 热索引管理器单元测试
/// </summary>
public class HotIndexManagerTests
{
    [Fact(DisplayName = "测试创建热索引管理器")]
    public void TestCreateHotIndexManager()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        Assert.NotNull(manager);
        Assert.Equal(config, manager.Config);
        Assert.Equal(0, manager.HotSegmentCount);
    }

    [Fact(DisplayName = "测试添加热段")]
    public void TestAddHotSegment()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        var segment = new IndexSegment
        {
            SegmentId = 1,
            MinKey = 100,
            MaxKey = 200,
            RowCount = 100
        };

        manager.AddHotSegment(segment);

        Assert.Equal(1, manager.HotSegmentCount);
        Assert.True(segment.IsHot);
    }

    [Fact(DisplayName = "测试移除热段")]
    public void TestRemoveHotSegment()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        var segment = new IndexSegment
        {
            SegmentId = 1,
            MinKey = 100,
            MaxKey = 200,
            RowCount = 100
        };

        manager.AddHotSegment(segment);
        Assert.Equal(1, manager.HotSegmentCount);

        var removed = manager.RemoveHotSegment(100);
        Assert.True(removed);
        Assert.Equal(0, manager.HotSegmentCount);
    }

    [Fact(DisplayName = "测试访问键更新热度")]
    public void TestAccessKey()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        var segment = new IndexSegment
        {
            SegmentId = 1,
            MinKey = 100,
            MaxKey = 200,
            RowCount = 100
        };

        manager.AddHotSegment(segment);

        var initialAccessTime = segment.LastAccessTime;
        Thread.Sleep(50);

        manager.AccessKey(100);

        Assert.True(segment.LastAccessTime > initialAccessTime);
    }

    [Fact(DisplayName = "测试淘汰冷段")]
    public void TestEvictColdSegments()
    {
        var config = new HotSegmentConfig
        {
            ColdEvictionSeconds = 1  // 1 秒后淘汰
        };
        var manager = new HotIndexManager(config);

        // 添加段
        var segment1 = new IndexSegment { SegmentId = 1, MinKey = 100, MaxKey = 200, RowCount = 100 };
        var segment2 = new IndexSegment { SegmentId = 2, MinKey = 300, MaxKey = 400, RowCount = 100 };

        manager.AddHotSegment(segment1);
        manager.AddHotSegment(segment2);

        // 等待超过淘汰阈值
        Thread.Sleep(1100);

        // 访问 segment2 保持其热度
        manager.AccessKey(300);

        // 淘汰冷段
        var evicted = manager.EvictColdSegments();

        // segment1 应该被淘汰，segment2 不应该
        Assert.Single(evicted);
        Assert.Equal(1, evicted[0].SegmentId);
        Assert.False(evicted[0].IsHot);
        Assert.Equal(1, manager.HotSegmentCount);
    }

    [Fact(DisplayName = "测试查找段")]
    public void TestFindSegment()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        var segment = new IndexSegment
        {
            SegmentId = 1,
            MinKey = 100,
            MaxKey = 200,
            RowCount = 100
        };

        manager.AddHotSegment(segment);

        var found = manager.FindSegment(100);
        Assert.NotNull(found);
        Assert.Equal(1, found!.SegmentId);

        var notFound = manager.FindSegment(999);
        Assert.Null(notFound);
    }

    [Fact(DisplayName = "测试获取所有热段")]
    public void TestGetAllHotSegments()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        manager.AddHotSegment(new IndexSegment { SegmentId = 1, MinKey = 100, MaxKey = 200, RowCount = 100 });
        manager.AddHotSegment(new IndexSegment { SegmentId = 2, MinKey = 300, MaxKey = 400, RowCount = 100 });
        manager.AddHotSegment(new IndexSegment { SegmentId = 3, MinKey = 500, MaxKey = 600, RowCount = 100 });

        var allSegments = manager.GetAllHotSegments();

        Assert.Equal(3, allSegments.Count);
    }

    [Fact(DisplayName = "测试清空热段")]
    public void TestClear()
    {
        var config = new HotSegmentConfig();
        var manager = new HotIndexManager(config);

        manager.AddHotSegment(new IndexSegment { SegmentId = 1, MinKey = 100, MaxKey = 200, RowCount = 100 });
        manager.AddHotSegment(new IndexSegment { SegmentId = 2, MinKey = 300, MaxKey = 400, RowCount = 100 });

        Assert.Equal(2, manager.HotSegmentCount);

        manager.Clear();

        Assert.Equal(0, manager.HotSegmentCount);
    }

    [Fact(DisplayName = "测试是否需要热度检查")]
    public void TestShouldCheckHeat()
    {
        var config = new HotSegmentConfig
        {
            HeatCheckIntervalSeconds = 1
        };
        var manager = new HotIndexManager(config);

        // 刚创建时不需要检查
        Assert.False(manager.ShouldCheckHeat());

        // 等待超过检查间隔
        Thread.Sleep(1100);

        Assert.True(manager.ShouldCheckHeat());

        // 执行淘汰后重置检查时间
        manager.EvictColdSegments();
        Assert.False(manager.ShouldCheckHeat());
    }
}

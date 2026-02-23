using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Engine;

public class MaterializedViewManagerTests : IDisposable
{
    private readonly String _dbPath;
    private readonly SqlEngine _engine;
    private readonly MaterializedViewManager _manager;

    public MaterializedViewManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nova_mv_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);

        _engine = new SqlEngine(_dbPath);
        _manager = new MaterializedViewManager(_engine);

        // 创建测试表
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, product STRING, amount DOUBLE)");
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (1, 'Apple', 10.5)");
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (2, 'Banana', 20.0)");
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (3, 'Apple', 15.0)");
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (4, 'Banana', 25.5)");
    }

    public void Dispose()
    {
        _manager.Dispose();
        _engine.Dispose();

        try { Directory.Delete(_dbPath, true); } catch { }
    }

    [Fact(DisplayName = "创建物化视图并查询")]
    public void CreateAndQuery()
    {
        var view = _manager.Create("order_summary",
            "SELECT product, SUM(amount) AS total FROM orders GROUP BY product");

        Assert.NotNull(view);
        Assert.Equal("order_summary", view.Name);
        Assert.True(view.RefreshCount > 0);

        var result = _manager.Query("order_summary");
        Assert.True(result.IsQuery);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "刷新物化视图反映最新数据")]
    public void RefreshReflectsNewData()
    {
        _manager.Create("order_count", "SELECT COUNT(*) AS cnt FROM orders");

        var result1 = _manager.Query("order_count");
        Assert.Equal(4, Convert.ToInt32(result1.Rows[0][0]));

        // 插入新数据
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (5, 'Cherry', 30.0)");

        // 刷新视图
        _manager.Refresh("order_count");

        var result2 = _manager.Query("order_count");
        Assert.Equal(5, Convert.ToInt32(result2.Rows[0][0]));
    }

    [Fact(DisplayName = "删除物化视图")]
    public void DropView()
    {
        _manager.Create("temp_view", "SELECT COUNT(*) AS cnt FROM orders");
        Assert.Equal(1, _manager.Count);

        var dropped = _manager.Drop("temp_view");
        Assert.True(dropped);
        Assert.Equal(0, _manager.Count);

        Assert.Null(_manager.Get("temp_view"));
    }

    [Fact(DisplayName = "创建重名视图抛异常")]
    public void DuplicateNameThrows()
    {
        _manager.Create("dup_view", "SELECT COUNT(*) AS cnt FROM orders");

        var ex = Assert.Throws<NovaException>(() =>
            _manager.Create("dup_view", "SELECT COUNT(*) AS cnt FROM orders"));
        Assert.Equal(ErrorCode.TableExists, ex.Code);
    }

    [Fact(DisplayName = "查询不存在的视图抛异常")]
    public void QueryNonexistentViewThrows()
    {
        var ex = Assert.Throws<NovaException>(() => _manager.Query("nonexistent"));
        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "获取所有视图")]
    public void GetAllViews()
    {
        _manager.Create("view1", "SELECT COUNT(*) AS cnt FROM orders");
        _manager.Create("view2", "SELECT SUM(amount) AS total FROM orders");

        var all = _manager.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact(DisplayName = "定时刷新策略检查")]
    public void NeedsRefreshCheck()
    {
        var view = _manager.Create("timed_view", "SELECT COUNT(*) AS cnt FROM orders", refreshIntervalSeconds: 1);

        // 刚创建完应该不需要刷新
        Assert.False(view.NeedsRefresh());

        // 强制更新时间使其过期
        view.LastRefreshTime = DateTime.UtcNow.AddSeconds(-2);
        Assert.True(view.NeedsRefresh());
    }

    [Fact(DisplayName = "RefreshDue 刷新到期视图")]
    public void RefreshDueUpdatesExpiredViews()
    {
        var view = _manager.Create("due_view", "SELECT COUNT(*) AS cnt FROM orders", refreshIntervalSeconds: 1);

        // 未到期时不刷新
        var refreshed = _manager.RefreshDue();
        Assert.Equal(0, refreshed);

        // 让视图过期
        view.LastRefreshTime = DateTime.UtcNow.AddSeconds(-2);

        // 插入新数据
        _engine.Execute("INSERT INTO orders (id, product, amount) VALUES (10, 'Date', 5.0)");

        refreshed = _manager.RefreshDue();
        Assert.Equal(1, refreshed);

        var result = _manager.Query("due_view");
        Assert.Equal(5, Convert.ToInt32(result.Rows[0][0]));
    }

    [Fact(DisplayName = "物化视图记录刷新统计")]
    public void RefreshStatistics()
    {
        var view = _manager.Create("stats_view", "SELECT COUNT(*) AS cnt FROM orders");

        Assert.Equal(1L, view.RefreshCount);
        Assert.True(view.LastRefreshMs >= 0);
        Assert.True(view.LastRefreshTime > DateTime.MinValue);

        _manager.Refresh("stats_view");
        Assert.Equal(2L, view.RefreshCount);
    }

    [Fact(DisplayName = "获取视图详情")]
    public void GetViewDetails()
    {
        _manager.Create("detail_view", "SELECT product, COUNT(*) AS cnt FROM orders GROUP BY product");

        var view = _manager.Get("detail_view");
        Assert.NotNull(view);
        Assert.Equal("detail_view", view!.Name);
        Assert.Equal(2, view.Rows.Count);
        Assert.Contains("product", view.ColumnNames);
    }

    [Fact(DisplayName = "删除不存在的视图返回 false")]
    public void DropNonexistentReturnsFalse()
    {
        var result = _manager.Drop("nonexistent");
        Assert.False(result);
    }

    [Fact(DisplayName = "Dispose 后操作抛异常")]
    public void OperationsAfterDisposeThrow()
    {
        var engine2 = new SqlEngine(Path.Combine(Path.GetTempPath(), $"nova_mv_disp_{Guid.NewGuid():N}"));
        var mgr2 = new MaterializedViewManager(engine2);
        mgr2.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            mgr2.Create("v1", "SELECT 1"));

        engine2.Dispose();
    }
}

using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>会话/状态窗口分组测试（F17）</summary>
public class WindowFunctionTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public WindowFunctionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"WindowFuncTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private void CreateSessionTable()
    {
        _engine.Execute("CREATE TABLE tracks (id INT PRIMARY KEY, ts DATETIME, speed INT)");
        _engine.Execute("INSERT INTO tracks VALUES (1, '2026-01-01 10:00:00', 30)");
        _engine.Execute("INSERT INTO tracks VALUES (2, '2026-01-01 10:02:00', 32)");
        _engine.Execute("INSERT INTO tracks VALUES (3, '2026-01-01 10:04:00', 34)");
        _engine.Execute("INSERT INTO tracks VALUES (4, '2026-01-01 10:20:00', 60)");
        _engine.Execute("INSERT INTO tracks VALUES (5, '2026-01-01 10:21:00', 62)");
    }

    [Fact(DisplayName = "SESSION_WINDOW 按时间间隔切分会话")]
    public void SessionWindow_SplitsByGap()
    {
        CreateSessionTable();

        var result = _engine.Execute("SELECT SESSION_WINDOW(ts, 300) AS session, COUNT(*) AS cnt FROM tracks GROUP BY SESSION_WINDOW(ts, 300)");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0), result.Rows[0][0]);
        Assert.Equal(3L, Convert.ToInt64(result.Rows[0][1]));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 20, 0), result.Rows[1][0]);
        Assert.Equal(2L, Convert.ToInt64(result.Rows[1][1]));
    }

    [Fact(DisplayName = "SESSION_WINDOW 支持数值时间戳")]
    public void SessionWindow_NumericTimestamps()
    {
        _engine.Execute("CREATE TABLE samples (id INT PRIMARY KEY, epoch BIGINT, value INT)");
        _engine.Execute("INSERT INTO samples VALUES (1, 1000, 5)");
        _engine.Execute("INSERT INTO samples VALUES (2, 1010, 6)");
        _engine.Execute("INSERT INTO samples VALUES (3, 1100, 7)");

        var result = _engine.Execute("SELECT SESSION_WINDOW(epoch, 30) AS session, SUM(value) FROM samples GROUP BY SESSION_WINDOW(epoch, 30)");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1000L, result.Rows[0][0]);
        Assert.Equal(11L, Convert.ToInt64(result.Rows[0][1]));
        Assert.Equal(1100L, result.Rows[1][0]);
    }

    [Fact(DisplayName = "SESSION_WINDOW 按设备分组独立计算会话")]
    public void SessionWindow_GroupedByDevice()
    {
        _engine.Execute("CREATE TABLE devices (id INT PRIMARY KEY, device VARCHAR, ts DATETIME)");
        _engine.Execute("INSERT INTO devices VALUES (1, 'A', '2026-01-01 10:00:00')");
        _engine.Execute("INSERT INTO devices VALUES (2, 'B', '2026-01-01 10:01:00')");
        _engine.Execute("INSERT INTO devices VALUES (3, 'A', '2026-01-01 10:30:00')");
        _engine.Execute("INSERT INTO devices VALUES (4, 'B', '2026-01-01 10:02:00')");

        var result = _engine.Execute("SELECT device, SESSION_WINDOW(ts, 300) AS session, COUNT(*) FROM devices GROUP BY device, SESSION_WINDOW(ts, 300)");

        // 会话窗口按设备隔离：A 两段会话，B 一段会话
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("A", result.Rows[0][0]);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0), result.Rows[0][1]);
        Assert.Equal(1L, Convert.ToInt64(result.Rows[0][2]));
        Assert.Equal("B", result.Rows[1][0]);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 1, 0), result.Rows[1][1]);
        Assert.Equal(2L, Convert.ToInt64(result.Rows[1][2]));
        Assert.Equal("A", result.Rows[2][0]);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 30, 0), result.Rows[2][1]);
    }

    [Fact(DisplayName = "STATE_WINDOW 按状态变化切分窗口")]
    public void StateWindow_SplitsByValueChange()
    {
        _engine.Execute("CREATE TABLE states (id INT PRIMARY KEY, status VARCHAR, value INT)");
        _engine.Execute("INSERT INTO states VALUES (1, 'ok', 10)");
        _engine.Execute("INSERT INTO states VALUES (2, 'ok', 20)");
        _engine.Execute("INSERT INTO states VALUES (3, 'warn', 30)");
        _engine.Execute("INSERT INTO states VALUES (4, 'warn', 40)");
        _engine.Execute("INSERT INTO states VALUES (5, 'warn', 50)");
        _engine.Execute("INSERT INTO states VALUES (6, 'ok', 60)");

        var result = _engine.Execute("SELECT STATE_WINDOW(status) AS win, COUNT(*) AS cnt, MAX(value) AS peak FROM states GROUP BY STATE_WINDOW(status)");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(1L, Convert.ToInt64(result.Rows[0][0]));
        Assert.Equal(2L, Convert.ToInt64(result.Rows[0][1]));
        Assert.Equal(3L, Convert.ToInt64(result.Rows[1][1]));
        Assert.Equal(1L, Convert.ToInt64(result.Rows[2][1]));
        Assert.Equal(60L, Convert.ToInt64(result.Rows[2][2]));
    }

    [Fact(DisplayName = "STATE_WINDOW 带时间参数返回窗口起始时间")]
    public void StateWindow_WithTimestamp()
    {
        _engine.Execute("CREATE TABLE status_logs (id INT PRIMARY KEY, status VARCHAR, ts DATETIME)");
        _engine.Execute("INSERT INTO status_logs VALUES (1, 'idle', '2026-01-01 08:00:00')");
        _engine.Execute("INSERT INTO status_logs VALUES (2, 'idle', '2026-01-01 08:30:00')");
        _engine.Execute("INSERT INTO status_logs VALUES (3, 'busy', '2026-01-01 09:00:00')");
        _engine.Execute("INSERT INTO status_logs VALUES (4, 'idle', '2026-01-01 09:15:00')");

        var result = _engine.Execute("SELECT STATE_WINDOW(status, ts) AS win_start, COUNT(*) FROM status_logs GROUP BY STATE_WINDOW(status, ts)");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), result.Rows[0][0]);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), result.Rows[1][0]);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 15, 0), result.Rows[2][0]);
    }

    [Fact(DisplayName = "窗口函数在非分组场景使用时报错")]
    public void WindowFunction_OutsideGroupBy_Throws()
    {
        CreateSessionTable();

        var ex = Assert.Throws<NovaException>(() => _engine.Execute("SELECT SESSION_WINDOW(ts, 300) FROM tracks"));
        Assert.Contains("GROUP BY", ex.Message);

        var ex2 = Assert.Throws<NovaException>(() => _engine.Execute("SELECT STATE_WINDOW(speed) FROM tracks"));
        Assert.Contains("GROUP BY", ex2.Message);
    }

    [Fact(DisplayName = "SESSION_WINDOW 参数不足时报错")]
    public void SessionWindow_MissingArguments_Throws()
    {
        CreateSessionTable();

        Assert.Throws<NovaException>(() => _engine.Execute("SELECT COUNT(*) FROM tracks GROUP BY SESSION_WINDOW(ts)"));
    }
}

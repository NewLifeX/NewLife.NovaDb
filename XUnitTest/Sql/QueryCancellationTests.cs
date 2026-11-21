using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>查询协作式取消测试</summary>
public class QueryCancellationTests : IDisposable
{
    private const Int32 RowCount = 100_000;
    private const Int32 BatchSize = 1_000;

    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public QueryCancellationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaCancel_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();

        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }

    /// <summary>创建十万行测试表，分批多值插入以缩短准备时间</summary>
    /// <param name="engine">SQL 引擎</param>
    private static void CreateBigTable(SqlEngine engine)
    {
        engine.Execute("CREATE TABLE big (id INT PRIMARY KEY, value INT NOT NULL, name VARCHAR)");

        for (var b = 0; b < RowCount / BatchSize; b++)
        {
            var sb = new StringBuilder("INSERT INTO big VALUES ");
            for (var i = 1; i <= BatchSize; i++)
            {
                var id = b * BatchSize + i;
                if (i > 1) sb.Append(',');
                sb.Append('(').Append(id).Append(',').Append(id % 1000).Append(",'n").Append(id).Append("')");
            }
            engine.Execute(sb.ToString());
        }
    }

    [Fact(DisplayName = "取消-预取消令牌立即抛出取消异常")]
    public void PreCancelledToken_ThrowsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        // 表不存在：若引擎未在扫描前检查令牌，会抛 TableNotFound 而不是取消异常
        Assert.Throws<OperationCanceledException>(() =>
            _engine.Execute("SELECT COUNT(*) FROM big", null, null, cts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"预取消令牌应立即失败，实际耗时 {sw.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "取消-十万行全表扫描五十毫秒后取消且两秒内抛出")]
    public void SlowScan_CancelledAfter50ms()
    {
        CreateBigTable(_engine);

        using var cts = new CancellationTokenSource(50);
        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            _engine.Execute("SELECT * FROM big WHERE value >= 0", null, null, cts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"取消应在 2 秒内生效，实际耗时 {sw.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "取消-取消后同一引擎仍可正常执行查询")]
    public void Engine_StillUsableAfterCancellation()
    {
        CreateBigTable(_engine);

        using (var cts = new CancellationTokenSource(50))
        {
            Assert.Throws<OperationCanceledException>(() =>
                _engine.Execute("SELECT * FROM big WHERE value >= 0", null, null, cts.Token));
        }

        var result = _engine.Execute("SELECT 1");
        Assert.Equal(1, Convert.ToInt32(result.GetScalar()));

        result = _engine.Execute("SELECT COUNT(*) FROM big");
        Assert.Equal(100_000L, Convert.ToInt64(result.GetScalar()));
    }

    [Fact(DisplayName = "取消-命令超时抛出超时异常且连接仍可用")]
    public void CommandTimeout_ThrowsTimeoutException_AndConnectionSurvives()
    {
        var clientDir = Path.Combine(_testDir, "client");
        Directory.CreateDirectory(clientDir);

        using var conn = new NovaConnection { ConnectionString = $"Data Source={clientDir}" };
        conn.Open();

        CreateBigTable(conn.SqlEngine!);

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 1;
        // 十万行自连接为嵌套循环慢查询，耗时远超 1 秒，用于验证协作式超时能中断扫描
        cmd.CommandText = "SELECT COUNT(*) FROM big a JOIN big b ON a.value = b.value";

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<NovaException>(() => cmd.ExecuteScalar());
        sw.Stop();

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"命令超时应及时抛出，实际耗时 {sw.ElapsedMilliseconds}ms");

        // 超时后同一连接继续可用
        cmd.CommandTimeout = 30;
        cmd.CommandText = "SELECT 1";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}

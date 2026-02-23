using System.Threading;

namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 运行时指标</summary>
/// <remarks>所有计数器均为线程安全，支持多线程并发递增</remarks>
public class NovaMetrics
{
    #region 属性
    /// <summary>表数量</summary>
    public Int32 TableCount { get; set; }

    /// <summary>总行数（所有表）</summary>
    public Int64 TotalRows { get; set; }

    /// <summary>启动时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>运行时长</summary>
    public TimeSpan Uptime => DateTime.Now - StartTime;
    #endregion

    #region 计数器（线程安全）
    private Int64 _executeCount;
    private Int64 _queryCount;
    private Int64 _insertCount;
    private Int64 _updateCount;
    private Int64 _deleteCount;
    private Int64 _ddlCount;
    private Int64 _commitCount;
    private Int64 _rollbackCount;

    /// <summary>SQL 执行次数</summary>
    public Int64 ExecuteCount { get => Interlocked.Read(ref _executeCount); set => Interlocked.Exchange(ref _executeCount, value); }

    /// <summary>查询次数</summary>
    public Int64 QueryCount { get => Interlocked.Read(ref _queryCount); set => Interlocked.Exchange(ref _queryCount, value); }

    /// <summary>插入次数</summary>
    public Int64 InsertCount { get => Interlocked.Read(ref _insertCount); set => Interlocked.Exchange(ref _insertCount, value); }

    /// <summary>更新次数</summary>
    public Int64 UpdateCount { get => Interlocked.Read(ref _updateCount); set => Interlocked.Exchange(ref _updateCount, value); }

    /// <summary>删除次数</summary>
    public Int64 DeleteCount { get => Interlocked.Read(ref _deleteCount); set => Interlocked.Exchange(ref _deleteCount, value); }

    /// <summary>DDL 次数</summary>
    public Int64 DdlCount { get => Interlocked.Read(ref _ddlCount); set => Interlocked.Exchange(ref _ddlCount, value); }

    /// <summary>事务提交次数</summary>
    public Int64 CommitCount { get => Interlocked.Read(ref _commitCount); set => Interlocked.Exchange(ref _commitCount, value); }

    /// <summary>事务回滚次数</summary>
    public Int64 RollbackCount { get => Interlocked.Read(ref _rollbackCount); set => Interlocked.Exchange(ref _rollbackCount, value); }
    #endregion
}

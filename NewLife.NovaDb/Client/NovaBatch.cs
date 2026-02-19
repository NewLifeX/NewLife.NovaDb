#if NET6_0_OR_GREATER
using System.Data;
using System.Data.Common;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb 批量命令项</summary>
public class NovaBatchCommand : DbBatchCommand
{
    /// <summary>SQL 命令文本</summary>
    public override String CommandText { get; set; } = String.Empty;

    /// <summary>命令类型</summary>
    public override CommandType CommandType { get; set; } = CommandType.Text;

    private readonly NovaParameterCollection _parameters = [];

    /// <summary>参数集合</summary>
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>受影响行数</summary>
    public override Int32 RecordsAffected => 0;
}

/// <summary>NovaDb 批量命令项集合</summary>
public class NovaBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<NovaBatchCommand> _commands = [];

    /// <summary>集合数量</summary>
    public override Int32 Count => _commands.Count;

    /// <summary>是否只读</summary>
    public override Boolean IsReadOnly => false;

    /// <summary>按索引获取批量命令项</summary>
    /// <param name="index">索引</param>
    /// <returns>批量命令项</returns>
    public new NovaBatchCommand this[Int32 index]
    {
        get => _commands[index];
        set => _commands[index] = value;
    }

    /// <summary>添加命令项</summary>
    /// <param name="item">批量命令项</param>
    public override void Add(DbBatchCommand item) => _commands.Add((NovaBatchCommand)item);

    /// <summary>清空</summary>
    public override void Clear() => _commands.Clear();

    /// <summary>是否包含</summary>
    /// <param name="item">批量命令项</param>
    /// <returns>是否包含</returns>
    public override Boolean Contains(DbBatchCommand item) => _commands.Contains((NovaBatchCommand)item);

    /// <summary>复制到数组</summary>
    /// <param name="array">目标数组</param>
    /// <param name="arrayIndex">起始索引</param>
    public override void CopyTo(DbBatchCommand[] array, Int32 arrayIndex)
    {
        for (var i = 0; i < _commands.Count; i++)
        {
            array[arrayIndex + i] = _commands[i];
        }
    }

    /// <summary>获取索引</summary>
    /// <param name="item">批量命令项</param>
    /// <returns>索引</returns>
    public override Int32 IndexOf(DbBatchCommand item) => _commands.IndexOf((NovaBatchCommand)item);

    /// <summary>插入</summary>
    /// <param name="index">插入位置</param>
    /// <param name="item">批量命令项</param>
    public override void Insert(Int32 index, DbBatchCommand item) => _commands.Insert(index, (NovaBatchCommand)item);

    /// <summary>移除</summary>
    /// <param name="item">批量命令项</param>
    /// <returns>是否移除成功</returns>
    public override Boolean Remove(DbBatchCommand item) => _commands.Remove((NovaBatchCommand)item);

    /// <summary>按索引移除</summary>
    /// <param name="index">索引</param>
    public override void RemoveAt(Int32 index) => _commands.RemoveAt(index);

    /// <summary>获取枚举器</summary>
    /// <returns>枚举器</returns>
    public override IEnumerator<DbBatchCommand> GetEnumerator() => _commands.Cast<DbBatchCommand>().GetEnumerator();

    /// <summary>按索引获取批量命令项</summary>
    /// <param name="index">索引</param>
    /// <returns>批量命令项</returns>
    protected override DbBatchCommand GetBatchCommand(Int32 index) => _commands[index];

    /// <summary>按索引设置批量命令项</summary>
    /// <param name="index">索引</param>
    /// <param name="batchCommand">批量命令项</param>
    protected override void SetBatchCommand(Int32 index, DbBatchCommand batchCommand) => _commands[index] = (NovaBatchCommand)batchCommand;
}

/// <summary>NovaDb 批量命令。支持多条 SQL 语句在一次调用中执行</summary>
public class NovaBatch : DbBatch
{
    #region 属性
    private NovaConnection? _connection;

    /// <summary>连接</summary>
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as NovaConnection;
    }

    private DbTransaction? _transaction;

    /// <summary>事务</summary>
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    /// <summary>超时时间（秒）</summary>
    public override Int32 Timeout { get; set; } = 30;

    private readonly NovaBatchCommandCollection _commands = [];

    /// <summary>命令集合</summary>
    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    /// <summary>命令集合</summary>
    public new NovaBatchCommandCollection BatchCommands => _commands;
    #endregion

    #region 构造
    /// <summary>实例化</summary>
    public NovaBatch() { }

    /// <summary>使用连接实例化</summary>
    /// <param name="connection">连接</param>
    public NovaBatch(NovaConnection connection) => _connection = connection;
    #endregion

    #region 方法
    /// <summary>创建批量命令项</summary>
    /// <returns>批量命令项实例</returns>
    protected override DbBatchCommand CreateDbBatchCommand() => new NovaBatchCommand();

    /// <summary>执行批量命令并返回读取器</summary>
    /// <param name="behavior">命令行为</param>
    /// <returns>数据读取器</returns>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>异步执行批量命令并返回读取器</summary>
    /// <param name="behavior">命令行为</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>数据读取器</returns>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        EnsureValid();

        var combinedSql = BuildCombinedSql();
        using var cmd = new NovaCommand { CommandText = combinedSql, Connection = _connection };
        return await cmd.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行并返回总影响行数</summary>
    /// <returns>受影响行数</returns>
    public override Int32 ExecuteNonQuery()
        => ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>异步执行并返回总影响行数</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>受影响行数</returns>
    public override async Task<Int32> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        EnsureValid();

        var total = 0;
        for (var i = 0; i < _commands.Count; i++)
        {
            var sql = _commands[i].CommandText;
            if (String.IsNullOrEmpty(sql)) continue;

            using var cmd = new NovaCommand { CommandText = sql, Connection = _connection };
            total += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>执行并返回第一个结果集的第一行第一列</summary>
    /// <returns>标量值</returns>
    public override Object? ExecuteScalar()
        => ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>异步执行并返回第一个结果集的第一行第一列</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>标量值</returns>
    public override async Task<Object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        EnsureValid();

        var sql = _commands[0].CommandText;
        if (String.IsNullOrEmpty(sql)) return null;

        using var cmd = new NovaCommand { CommandText = sql, Connection = _connection };
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>预编译</summary>
    public override void Prepare() { }

    /// <summary>异步预编译</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns></returns>
    public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>取消</summary>
    public override void Cancel() { }

    /// <summary>释放</summary>
    public override void Dispose() { }
    #endregion

    #region 辅助
    /// <summary>校验连接和命令有效性</summary>
    private void EnsureValid()
    {
        if (_connection == null) throw new InvalidOperationException("连接未设置");
        if (_commands.Count == 0) throw new InvalidOperationException("没有命令需要执行");
    }

    /// <summary>将多条命令拼接为分号分隔的多语句 SQL</summary>
    /// <returns>合并后的 SQL</returns>
    private String BuildCombinedSql()
    {
        var sqls = new List<String>(_commands.Count);
        for (var i = 0; i < _commands.Count; i++)
        {
            var sql = _commands[i].CommandText;
            if (!String.IsNullOrEmpty(sql))
                sqls.Add(sql);
        }

        return String.Join(";", sqls);
    }
    #endregion
}
#endif

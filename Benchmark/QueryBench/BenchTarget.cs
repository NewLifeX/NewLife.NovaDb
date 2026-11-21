using System.Data.Common;

namespace NewLife.QueryBench;

/// <summary>数据库基准目标抽象</summary>
public abstract class BenchTarget : IDisposable
{
    /// <summary>目标名称</summary>
    public abstract String Name { get; }

    /// <summary>连接</summary>
    protected DbConnection Connection { get; set; } = null!;

    /// <summary>选项</summary>
    protected BenchOptions Options { get; private set; } = null!;

    /// <summary>数据根目录</summary>
    protected String DataRoot => Options.DataRoot;

    /// <summary>打开连接</summary>
    /// <param name="options"></param>
    /// <param name="scale"></param>
    public virtual void Open(BenchOptions options, String scale)
    {
        Options = options;
        Connection = CreateConnection(options, scale);
        Connection.Open();
    }

    /// <summary>创建连接</summary>
    /// <param name="options"></param>
    /// <param name="scale"></param>
    /// <returns></returns>
    protected abstract DbConnection CreateConnection(BenchOptions options, String scale);

    /// <summary>建表 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public abstract String CreateTableSql(String table);

    /// <summary>建索引 SQL 列表</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public abstract String[] CreateIndexSql(String table);

    /// <summary>统计信息 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public abstract String AnalyzeSql(String table);

    /// <summary>装载数据</summary>
    /// <param name="table"></param>
    /// <param name="count"></param>
    /// <param name="withUserDim"></param>
    public abstract void Load(String table, Int64 count, Boolean withUserDim);

    /// <summary>删除表</summary>
    /// <param name="table"></param>
    public abstract void Drop(String table);

    /// <summary>维度表建表 SQL</summary>
    public virtual String CreateUserTableSql => "CREATE TABLE t_user (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL)";

    /// <summary>重建表与索引</summary>
    /// <param name="table"></param>
    /// <param name="withUserDim"></param>
    public virtual void Recreate(String table, Boolean withUserDim)
    {
        Drop(table);
        if (withUserDim)
        {
            Drop("t_user");
            Execute(CreateUserTableSql);
        }

        Execute(CreateTableSql(table));
        foreach (var sql in CreateIndexSql(table)) Execute(sql);
    }

    /// <summary>收集统计信息</summary>
    /// <param name="table"></param>
    /// <param name="withUserDim"></param>
    public virtual void Analyze(String table, Boolean withUserDim)
    {
        Execute(AnalyzeSql(table));
        if (withUserDim) Execute(AnalyzeSql("t_user"));
    }

    /// <summary>执行非查询 SQL</summary>
    /// <param name="sql"></param>
    protected void Execute(String sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>异步执行查询并哈希结果</summary>
    /// <param name="sql"></param>
    /// <param name="parameters"></param>
    /// <param name="timeoutSeconds"></param>
    /// <param name="hasher"></param>
    /// <returns>结果行数</returns>
    public async Task<Int64> ExecuteAsync(String sql, IReadOnlyDictionary<String, Object?> parameters, Int32 timeoutSeconds, ResultHasher? hasher)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;

        foreach (var item in parameters)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = item.Key;
            parameter.Value = NormalizeValue(item.Value) ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await using var reader = await cmd.ExecuteReaderAsync(cts.Token).ConfigureAwait(false);

        var values = new Object[reader.FieldCount];
        Int64 rows = 0;
        while (await reader.ReadAsync(cts.Token).ConfigureAwait(false))
        {
            rows++;
            if (hasher != null)
            {
                reader.GetValues(values);
                hasher.Add(values);
            }
        }

        return rows;
    }

    /// <summary>参数值转换。文本时间库把 DateTime 转为 yyyy-MM-dd HH:mm:ss</summary>
    /// <param name="value"></param>
    /// <returns></returns>
    protected virtual Object? NormalizeValue(Object? value) => value;

    /// <summary>释放连接</summary>
    public void Dispose()
    {
        Connection?.Dispose();
        Connection = null!;
        GC.SuppressFinalize(this);
    }
}

/// <summary>创建数据库目标</summary>
public static class BenchTargetFactory
{
    /// <summary>按名称创建</summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static BenchTarget Create(String name) => name.ToLowerInvariant() switch
    {
        "nova" => new NovaTarget(),
        "sqlite" => new SqliteTarget(),
        "mysql" => new MySqlTarget(),
        _ => throw new ArgumentException($"未知数据库：{name}"),
    };
}

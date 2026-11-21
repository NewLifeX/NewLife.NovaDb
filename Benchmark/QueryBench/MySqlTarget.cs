using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using MySqlConnector;

namespace NewLife.QueryBench;

/// <summary>MySQL 目标。优先 MySqlBulkCopy，失败回退多行 INSERT</summary>
public sealed class MySqlTarget : BenchTarget
{
    /// <summary>名称</summary>
    public override String Name => "mysql";

    /// <summary>每批行数</summary>
    private const Int32 BatchSize = 500;

    /// <summary>实际使用的数据库</summary>
    private String _database = "querybench";

    /// <summary>服务器禁用了 local_infile 时跳过后续重试</summary>
    private Boolean _bulkUnavailable;

    /// <summary>打开连接</summary>
    /// <param name="options"></param>
    /// <param name="scale"></param>
    /// <returns></returns>
    protected override DbConnection CreateConnection(BenchOptions options, String scale)
    {
        var connectionString = MySqlConfig.Resolve(options);
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (String.IsNullOrEmpty(builder.Server) || builder.Server == "." || builder.Server == "(local)")
            builder.Server = "localhost";
        builder.AllowLoadLocalInfile = true;
        builder.AllowUserVariables = true;
        if (builder.ConnectionTimeout == 0 || builder.ConnectionTimeout > 30) builder.ConnectionTimeout = 10;
        builder.DefaultCommandTimeout = (UInt32)options.TimeoutSeconds;

        var original = builder.Database;
        var database = "querybench";
        try
        {
            var adminBuilder = new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" };
            using var admin = new MySqlConnection(adminBuilder.ConnectionString);
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = "CREATE DATABASE IF NOT EXISTS `querybench` CHARACTER SET utf8mb4";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            database = String.IsNullOrEmpty(original) ? "sys" : original;
            BenchLog.Write($"[mysql] 无法创建 querybench 库，改用 {database}：{BenchLog.Summarize(ex)}");
        }

        builder.Database = database;
        _database = database;

        BenchLog.Write($"[mysql] 连接 Server={builder.Server};Port={builder.Port};Database={builder.Database};User={builder.UserID}");

        return new MySqlConnection(builder.ConnectionString);
    }

    /// <summary>建表 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String CreateTableSql(String table) =>
        $"CREATE TABLE IF NOT EXISTS `{table}` (id INT PRIMARY KEY, user_id INT NOT NULL, status INT NOT NULL, code VARCHAR(32) NOT NULL, amount DOUBLE NOT NULL, created_at DATETIME NOT NULL, remark VARCHAR(200)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";

    /// <summary>维度表建表 SQL</summary>
    public override String CreateUserTableSql =>
        "CREATE TABLE IF NOT EXISTS `t_user` (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";

    /// <summary>建索引 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String[] CreateIndexSql(String table) => table switch
    {
        "t_singleidx" => [$"CREATE INDEX idx_user ON `{table}`(user_id)", $"CREATE UNIQUE INDEX uq_code ON `{table}`(code)"],
        "t_composite" => [$"CREATE INDEX idx_status_created ON `{table}`(status, created_at)", $"CREATE INDEX idx_user ON `{table}`(user_id)"],
        _ => [],
    };

    /// <summary>统计信息 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String AnalyzeSql(String table) => $"ANALYZE TABLE `{table}`";

    /// <summary>删除表</summary>
    /// <param name="table"></param>
    public override void Drop(String table) => Execute($"DROP TABLE IF EXISTS `{table}`");

    /// <summary>装载数据</summary>
    /// <param name="table"></param>
    /// <param name="count"></param>
    /// <param name="withUserDim"></param>
    public override void Load(String table, Int64 count, Boolean withUserDim)
    {
        if (!_bulkUnavailable)
        {
            try
            {
                BulkLoad(table, count);
                if (withUserDim) BatchInsertUsers(count);
                return;
            }
            catch (Exception ex)
            {
                _bulkUnavailable = true;
                BenchLog.Write($"[mysql] MySqlBulkCopy 不可用（{BenchLog.Summarize(ex)}），本次运行回退批量 INSERT");
                Recreate(table, withUserDim);
            }
        }

        BatchInsert(table, count, withUserDim);
    }

    private void BulkLoad(String table, Int64 count)
    {
        var data = new DataTable();
        data.Columns.Add("id", typeof(Int32));
        data.Columns.Add("user_id", typeof(Int32));
        data.Columns.Add("status", typeof(Int32));
        data.Columns.Add("code", typeof(String));
        data.Columns.Add("amount", typeof(Double));
        data.Columns.Add("created_at", typeof(DateTime));
        data.Columns.Add("remark", typeof(String));

        var bulk = new MySqlBulkCopy((MySqlConnection)Connection) { DestinationTableName = table };
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(0, "id"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(1, "user_id"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(2, "status"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(3, "code"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(4, "amount"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(5, "created_at"));
        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(6, "remark"));

        for (Int64 i = 1; i <= count; i++)
        {
            var row = DataGenerator.Create(i, count);
            data.Rows.Add(row.Id, row.UserId, row.Status, row.Code, row.Amount, row.CreatedAt, row.Remark);

            if (i % BatchSize == 0 || i == count)
            {
                bulk.WriteToServer(data);
                data.Rows.Clear();
            }
        }
    }

    private void BatchInsert(String table, Int64 count, Boolean withUserDim)
    {
        using var tx = Connection.BeginTransaction();

        var builder = new StringBuilder(512 * 1024);
        for (Int64 i = 1; i <= count; i++)
        {
            var row = DataGenerator.Create(i, count);
            if (builder.Length == 0)
                builder.Append("INSERT INTO `").Append(table).Append("` (id,user_id,status,code,amount,created_at,remark) VALUES ");
            else
                builder.Append(',');

            builder.Append('(')
                .Append(row.Id).Append(',')
                .Append(row.UserId).Append(',')
                .Append(row.Status).Append(',')
                .Append(DataGenerator.ToSqlLiteral(row.Code)).Append(',')
                .Append(row.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                .Append(DataGenerator.ToSqlDateTime(row.CreatedAt)).Append(',')
                .Append(DataGenerator.ToSqlLiteral(row.Remark))
                .Append(')');

            if (i % BatchSize == 0 || i == count)
            {
                ExecuteInTransaction(builder.ToString(), tx);
                builder.Clear();
            }
        }

        tx.Commit();

        if (withUserDim) BatchInsertUsers(count);
    }

    private void BatchInsertUsers(Int64 count)
    {
        using var tx = Connection.BeginTransaction();

        var builder = new StringBuilder(4096);
        var maxUser = DataGenerator.MaxUserId(count);
        for (var i = 1; i <= maxUser; i++)
        {
            if (builder.Length == 0)
                builder.Append("INSERT INTO `t_user` (id,name) VALUES ");
            else
                builder.Append(',');

            builder.Append('(').Append(i).Append(',').Append(DataGenerator.ToSqlLiteral($"u{i}")).Append(')');

            if (i % BatchSize == 0 || i == maxUser)
            {
                ExecuteInTransaction(builder.ToString(), tx);
                builder.Clear();
            }
        }

        tx.Commit();
    }

    private void ExecuteInTransaction(String sql, DbTransaction tx)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>实际使用的数据库名</summary>
    public override String ToString() => $"{Name}({_database})";
}

/// <summary>MySQL 连接配置解析。优先级：--mysql &gt; QUERYBENCH_MYSQL &gt; 配置文件</summary>
public static class MySqlConfig
{
    /// <summary>解析连接串</summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static String Resolve(BenchOptions options)
    {
        if (!String.IsNullOrWhiteSpace(options.MySqlConnectionString)) return options.MySqlConnectionString.Trim();

        var environment = Environment.GetEnvironmentVariable("QUERYBENCH_MYSQL");
        if (!String.IsNullOrWhiteSpace(environment)) return environment.Trim();

        var file = options.ResolveInputPath(options.MySqlFile);
        if (!File.Exists(file))
            throw new FileNotFoundException($"未找到 MySQL 连接配置：{file}。可用 --mysql 或环境变量 QUERYBENCH_MYSQL 指定连接串", file);

        var lines = File.ReadAllLines(file)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0 && !e.StartsWith('#'))
            .Select(e => e.TrimEnd(';'))
            .ToArray();

        if (lines.Length == 0) throw new InvalidDataException($"MySQL 连接配置为空：{file}");

        return String.Join(";", lines);
    }
}

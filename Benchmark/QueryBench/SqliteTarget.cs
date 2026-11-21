using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace NewLife.QueryBench;

/// <summary>SQLite 目标。created_at 以 yyyy-MM-dd HH:mm:ss 文本存储，字典序等于时间序</summary>
public sealed class SqliteTarget : BenchTarget
{
    /// <summary>名称</summary>
    public override String Name => "sqlite";

    /// <summary>创建连接</summary>
    /// <param name="options"></param>
    /// <param name="scale"></param>
    /// <returns></returns>
    protected override DbConnection CreateConnection(BenchOptions options, String scale)
    {
        var dir = Path.Combine(DataRoot, "sqlite");
        Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, $"{scale}.db"),
            Pooling = false,
            DefaultTimeout = options.TimeoutSeconds,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    /// <summary>建表 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String CreateTableSql(String table) =>
        $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, status INTEGER NOT NULL, code TEXT NOT NULL COLLATE NOCASE, amount REAL NOT NULL, created_at TEXT NOT NULL, remark TEXT COLLATE NOCASE)";

    /// <summary>建索引 SQL。SQLite 索引名在库内全局唯一，统一加表名后缀</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String[] CreateIndexSql(String table) => table switch
    {
        "t_singleidx" => [$"CREATE INDEX idx_user_{table} ON {table}(user_id)", $"CREATE UNIQUE INDEX uq_code_{table} ON {table}(code)"],
        "t_composite" => [$"CREATE INDEX idx_status_created_{table} ON {table}(status, created_at)", $"CREATE INDEX idx_user_{table} ON {table}(user_id)"],
        _ => [],
    };

    /// <summary>统计信息 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String AnalyzeSql(String table) => $"ANALYZE {table}";

    /// <summary>删除表</summary>
    /// <param name="table"></param>
    public override void Drop(String table) => Execute($"DROP TABLE IF EXISTS {table}");

    /// <summary>事务内预编译单行 INSERT</summary>
    /// <param name="table"></param>
    /// <param name="count"></param>
    /// <param name="withUserDim"></param>
    public override void Load(String table, Int64 count, Boolean withUserDim)
    {
        using var tx = Connection.BeginTransaction();

        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} (id,user_id,status,code,amount,created_at,remark) VALUES (@id,@user_id,@status,@code,@amount,@created_at,@remark)";
        var pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        var pUserId = cmd.CreateParameter();
        pUserId.ParameterName = "@user_id";
        var pStatus = cmd.CreateParameter();
        pStatus.ParameterName = "@status";
        var pCode = cmd.CreateParameter();
        pCode.ParameterName = "@code";
        var pAmount = cmd.CreateParameter();
        pAmount.ParameterName = "@amount";
        var pCreatedAt = cmd.CreateParameter();
        pCreatedAt.ParameterName = "@created_at";
        var pRemark = cmd.CreateParameter();
        pRemark.ParameterName = "@remark";
        cmd.Parameters.AddRange(new DbParameter[] { pId, pUserId, pStatus, pCode, pAmount, pCreatedAt, pRemark });

        for (Int64 i = 1; i <= count; i++)
        {
            var row = DataGenerator.Create(i, count);
            pId.Value = row.Id;
            pUserId.Value = row.UserId;
            pStatus.Value = row.Status;
            pCode.Value = row.Code;
            pAmount.Value = row.Amount;
            pCreatedAt.Value = DataGenerator.ToTextDateTime(row.CreatedAt);
            pRemark.Value = row.Remark;
            cmd.ExecuteNonQuery();
        }

        if (withUserDim)
        {
            using var userCmd = Connection.CreateCommand();
            userCmd.Transaction = tx;
            userCmd.CommandText = "INSERT INTO t_user (id,name) VALUES (@id,@name)";
            var uId = userCmd.CreateParameter();
            uId.ParameterName = "@id";
            var uName = userCmd.CreateParameter();
            uName.ParameterName = "@name";
            userCmd.Parameters.AddRange(new DbParameter[] { uId, uName });

            var maxUser = DataGenerator.MaxUserId(count);
            for (var i = 1; i <= maxUser; i++)
            {
                uId.Value = i;
                uName.Value = $"u{i}";
                userCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>时间参数转为文本</summary>
    /// <param name="value"></param>
    /// <returns></returns>
    protected override Object? NormalizeValue(Object? value) => value is DateTime time ? DataGenerator.ToTextDateTime(time) : value;
}

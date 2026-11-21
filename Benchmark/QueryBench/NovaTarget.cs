using System.Data.Common;
using System.Globalization;
using System.Text;
using NewLife.NovaDb.Client;

namespace NewLife.QueryBench;

/// <summary>NovaDb 嵌入模式目标</summary>
public sealed class NovaTarget : BenchTarget
{
    /// <summary>名称</summary>
    public override String Name => "nova";

    /// <summary>每批行数</summary>
    private const Int32 BatchSize = 500;

    /// <summary>创建连接</summary>
    /// <param name="options"></param>
    /// <param name="scale"></param>
    /// <returns></returns>
    protected override DbConnection CreateConnection(BenchOptions options, String scale)
    {
        var dir = Path.Combine(DataRoot, "nova", scale);
        Directory.CreateDirectory(dir);

        return new NovaConnection($"Data Source={dir}");
    }

    /// <summary>建表 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String CreateTableSql(String table) =>
        $"CREATE TABLE {table} (id INT PRIMARY KEY, user_id INT NOT NULL, status INT NOT NULL, code VARCHAR(32) NOT NULL, amount DOUBLE NOT NULL, created_at DATETIME NOT NULL, remark VARCHAR(200))";

    /// <summary>建索引 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String[] CreateIndexSql(String table) => table switch
    {
        "t_singleidx" => [$"CREATE INDEX idx_user ON {table}(user_id)", $"CREATE UNIQUE INDEX uq_code ON {table}(code)"],
        "t_composite" => [$"CREATE INDEX idx_status_created ON {table}(status, created_at)", $"CREATE INDEX idx_user ON {table}(user_id)"],
        _ => [],
    };

    /// <summary>统计信息 SQL</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public override String AnalyzeSql(String table) => $"ANALYZE TABLE {table}";

    /// <summary>删除表</summary>
    /// <param name="table"></param>
    public override void Drop(String table) => Execute($"DROP TABLE IF EXISTS {table}");

    /// <summary>批量多行 INSERT，单事务</summary>
    /// <param name="table"></param>
    /// <param name="count"></param>
    /// <param name="withUserDim"></param>
    public override void Load(String table, Int64 count, Boolean withUserDim)
    {
        using var tx = Connection.BeginTransaction();

        var builder = new StringBuilder(512 * 1024);
        for (Int64 i = 1; i <= count; i++)
        {
            var row = DataGenerator.Create(i, count);
            if (builder.Length == 0)
                builder.Append("INSERT INTO ").Append(table).Append(" (id,user_id,status,code,amount,created_at,remark) VALUES ");
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

        if (withUserDim) LoadUsers(count, tx);

        tx.Commit();
    }

    private void LoadUsers(Int64 count, DbTransaction tx)
    {
        var maxUser = DataGenerator.MaxUserId(count);
        var builder = new StringBuilder(4096);
        for (var i = 1; i <= maxUser; i++)
        {
            if (builder.Length == 0)
                builder.Append("INSERT INTO t_user (id,name) VALUES ");
            else
                builder.Append(',');

            builder.Append('(').Append(i).Append(',').Append(DataGenerator.ToSqlLiteral($"u{i}")).Append(')');

            if (i % BatchSize == 0 || i == maxUser)
            {
                ExecuteInTransaction(builder.ToString(), tx);
                builder.Clear();
            }
        }
    }

    private void ExecuteInTransaction(String sql, DbTransaction tx)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}

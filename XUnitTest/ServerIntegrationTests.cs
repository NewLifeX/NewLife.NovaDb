using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest;

/// <summary>网络模式集成测试，通过 ADO.NET 连接 NovaServer 完成完整数据库操作</summary>
[Collection("IntegrationTests")]
public class ServerIntegrationTests : IClassFixture<IntegrationServerFixture>
{
    private readonly IntegrationServerFixture _fixture;
    private Int32 _port => _fixture.Port;

    public ServerIntegrationTests(IntegrationServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>创建并打开网络模式连接</summary>
    private NovaConnection CreateConnection()
    {
        var conn = new NovaConnection { ConnectionString = $"Server=127.0.0.1;Port={_port}" };
        conn.Open();
        return conn;
    }

    #region DDL 测试

    [Fact(DisplayName = "网络模式-创建表")]
    public void CreateTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_ddl_create (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT, score DOUBLE, active BOOLEAN, created DATETIME)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "网络模式-创建表IF NOT EXISTS")]
    public void CreateTableIfNotExists()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_ddl_ifne (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE TABLE IF NOT EXISTS srv_ddl_ifne (id INT PRIMARY KEY, name VARCHAR)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "网络模式-删除表")]
    public void DropTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_ddl_drop (id INT PRIMARY KEY)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DROP TABLE srv_ddl_drop";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "网络模式-删除表IF EXISTS")]
    public void DropTableIfExists()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "DROP TABLE IF EXISTS srv_ddl_noexist";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "网络模式-创建索引")]
    public void CreateIndex()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_ddl_idx (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX idx_srv_name ON srv_ddl_idx (name)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "网络模式-删除索引")]
    public void DropIndex()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_ddl_dropidx (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX idx_srv_dropname ON srv_ddl_dropidx (name)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DROP INDEX idx_srv_dropname ON srv_ddl_dropidx";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    #endregion

    #region DML 测试

    [Fact(DisplayName = "网络模式-插入单行")]
    public void InsertSingleRow()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_dml_ins1 (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_dml_ins1 VALUES (1, 'Alice', 25)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    [Fact(DisplayName = "网络模式-插入多行")]
    public void InsertMultipleRows()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_dml_ins2 (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_dml_ins2 VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(3, rows);
    }

    [Fact(DisplayName = "网络模式-UPDATE带WHERE")]
    public void UpdateWithWhere()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_dml_upd (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_dml_upd VALUES (1, 'Alice', 25), (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "UPDATE srv_dml_upd SET name = 'Alice Smith', age = 26 WHERE id = 1";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 验证更新结果
        cmd.CommandText = "SELECT name FROM srv_dml_upd WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alice Smith", Convert.ToString(reader.GetValue(0)));
    }

    [Fact(DisplayName = "网络模式-DELETE带WHERE")]
    public void DeleteWithWhere()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_dml_del (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_dml_del VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM srv_dml_del WHERE age >= 30";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(2, rows);
    }

    [Fact(DisplayName = "网络模式-带列名插入")]
    public void InsertWithColumnList()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_dml_inscol (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_dml_inscol (id, name) VALUES (1, 'Alice')";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    #endregion

    #region 查询测试

    [Fact(DisplayName = "网络模式-SELECT全部")]
    public void SelectAll()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_all (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_all VALUES (1, 'Alice', 25), (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM srv_qry_all";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);

        var rowCount = 0;
        while (reader.Read()) rowCount++;
        Assert.Equal(2, rowCount);
    }

    [Fact(DisplayName = "网络模式-WHERE比较运算符")]
    public void SelectWithComparisonOperators()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_cmp (id INT PRIMARY KEY, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_cmp VALUES (1, 60), (2, 70), (3, 80), (4, 90), (5, 100)";
        cmd.ExecuteNonQuery();

        // 等于
        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_cmp WHERE score = 80";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));

        // 大于
        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_cmp WHERE score > 80";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        // 小于等于
        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_cmp WHERE score <= 80";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "网络模式-WHERE AND/OR")]
    public void SelectWithAndOr()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_logic (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_logic VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_logic WHERE age > 20 AND age < 32";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_logic WHERE name = 'Alice' OR name = 'Charlie'";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "网络模式-WHERE LIKE")]
    public void SelectWithLike()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_like (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_like VALUES (1, 'Alice'), (2, 'Albert'), (3, 'Bob')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_like WHERE name LIKE 'Al%'";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "网络模式-ORDER BY")]
    public void SelectWithOrderBy()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_order (id INT PRIMARY KEY, name VARCHAR, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_order VALUES (1, 'Alice', 90), (2, 'Bob', 70), (3, 'Charlie', 80)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name FROM srv_qry_order ORDER BY score ASC";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Bob", Convert.ToString(reader.GetValue(0)));
        Assert.True(reader.Read());
        Assert.Equal("Charlie", Convert.ToString(reader.GetValue(0)));
        Assert.True(reader.Read());
        Assert.Equal("Alice", Convert.ToString(reader.GetValue(0)));
    }

    [Fact(DisplayName = "网络模式-GROUP BY聚合")]
    public void SelectWithGroupBy()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_grp (id INT PRIMARY KEY, dept VARCHAR, salary INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_grp VALUES (1, 'HR', 5000), (2, 'HR', 6000), (3, 'IT', 7000), (4, 'IT', 8000), (5, 'IT', 9000)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT dept, COUNT(*) AS cnt, SUM(salary) AS total FROM srv_qry_grp GROUP BY dept ORDER BY dept ASC";
        using var reader = cmd.ExecuteReader();

        // HR
        Assert.True(reader.Read());
        Assert.Equal("HR", Convert.ToString(reader.GetValue(0)));
        Assert.Equal(2, Convert.ToInt32(reader["cnt"]));
        Assert.Equal(11000, Convert.ToInt32(reader["total"]));

        // IT
        Assert.True(reader.Read());
        Assert.Equal("IT", Convert.ToString(reader.GetValue(0)));
        Assert.Equal(3, Convert.ToInt32(reader["cnt"]));
        Assert.Equal(24000, Convert.ToInt32(reader["total"]));
    }

    [Fact(DisplayName = "网络模式-HAVING")]
    public void SelectWithHaving()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_hav (id INT PRIMARY KEY, dept VARCHAR, salary INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_hav VALUES (1, 'HR', 5000), (2, 'HR', 6000), (3, 'IT', 7000), (4, 'IT', 8000), (5, 'IT', 9000)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT dept, COUNT(*) AS cnt FROM srv_qry_hav GROUP BY dept HAVING COUNT(*) > 2";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("IT", Convert.ToString(reader.GetValue(0)));
        Assert.Equal(3, Convert.ToInt32(reader["cnt"]));
        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "网络模式-LIMIT和OFFSET")]
    public void SelectWithLimitOffset()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_lim (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_lim VALUES (1, 'A'), (2, 'B'), (3, 'C'), (4, 'D'), (5, 'E')";
        cmd.ExecuteNonQuery();

        // LIMIT
        cmd.CommandText = "SELECT name FROM srv_qry_lim ORDER BY id ASC LIMIT 3";
        using (var reader = cmd.ExecuteReader())
        {
            var count = 0;
            while (reader.Read()) count++;
            Assert.Equal(3, count);
        }

        // LIMIT + OFFSET
        cmd.CommandText = "SELECT name FROM srv_qry_lim ORDER BY id ASC LIMIT 2 OFFSET 2";
        using (var reader2 = cmd.ExecuteReader())
        {
            Assert.True(reader2.Read());
            Assert.Equal("C", Convert.ToString(reader2.GetValue(0)));
            Assert.True(reader2.Read());
            Assert.Equal("D", Convert.ToString(reader2.GetValue(0)));
            Assert.False(reader2.Read());
        }
    }

    [Fact(DisplayName = "网络模式-聚合查询")]
    public void AggregationQueries()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_qry_agg (id INT PRIMARY KEY, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_qry_agg VALUES (1, 60), (2, 70), (3, 80), (4, 90), (5, 100)";
        cmd.ExecuteNonQuery();

        // COUNT
        cmd.CommandText = "SELECT COUNT(*) FROM srv_qry_agg";
        Assert.Equal(5, Convert.ToInt32(cmd.ExecuteScalar()));

        // SUM
        cmd.CommandText = "SELECT SUM(score) FROM srv_qry_agg";
        Assert.Equal(400, Convert.ToInt32(cmd.ExecuteScalar()));

        // MIN
        cmd.CommandText = "SELECT MIN(score) FROM srv_qry_agg";
        Assert.Equal(60, Convert.ToInt32(cmd.ExecuteScalar()));

        // MAX
        cmd.CommandText = "SELECT MAX(score) FROM srv_qry_agg";
        Assert.Equal(100, Convert.ToInt32(cmd.ExecuteScalar()));

        // AVG
        cmd.CommandText = "SELECT AVG(score) FROM srv_qry_agg";
        Assert.Equal(80, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    #endregion

    #region SQL 函数测试

    [Fact(DisplayName = "网络模式-字符串函数")]
    public void StringFunctions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_fn_str (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_fn_str VALUES (1, 'Hello World')";
        cmd.ExecuteNonQuery();

        // UPPER
        cmd.CommandText = "SELECT UPPER(name) FROM srv_fn_str WHERE id = 1";
        Assert.Equal("HELLO WORLD", Convert.ToString(cmd.ExecuteScalar()));

        // LOWER
        cmd.CommandText = "SELECT LOWER(name) FROM srv_fn_str WHERE id = 1";
        Assert.Equal("hello world", Convert.ToString(cmd.ExecuteScalar()));

        // LENGTH
        cmd.CommandText = "SELECT LENGTH(name) FROM srv_fn_str WHERE id = 1";
        Assert.Equal(11, Convert.ToInt32(cmd.ExecuteScalar()));

        // SUBSTRING
        cmd.CommandText = "SELECT SUBSTRING(name, 1, 5) FROM srv_fn_str WHERE id = 1";
        Assert.Equal("Hello", Convert.ToString(cmd.ExecuteScalar()));

        // CONCAT
        cmd.CommandText = "SELECT CONCAT(name, '!') FROM srv_fn_str WHERE id = 1";
        Assert.Equal("Hello World!", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "网络模式-数值函数")]
    public void NumericFunctions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_fn_num (id INT PRIMARY KEY, val DOUBLE)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_fn_num VALUES (1, -3.7)";
        cmd.ExecuteNonQuery();

        // ABS
        cmd.CommandText = "SELECT ABS(val) FROM srv_fn_num WHERE id = 1";
        Assert.Equal(3.7, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // ROUND
        cmd.CommandText = "SELECT ROUND(val, 0) FROM srv_fn_num WHERE id = 1";
        Assert.Equal(-4.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // CEILING
        cmd.CommandText = "SELECT CEILING(val) FROM srv_fn_num WHERE id = 1";
        Assert.Equal(-3.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // FLOOR
        cmd.CommandText = "SELECT FLOOR(val) FROM srv_fn_num WHERE id = 1";
        Assert.Equal(-4.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);
    }

    [Fact(DisplayName = "网络模式-CASE WHEN")]
    public void CaseWhen()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_fn_case (id INT PRIMARY KEY, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_fn_case VALUES (1, 90), (2, 60), (3, 45)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT CASE WHEN score >= 80 THEN 'A' WHEN score >= 60 THEN 'B' ELSE 'C' END AS grade FROM srv_fn_case ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("A", Convert.ToString(reader["grade"]));
        Assert.True(reader.Read());
        Assert.Equal("B", Convert.ToString(reader["grade"]));
        Assert.True(reader.Read());
        Assert.Equal("C", Convert.ToString(reader["grade"]));
    }

    [Fact(DisplayName = "网络模式-COALESCE")]
    public void Coalesce()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE srv_fn_coal (id INT PRIMARY KEY, name VARCHAR, nickname VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO srv_fn_coal (id, name) VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COALESCE(nickname, name) FROM srv_fn_coal WHERE id = 1";
        Assert.Equal("Alice", Convert.ToString(cmd.ExecuteScalar()));
    }

    #endregion

    #region 事务测试

    [Fact(DisplayName = "网络模式-事务提交")]
    public void TransactionCommit()
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();

        Assert.NotNull(tx);
        Assert.IsType<NovaTransaction>(tx);

        var novaTx = (NovaTransaction)tx;
        Assert.False(novaTx.IsCompleted);
        Assert.NotNull(novaTx.TxId);
        Assert.NotEmpty(novaTx.TxId);

        tx.Commit();
        Assert.True(novaTx.IsCompleted);
    }

    [Fact(DisplayName = "网络模式-事务回滚")]
    public void TransactionRollback()
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();

        var novaTx = (NovaTransaction)tx;
        Assert.False(novaTx.IsCompleted);

        tx.Rollback();
        Assert.True(novaTx.IsCompleted);
    }

    #endregion

    #region 系统表测试

    [Fact(DisplayName = "网络模式-查询系统表tables")]
    public void SelectSysTables()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        // 先确保有一张表
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS srv_sys_t1 (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM _sys.tables";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.FieldCount > 0);

        var found = false;
        while (reader.Read())
        {
            var tableName = Convert.ToString(reader["name"]);
            if (tableName == "srv_sys_t1") found = true;
        }
        Assert.True(found);
    }

    [Fact(DisplayName = "网络模式-查询系统表version")]
    public void SelectSysVersion()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT * FROM _sys.version";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.Read());

        var version = Convert.ToString(reader["version"]);
        Assert.False(String.IsNullOrEmpty(version));
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest;

/// <summary>关系型引擎集成测试夹具。维护固定测试目录，每次测试启动前清空</summary>
public sealed class RelationalDbFixture
{
    /// <summary>固定测试目录，位于项目根目录 TestData/RelationalDb/，方便人工检查</summary>
    public static readonly String DbPath = "../TestData/RelationalDb/".GetFullPath();

    /// <summary>嵌入式连接字符串</summary>
    public String ConnectionString => $"Data Source={DbPath}";

    public RelationalDbFixture()
    {
        // 每次测试启动前清空目录，保留目录本身供人工检查
        if (Directory.Exists(DbPath))
            Directory.Delete(DbPath, recursive: true);
        Directory.CreateDirectory(DbPath);
    }
}

/// <summary>关系型引擎嵌入模式集成测试，覆盖 DDL/DML/查询/事务及数据文件验证</summary>
/// <remarks>
/// 测试数据保存到 TestData/RelationalDb/ 目录（项目根目录），每次运行前清空，
/// 测试后数据留存，可人工检查数据库目录内文件的正确性。
/// 每个测试方法使用独立命名的表，互不干扰。
/// </remarks>
public class RelationalIntegrationTests : IClassFixture<RelationalDbFixture>
{
    private readonly RelationalDbFixture _fixture;

    public RelationalIntegrationTests(RelationalDbFixture fixture)
    {
        _fixture = fixture;
    }

    #region 辅助方法

    private NovaConnection OpenConnection(String walMode = "Full")
    {
        var conn = new NovaConnection { ConnectionString = $"{_fixture.ConnectionString};WalMode={walMode}" };
        conn.Open();
        return conn;
    }

    private String DataFile(String tableName) => Path.Combine(RelationalDbFixture.DbPath, $"{tableName}.data");
    private String WalFile(String tableName) => Path.Combine(RelationalDbFixture.DbPath, $"{tableName}.wal");
    private String IdxFile(String tableName) => Path.Combine(RelationalDbFixture.DbPath, $"{tableName}.idx");

    #endregion

    #region DDL 测试

    [Fact(DisplayName = "关系型集成-建表验证数据文件")]
    public void DDL_CreateTable_CreatesDataFile()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_ddl_create (id INT PRIMARY KEY, name STRING(50) NOT NULL, age INT, score DOUBLE, active BOOLEAN, created DATETIME)");

        // 验证 nova.db 元数据文件已创建
        var metaFile = Path.Combine(RelationalDbFixture.DbPath, "nova.db");
        Assert.True(File.Exists(metaFile), "nova.db 元数据文件应在首次建表后创建");

        // 验证 .data 文件已创建
        var dataFile = DataFile("rel_ddl_create");
        Assert.True(File.Exists(dataFile), ".data 文件应在建表后存在");

        // 验证文件头版本、类型、页大小
        var header = FileHeader.Read(dataFile);
        Assert.Equal((Byte)1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
        Assert.True(header.CreateTime.Year >= 2020);

        // 通过 _sys.tables 验证表定义已持久化
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM _sys.tables WHERE name = 'rel_ddl_create'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "系统表中应包含新建的表");
        Assert.Equal(6, Convert.ToInt32(reader["column_count"]));
        Assert.Equal("id", Convert.ToString(reader["primary_key"]));
    }

    [Fact(DisplayName = "关系型集成-删表后数据文件被清除")]
    public void DDL_DropTable_DeletesDataFile()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_ddl_drop (id INT PRIMARY KEY, name STRING(50))");

        var dataFile = DataFile("rel_ddl_drop");
        Assert.True(File.Exists(dataFile), "建表后 .data 文件应存在");

        conn.ExecuteNonQuery("DROP TABLE rel_ddl_drop");

        Assert.False(File.Exists(dataFile), "删表后 .data 文件应被删除");
        Assert.False(File.Exists(WalFile("rel_ddl_drop")), "删表后 .wal 文件应被删除");
        Assert.False(File.Exists(IdxFile("rel_ddl_drop")), "删表后 .idx 文件应被删除");

        // nova.db 元数据文件应仍然存在
        var metaFile = Path.Combine(RelationalDbFixture.DbPath, "nova.db");
        Assert.True(File.Exists(metaFile), "nova.db 元数据文件应在删表后保留");

        // 系统表中不应再包含该表
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM _sys.tables WHERE name = 'rel_ddl_drop'";
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "关系型集成-CREATE TABLE IF NOT EXISTS")]
    public void DDL_CreateTableIfNotExists()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_ddl_ifne (id INT PRIMARY KEY, name STRING(50))");

        // 再次创建不应报错
        var rows = conn.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS rel_ddl_ifne (id INT PRIMARY KEY, name STRING(50))");
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "关系型集成-ALTER TABLE ADD COLUMN")]
    public void DDL_AlterTable_AddColumn()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_ddl_alter (id INT PRIMARY KEY, name STRING(50))");

        conn.ExecuteNonQuery("ALTER TABLE rel_ddl_alter ADD COLUMN email STRING(100)");

        // 通过系统表验证新列存在
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_count FROM _sys.tables WHERE name = 'rel_ddl_alter'";
        var colCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(3, colCount);

        // 验证可向新列插入数据
        conn.ExecuteNonQuery("INSERT INTO rel_ddl_alter (id, name, email) VALUES (1, 'Alice', 'alice@example.com')");
        cmd.CommandText = "SELECT email FROM rel_ddl_alter WHERE id = 1";
        var email = Convert.ToString(cmd.ExecuteScalar());
        Assert.Equal("alice@example.com", email);
    }

    #endregion

    #region DML 测试

    [Fact(DisplayName = "关系型集成-INSERT后WAL文件增长")]
    public void DML_Insert_WalFileGrows()
    {
        using var conn = OpenConnection("Full");
        conn.ExecuteNonQuery("CREATE TABLE rel_dml_ins (id INT PRIMARY KEY, name STRING(50), age INT)");

        var walFile = WalFile("rel_dml_ins");
        var sizeBefore = File.Exists(walFile) ? new FileInfo(walFile).Length : 0L;

        var rows = conn.ExecuteNonQuery("INSERT INTO rel_dml_ins VALUES (1, 'Alice', 25)");
        Assert.Equal(1, rows);

        Assert.True(File.Exists(walFile), "INSERT 后 .wal 文件应存在");
        Assert.True(new FileInfo(walFile).Length > sizeBefore, "INSERT 后 .wal 文件大小应增长");
    }

    [Fact(DisplayName = "关系型集成-INSERT批量并SELECT验证")]
    public void DML_InsertBatch_And_Select()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_dml_ins2 (id INT PRIMARY KEY, name STRING(50), age INT)");

        var rows = conn.ExecuteNonQuery("INSERT INTO rel_dml_ins2 VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)");
        Assert.Equal(3, rows);

        // 验证三行都已写入
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rel_dml_ins2";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));

        // 验证数据内容正确
        cmd.CommandText = "SELECT name FROM rel_dml_ins2 WHERE id = 2";
        Assert.Equal("Bob", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "关系型集成-UPDATE后WAL增长且数据已更新")]
    public void DML_Update_WalGrows_DataUpdated()
    {
        using var conn = OpenConnection("Full");
        conn.ExecuteNonQuery("CREATE TABLE rel_dml_upd (id INT PRIMARY KEY, name STRING(50), age INT)");
        conn.ExecuteNonQuery("INSERT INTO rel_dml_upd VALUES (1, 'Alice', 25), (2, 'Bob', 30)");

        var walFile = WalFile("rel_dml_upd");
        var walSizeBefore = new FileInfo(walFile).Length;

        var rows = conn.ExecuteNonQuery("UPDATE rel_dml_upd SET name = 'Alice Smith', age = 26 WHERE id = 1");
        Assert.Equal(1, rows);

        Assert.True(new FileInfo(walFile).Length > walSizeBefore, "UPDATE 后 .wal 文件应增长");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, age FROM rel_dml_upd WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alice Smith", reader.GetString(0));
        Assert.Equal(26, reader.GetInt32(1));
    }

    [Fact(DisplayName = "关系型集成-DELETE后WAL增长且行消失")]
    public void DML_Delete_WalGrows_RowDisappears()
    {
        using var conn = OpenConnection("Full");
        conn.ExecuteNonQuery("CREATE TABLE rel_dml_del (id INT PRIMARY KEY, name STRING(50), age INT)");
        conn.ExecuteNonQuery("INSERT INTO rel_dml_del VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)");

        var walFile = WalFile("rel_dml_del");
        var walSizeBefore = new FileInfo(walFile).Length;

        var rows = conn.ExecuteNonQuery("DELETE FROM rel_dml_del WHERE age >= 30");
        Assert.Equal(2, rows);

        Assert.True(new FileInfo(walFile).Length > walSizeBefore, "DELETE 后 .wal 文件应增长");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rel_dml_del";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    #endregion

    #region 查询测试

    [Fact(DisplayName = "关系型集成-SELECT WHERE ORDER BY LIMIT")]
    public void Query_WhereOrderByLimit()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_qry_sel (id INT PRIMARY KEY, name STRING(50), score DOUBLE)");
        // Alice score=60 不满足 score>80，Bob=78 不满足，只有 Charlie(95) 和 Dave(82) 通过 WHERE 过滤
        conn.ExecuteNonQuery("INSERT INTO rel_qry_sel VALUES (1, 'Alice', 60.0), (2, 'Bob', 78.0), (3, 'Charlie', 95.0), (4, 'Dave', 82.0)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, score FROM rel_qry_sel WHERE score > 80 ORDER BY score DESC LIMIT 2";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read(), "第 1 行应为 Charlie（95.0 最高）");
        Assert.Equal("Charlie", reader.GetString(1));
        Assert.Equal(95.0, reader.GetDouble(2));

        Assert.True(reader.Read(), "第 2 行应为 Dave（82.0 次高）");
        Assert.Equal("Dave", reader.GetString(1));
        Assert.False(reader.Read(), "LIMIT 2 应只返回 2 行");
    }

    [Fact(DisplayName = "关系型集成-SELECT 参数化查询")]
    public void Query_ParameterizedQuery()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_qry_param (id INT PRIMARY KEY, name STRING(50), age INT)");
        conn.ExecuteNonQuery("INSERT INTO rel_qry_param VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 22)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM rel_qry_param WHERE age > @minAge ORDER BY age";
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@minAge", Value = 24 });
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("Bob", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "关系型集成-GROUP BY COUNT 聚合")]
    public void Query_GroupByCount()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_qry_grp (id INT PRIMARY KEY, dept STRING(30), salary DOUBLE)");
        conn.ExecuteNonQuery("INSERT INTO rel_qry_grp VALUES (1, 'IT', 10000), (2, 'IT', 12000), (3, 'HR', 9000), (4, 'HR', 9500)");

        using var cmd = conn.CreateCommand();
        // GROUP BY 不保证排序顺序，逐行读取后按 dept 名称断言
        cmd.CommandText = "SELECT dept, COUNT(*) as cnt FROM rel_qry_grp GROUP BY dept";
        using var reader = cmd.ExecuteReader();

        var groupResult = new Dictionary<String, Int32>();
        while (reader.Read())
            groupResult[reader.GetString(0)] = Convert.ToInt32(reader[1]);

        Assert.Equal(2, groupResult.Count);
        Assert.True(groupResult.ContainsKey("HR"), "应包含 HR 组");
        Assert.True(groupResult.ContainsKey("IT"), "应包含 IT 组");
        Assert.Equal(2, groupResult["HR"]);
        Assert.Equal(2, groupResult["IT"]);
    }

    #endregion

    #region 事务测试

    [Fact(DisplayName = "关系型集成-事务提交后数据可见")]
    public void Transaction_Commit_DataVisible()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_tx_commit (id INT PRIMARY KEY, val STRING(50))");

        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rel_tx_commit VALUES (1, 'committed')";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        // 提交后数据应可查询
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT val FROM rel_tx_commit WHERE id = 1";
        Assert.Equal("committed", Convert.ToString(cmd2.ExecuteScalar()));
    }

    [Fact(Skip = "嵌入模式 NovaTransaction.Rollback 尚未实现写撤销，嵌入事务暂不支持", DisplayName = "关系型集成-事务回滚后数据不可见")]
    public void Transaction_Rollback_DataNotVisible()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_tx_rollback (id INT PRIMARY KEY, val STRING(50))");

        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rel_tx_rollback VALUES (1, 'rolled_back')";
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }

        // 回滚后数据应不存在
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM rel_tx_rollback";
        Assert.Equal(0, Convert.ToInt32(cmd2.ExecuteScalar()));
    }

    [Fact(DisplayName = "关系型集成-事务内多行操作全部提交")]
    public void Transaction_MultipleInserts_AllCommitted()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE rel_tx_multi (id INT PRIMARY KEY, name STRING(50))");

        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rel_tx_multi VALUES (1, 'A')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO rel_tx_multi VALUES (2, 'B')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO rel_tx_multi VALUES (3, 'C')";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM rel_tx_multi";
        Assert.Equal(3, Convert.ToInt32(cmd2.ExecuteScalar()));
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Client;
using Xunit;

#nullable enable

namespace XUnitTest;

/// <summary>MySQL 方言兼容测试夹具。独立目录，避免与其他测试类并行争抢同一批文件</summary>
public sealed class MySqlCompatibilityFixture
{
    /// <summary>固定测试目录，位于测试输出目录 TestData/MySqlCompat/（按目标框架隔离）</summary>
    public static readonly String DbPath = TestDataHelper.GetPath("MySqlCompat");

    /// <summary>嵌入式连接字符串</summary>
    public String ConnectionString => $"Data Source={DbPath}";

    public MySqlCompatibilityFixture()
    {
        if (Directory.Exists(DbPath))
            Directory.Delete(DbPath, recursive: true);
        Directory.CreateDirectory(DbPath);
    }
}

/// <summary>MySQL 方言兼容测试：分页、限定符、类型与比较、转义、元数据、DDL 改名</summary>
public class MySqlCompatibilityTests : IClassFixture<MySqlCompatibilityFixture>
{
    private readonly MySqlCompatibilityFixture _fixture;

    public MySqlCompatibilityTests(MySqlCompatibilityFixture fixture) => _fixture = fixture;

    #region 辅助方法

    private NovaConnection OpenConnection()
    {
        var conn = new NovaConnection { ConnectionString = $"{_fixture.ConnectionString};WalMode=Full" };
        conn.Open();
        return conn;
    }

    private static Object? Scalar(NovaConnection conn, String sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static List<Object?[]> Query(NovaConnection conn, String sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var rows = new List<Object?[]>();
        while (reader.Read())
        {
            var row = new Object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    private static String[] ColumnNames(NovaConnection conn, String sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var names = new String[reader.FieldCount];
        for (var i = 0; i < names.Length; i++) names[i] = reader.GetName(i);

        return names;
    }

    #endregion

    [Fact(DisplayName = "MySQL兼容-LIMIT 逗号分页")]
    public void Limit_CommaPagination()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_limit (id INT PRIMARY KEY, name STRING(20))");
        conn.ExecuteNonQuery("INSERT INTO mysql_limit VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd'), (5, 'e')");

        // LIMIT offset,count 等价于 LIMIT count OFFSET offset
        var rows = Query(conn, "SELECT id FROM mysql_limit ORDER BY id LIMIT 1, 3");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, Convert.ToInt32(rows[0][0]));
        Assert.Equal(4, Convert.ToInt32(rows[2][0]));

        // 原有 LIMIT n OFFSET m 语义不变
        rows = Query(conn, "SELECT id FROM mysql_limit ORDER BY id LIMIT 3 OFFSET 1");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, Convert.ToInt32(rows[0][0]));
    }

    [Fact(DisplayName = "MySQL兼容-库名限定符与系统视图")]
    public void TableName_DatabaseQualifier()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_qual (id INT PRIMARY KEY)");
        conn.ExecuteNonQuery("INSERT INTO mysql_qual VALUES (1)");

        // 两段限定符忽略库名
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM `anydb`.`mysql_qual`")));
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM anydb.mysql_qual")));
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM .`mysql_qual`")));

        // 三段列引用同样丢弃库名
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT anydb.mysql_qual.id FROM mysql_qual")));

        // 系统视图前缀保持原样
        Assert.Single(Query(conn, "SELECT name FROM _sys.tables WHERE name = 'mysql_qual'"));
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM _sys.tables WHERE name = 'mysql_qual'")));

        // system view 支持 ORM 常用聚合
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'mysql_qual'")));
    }

    [Fact(DisplayName = "MySQL兼容-BIT 类型映射布尔")]
    public void BitType_MapsToBoolean()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_bit (id INT PRIMARY KEY, flag BIT)");
        conn.ExecuteNonQuery("INSERT INTO mysql_bit VALUES (1, 1), (2, 0)");

        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bit WHERE flag = 1")));
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bit WHERE flag = 0")));
        Assert.Equal(true, Scalar(conn, "SELECT flag FROM mysql_bit WHERE id = 1"));
    }

    [Fact(DisplayName = "MySQL兼容-布尔列与数字比较")]
    public void BoolColumn_ComparesWithNumber()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_bool (id INT PRIMARY KEY, active BOOLEAN)");
        conn.ExecuteNonQuery("INSERT INTO mysql_bool VALUES (1, TRUE), (2, FALSE), (3, '1'), (4, '0')");

        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bool WHERE active = 1")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bool WHERE active = 0")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bool WHERE active = '1'")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bool WHERE 1 = active")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_bool WHERE active")));

        // CAST 与 IF 同样接受 0/1 与 true/false 文本
        Assert.Equal(true, Scalar(conn, "SELECT CAST('1' AS BOOLEAN)"));
        Assert.Equal(false, Scalar(conn, "SELECT CAST(0 AS BOOL)"));
        Assert.Equal("yes", Scalar(conn, "SELECT IF(1, 'yes', 'no')"));
    }

    [Fact(DisplayName = "MySQL兼容-反斜杠转义")]
    public void StringLiteral_BackslashEscape()
    {
        using var conn = OpenConnection();

        // 标准转义序列还原
        Assert.Equal(@"C:\temp", Scalar(conn, @"SELECT 'C:\\temp'"));
        Assert.Equal("a'b", Scalar(conn, @"SELECT 'a\'b'"));
        Assert.Equal(@"a\tb", Scalar(conn, @"SELECT 'a\\tb'"));

        // 未知转义原样保留（保护 Windows 路径中的 \d、\p 等）
        Assert.Equal(@"C:\data\path", Scalar(conn, @"SELECT 'C:\data\path'"));

        // LIKE 中的 \% 按字面量匹配
        conn.ExecuteNonQuery("CREATE TABLE mysql_like (id INT PRIMARY KEY, name STRING(20))");
        conn.ExecuteNonQuery("INSERT INTO mysql_like VALUES (1, 'a%b'), (2, 'axb')");
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, @"SELECT COUNT(*) FROM mysql_like WHERE name LIKE 'a\%b'")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, @"SELECT COUNT(*) FROM mysql_like WHERE name LIKE 'a%b'")));
    }

    [Fact(DisplayName = "MySQL兼容-单引号列别名")]
    public void SelectColumn_StringAlias()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_alias (id INT PRIMARY KEY)");
        conn.ExecuteNonQuery("INSERT INTO mysql_alias VALUES (7)");

        var names = ColumnNames(conn, "SELECT id AS 'ident' FROM mysql_alias");
        Assert.Equal("ident", names[0]);
    }

    [Fact(DisplayName = "MySQL兼容-元数据 EXTRA 标记自增")]
    public void InformationSchema_ExtraAutoIncrement()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_meta (id INT PRIMARY KEY AUTO_INCREMENT, name STRING(20))");
        conn.ExecuteNonQuery("INSERT INTO mysql_meta (name) VALUES ('a')");

        Assert.Equal("auto_increment", Scalar(conn, "SELECT extra FROM information_schema.columns WHERE table_name = 'mysql_meta' AND column_name = 'id'"));
        Assert.Equal(String.Empty, Scalar(conn, "SELECT extra FROM information_schema.columns WHERE table_name = 'mysql_meta' AND column_name = 'name'"));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW COLUMNS FROM mysql_meta";
        using var reader = cmd.ExecuteReader();
        var extra = new List<String>();
        while (reader.Read()) extra.Add(Convert.ToString(reader["Extra"]) ?? String.Empty);
        Assert.Contains("auto_increment", extra);
    }

    [Fact(DisplayName = "MySQL兼容-LENGTH 字节与 CHAR_LENGTH 字符")]
    public void StringLength_ByteAndChar()
    {
        using var conn = OpenConnection();

        Assert.Equal(6, Convert.ToInt32(Scalar(conn, "SELECT LENGTH('中文')")));
        Assert.Equal(6, Convert.ToInt32(Scalar(conn, "SELECT LEN('中文')")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT CHAR_LENGTH('中文')")));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT CHARACTER_LENGTH('中文')")));
        Assert.Equal(3, Convert.ToInt32(Scalar(conn, "SELECT LENGTH('abc')")));
    }

    [Fact(DisplayName = "MySQL兼容-LOCATE 与 CHARINDEX 起始位置")]
    public void Locate_And_CharIndex()
    {
        using var conn = OpenConnection();

        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT LOCATE('b', 'abc')")));
        Assert.Equal(0, Convert.ToInt32(Scalar(conn, "SELECT LOCATE('b', 'abc', 3)")));
        Assert.Equal(3, Convert.ToInt32(Scalar(conn, "SELECT LOCATE('c', 'abc', 3)")));
        Assert.Equal(0, Convert.ToInt32(Scalar(conn, "SELECT LOCATE('x', 'abc')")));

        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT CHARINDEX('b', 'abc')")));
        Assert.Equal(3, Convert.ToInt32(Scalar(conn, "SELECT CHARINDEX('c', 'abc', 3)")));
    }

    [Fact(DisplayName = "MySQL兼容-CONCAT 遇 NULL 返回 NULL")]
    public void Concat_NullPropagation()
    {
        using var conn = OpenConnection();

        Assert.Null(Scalar(conn, "SELECT CONCAT('a', NULL)"));
        Assert.Null(Scalar(conn, "SELECT CONCAT(NULL, 'b')"));
        Assert.Equal("ab", Scalar(conn, "SELECT CONCAT('a', 'b')"));
    }

    [Fact(DisplayName = "MySQL兼容-UTC_TIMESTAMP 返回 UTC 时间")]
    public void UtcTimestamp_ReturnsUtc()
    {
        using var conn = OpenConnection();

        var value = Scalar(conn, "SELECT UTC_TIMESTAMP()");
        var utc = Assert.IsType<DateTime>(value);
        Assert.True(Math.Abs((utc - DateTime.UtcNow).TotalMinutes) < 5, $"UTC_TIMESTAMP 偏差过大：{utc:O}");
    }

    [Fact(DisplayName = "MySQL兼容-CHANGE COLUMN 改名改型保留数据")]
    public void ChangeColumn_RenamesAndKeepsData()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_change (id INT PRIMARY KEY, name STRING(20))");
        conn.ExecuteNonQuery("INSERT INTO mysql_change VALUES (1, 'Alice'), (2, 'Bob')");

        conn.ExecuteNonQuery("ALTER TABLE mysql_change CHANGE COLUMN name full_name STRING(100)");

        Assert.Equal("Alice", Scalar(conn, "SELECT full_name FROM mysql_change WHERE id = 1"));
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_change")));

        // 改名 + 改类型
        conn.ExecuteNonQuery("ALTER TABLE mysql_change CHANGE id user_id BIGINT");
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_change WHERE user_id >= 1")));
    }

    [Fact(DisplayName = "MySQL兼容-RENAME TABLE 搬迁数据文件")]
    public void RenameTable_MovesData()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_rename_old (id INT PRIMARY KEY, name STRING(20))");
        conn.ExecuteNonQuery("CREATE INDEX ix_mysql_rename_name ON mysql_rename_old (name)");
        conn.ExecuteNonQuery("INSERT INTO mysql_rename_old VALUES (1, 'a'), (2, 'b')");

        conn.ExecuteNonQuery("RENAME TABLE mysql_rename_old TO mysql_rename_new");

        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_rename_new")));
        // 二级索引随表搬迁后仍可用
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_rename_new WHERE name = 'a'")));
        Assert.True(File.Exists(Path.Combine(MySqlCompatibilityFixture.DbPath, "mysql_rename_new.data")), "数据文件应随表改名搬迁");
        Assert.False(File.Exists(Path.Combine(MySqlCompatibilityFixture.DbPath, "mysql_rename_old.data")), "旧数据文件应已移除");

        // 旧表名不可再访问
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM mysql_rename_old";
        Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());

        // ALTER TABLE ... RENAME TO 等价写法
        conn.ExecuteNonQuery("ALTER TABLE mysql_rename_new RENAME TO mysql_rename_alter");
        Assert.Equal(2, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_rename_alter")));
    }

    [Fact(DisplayName = "MySQL兼容-建表内联索引")]
    public void CreateTable_InlineIndex()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_inline_idx (id INT PRIMARY KEY, uid INT NOT NULL, name STRING(20), UNIQUE KEY uk_uid (uid), KEY ix_name (name))");
        conn.ExecuteNonQuery("INSERT INTO mysql_inline_idx VALUES (1, 10, 'a'), (2, 20, 'b')");

        // 唯一索引生效
        Assert.ThrowsAny<Exception>(() => conn.ExecuteNonQuery("INSERT INTO mysql_inline_idx VALUES (3, 10, 'c')"));

        // 普通索引可用于等值查询
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM mysql_inline_idx WHERE name = 'b'")));

        // 索引写入元数据：PRIMARY + uk_uid + ix_name
        Assert.Equal(3, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM information_schema.statistics WHERE table_name = 'mysql_inline_idx'")));
        Assert.Equal(0, Convert.ToInt32(Scalar(conn, "SELECT non_unique FROM information_schema.statistics WHERE table_name = 'mysql_inline_idx' AND index_name = 'uk_uid'")));

        // 索引列不存在时建表失败且不留残余表
        Assert.ThrowsAny<Exception>(() => conn.ExecuteNonQuery("CREATE TABLE mysql_bad_idx (id INT PRIMARY KEY, KEY ix_missing (nope))"));
        Assert.Equal(0, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM _sys.tables WHERE name = 'mysql_bad_idx'")));
    }

    [Fact(DisplayName = "MySQL兼容-FOR UPDATE 语法可执行")]
    public void ForUpdate_IsAccepted()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery("CREATE TABLE mysql_lock (id INT PRIMARY KEY)");
        conn.ExecuteNonQuery("INSERT INTO mysql_lock VALUES (1), (2)");

        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT id FROM mysql_lock WHERE id = 1 FOR UPDATE")));
        Assert.Single(Query(conn, "SELECT * FROM mysql_lock WHERE id = 1 FOR UPDATE"));
        Assert.Equal(2, Query(conn, "SELECT * FROM mysql_lock LOCK IN SHARE MODE").Count);
    }
}

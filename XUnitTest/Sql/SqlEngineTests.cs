using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>SQL 执行引擎单元测试</summary>
public class SqlEngineTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public SqlEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SqlEngineTests_{Guid.NewGuid():N}");
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

    private void CreateUsersTable()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");
    }

    [Fact(DisplayName = "测试 CREATE TABLE")]
    public void TestCreateTable()
    {
        var result = _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");

        Assert.Equal(0, result.AffectedRows);
        Assert.Contains("users", _engine.TableNames);
    }

    [Fact(DisplayName = "测试 CREATE TABLE IF NOT EXISTS")]
    public void TestCreateTableIfNotExists()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        var result = _engine.Execute("CREATE TABLE IF NOT EXISTS users (id INT PRIMARY KEY, name VARCHAR)");

        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "测试 CREATE TABLE 表已存在异常")]
    public void TestCreateTableDuplicate()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)"));

        Assert.Equal(ErrorCode.TableExists, ex.Code);
    }

    [Fact(DisplayName = "测试 DROP TABLE")]
    public void TestDropTable()
    {
        CreateUsersTable();
        var result = _engine.Execute("DROP TABLE users");

        Assert.Equal(0, result.AffectedRows);
        Assert.DoesNotContain("users", _engine.TableNames);
    }

    [Fact(DisplayName = "测试 DROP TABLE IF EXISTS")]
    public void TestDropTableIfExists()
    {
        var result = _engine.Execute("DROP TABLE IF EXISTS nonexistent");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "测试 TRUNCATE TABLE")]
    public void TestTruncateTable()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        // 验证插入成功
        var query = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(3, query.Rows[0][0]);

        // 执行 TRUNCATE
        var result = _engine.Execute("TRUNCATE TABLE users");
        Assert.Equal(0, result.AffectedRows);

        // 验证数据已清空
        query = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(0, query.Rows[0][0]);

        // 验证表仍然存在，可以继续插入
        Assert.Contains("users", _engine.TableNames);
        _engine.Execute("INSERT INTO users VALUES (1, 'Diana', 28)");

        query = _engine.Execute("SELECT * FROM users");
        Assert.Single(query.Rows);
        Assert.Equal("Diana", query.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 TRUNCATE TABLE 不存在的表")]
    public void TestTruncateTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("TRUNCATE TABLE nonexistent"));

        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "测试 TRUNCATE TABLE 空表")]
    public void TestTruncateEmptyTable()
    {
        CreateUsersTable();

        var result = _engine.Execute("TRUNCATE TABLE users");
        Assert.Equal(0, result.AffectedRows);

        // 表仍然存在且可正常使用
        Assert.Contains("users", _engine.TableNames);
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var query = _engine.Execute("SELECT * FROM users");
        Assert.Single(query.Rows);
    }

    [Fact(DisplayName = "测试 INSERT")]
    public void TestInsert()
    {
        CreateUsersTable();

        var result = _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25)");
        Assert.Equal(1, result.AffectedRows);

        result = _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        Assert.Equal(1, result.AffectedRows);
    }

    [Fact(DisplayName = "测试多行 INSERT")]
    public void TestMultiRowInsert()
    {
        CreateUsersTable();

        var result = _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)");
        Assert.Equal(3, result.AffectedRows);
    }

    [Fact(DisplayName = "测试 SELECT *")]
    public void TestSelectAll()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var result = _engine.Execute("SELECT * FROM users");

        Assert.True(result.IsQuery);
        Assert.Equal(3, result.ColumnNames!.Length);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 SELECT 带列投影")]
    public void TestSelectWithProjection()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute("SELECT name, age FROM users");

        Assert.Equal(2, result.ColumnNames!.Length);
        Assert.Equal("name", result.ColumnNames[0]);
        Assert.Equal("age", result.ColumnNames[1]);
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0][0]);
        Assert.Equal(25, result.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 SELECT 带 WHERE")]
    public void TestSelectWithWhere()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        var result = _engine.Execute("SELECT * FROM users WHERE age > 25");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 SELECT 带 AND/OR")]
    public void TestSelectWithAndOr()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        var result = _engine.Execute("SELECT * FROM users WHERE age >= 30 AND name = 'Bob'");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 SELECT 带 ORDER BY")]
    public void TestSelectWithOrderBy()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Charlie', 35)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Bob', 30)");

        var result = _engine.Execute("SELECT * FROM users ORDER BY name ASC");
        Assert.Equal("Alice", result.Rows[0][1]);
        Assert.Equal("Bob", result.Rows[1][1]);
        Assert.Equal("Charlie", result.Rows[2][1]);

        result = _engine.Execute("SELECT * FROM users ORDER BY age DESC");
        Assert.Equal(35, result.Rows[0][2]);
        Assert.Equal(30, result.Rows[1][2]);
        Assert.Equal(25, result.Rows[2][2]);
    }

    [Fact(DisplayName = "测试 SELECT 带 GROUP BY")]
    public void TestSelectWithGroupBy()
    {
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, product VARCHAR, amount INT)");
        _engine.Execute("INSERT INTO orders VALUES (1, 'Apple', 10)");
        _engine.Execute("INSERT INTO orders VALUES (2, 'Banana', 20)");
        _engine.Execute("INSERT INTO orders VALUES (3, 'Apple', 30)");

        var result = _engine.Execute("SELECT product, SUM(amount) AS total FROM orders GROUP BY product");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 SELECT 带 HAVING")]
    public void TestSelectWithHaving()
    {
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, product VARCHAR, amount INT)");
        _engine.Execute("INSERT INTO orders VALUES (1, 'Apple', 10)");
        _engine.Execute("INSERT INTO orders VALUES (2, 'Banana', 20)");
        _engine.Execute("INSERT INTO orders VALUES (3, 'Apple', 30)");

        var result = _engine.Execute("SELECT product, COUNT(*) AS cnt FROM orders GROUP BY product HAVING COUNT(*) > 1");

        Assert.Single(result.Rows);
        Assert.Equal("Apple", result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 SELECT COUNT")]
    public void TestSelectCount()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var result = _engine.Execute("SELECT COUNT(*) FROM users");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 SELECT SUM/AVG/MIN/MAX")]
    public void TestSelectAggregates()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 20)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 40)");

        var result = _engine.Execute("SELECT SUM(age), AVG(age), MIN(age), MAX(age) FROM users");

        Assert.Equal(90.0, result.Rows[0][0]);
        Assert.Equal(30.0, result.Rows[0][1]);
        Assert.Equal(20, result.Rows[0][2]);
        Assert.Equal(40, result.Rows[0][3]);
    }

    [Fact(DisplayName = "测试 SELECT 带 LIMIT")]
    public void TestSelectWithLimit()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        var result = _engine.Execute("SELECT * FROM users LIMIT 2");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 SELECT 带 OFFSET")]
    public void TestSelectWithOffset()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        var result = _engine.Execute("SELECT * FROM users ORDER BY id ASC LIMIT 2 OFFSET 1");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 UPDATE")]
    public void TestUpdate()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute("UPDATE users SET name = 'Alice Smith', age = 26 WHERE id = 1");
        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT name, age FROM users WHERE id = 1");
        Assert.Equal("Alice Smith", query.Rows[0][0]);
        Assert.Equal(26, query.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 DELETE")]
    public void TestDelete()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var result = _engine.Execute("DELETE FROM users WHERE id = 1");
        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT * FROM users");
        Assert.Single(query.Rows);
        Assert.Equal("Bob", query.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 SELECT 无 FROM")]
    public void TestSelectNoFrom()
    {
        var result = _engine.Execute("SELECT 1");
        Assert.Single(result.Rows);
        Assert.NotNull(result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试参数化查询")]
    public void TestParameterizedQuery()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var parameters = new Dictionary<String, Object?> { ["@id"] = (Int64)1 };
        var result = _engine.Execute("SELECT * FROM users WHERE id = @id", parameters);

        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0][1]);
    }

    [Fact(DisplayName = "测试 LIKE 查询")]
    public void TestLikeQuery()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Alex', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Bob', 35)");

        var result = _engine.Execute("SELECT * FROM users WHERE name LIKE 'Al%'");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试表不存在异常")]
    public void TestTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("SELECT * FROM nonexistent"));

        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "测试 GetScalar 方法")]
    public void TestGetScalar()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(1, result.GetScalar());
    }

    [Fact(DisplayName = "测试 NULL 值插入和查询")]
    public void TestNullValues()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', NULL)");

        var result = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Single(result.Rows);
        Assert.Null(result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 SELECT 带别名")]
    public void TestSelectWithAlias()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute("SELECT name AS user_name FROM users");
        Assert.Equal("user_name", result.ColumnNames![0]);
    }

    [Fact(DisplayName = "测试 ADO.NET 集成")]
    public void TestAdoNetIntegration()
    {
        var dbPath = Path.Combine(_testDir, "ado_test");

        using var conn = new NewLife.NovaDb.Client.NovaConnection
        {
            ConnectionString = $"Data Source={dbPath}"
        };
        conn.Open();

        // CREATE TABLE
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR, price DOUBLE)";
            cmd.ExecuteNonQuery();
        }

        // INSERT
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO products VALUES (1, 'Widget', 9.99)";
            var rows = cmd.ExecuteNonQuery();
            Assert.Equal(1, rows);
        }

        // SELECT
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM products";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.HasRows);
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("Widget", reader.GetString(1));
            Assert.False(reader.Read());
        }

        // ExecuteScalar
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM products";
            var count = cmd.ExecuteScalar();
            Assert.Equal(1, count);
        }

        conn.Close();
    }

    #region JOIN 测试

    private void CreateJoinTables()
    {
        _engine.Execute("CREATE TABLE departments (id INT PRIMARY KEY, name VARCHAR NOT NULL)");
        _engine.Execute("INSERT INTO departments VALUES (1, 'Engineering')");
        _engine.Execute("INSERT INTO departments VALUES (2, 'Marketing')");
        _engine.Execute("INSERT INTO departments VALUES (3, 'Finance')");

        _engine.Execute("CREATE TABLE employees (id INT PRIMARY KEY, name VARCHAR NOT NULL, dept_id INT)");
        _engine.Execute("INSERT INTO employees VALUES (1, 'Alice', 1)");
        _engine.Execute("INSERT INTO employees VALUES (2, 'Bob', 1)");
        _engine.Execute("INSERT INTO employees VALUES (3, 'Charlie', 2)");
        _engine.Execute("INSERT INTO employees VALUES (4, 'Diana', NULL)");
    }

    [Fact(DisplayName = "测试 INNER JOIN")]
    public void TestInnerJoin()
    {
        CreateJoinTables();

        var result = _engine.Execute(
            "SELECT e.name, d.name FROM employees e INNER JOIN departments d ON e.dept_id = d.id ORDER BY e.name ASC");

        Assert.True(result.IsQuery);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0][0]);
        Assert.Equal("Engineering", result.Rows[0][1]);
        Assert.Equal("Bob", result.Rows[1][0]);
        Assert.Equal("Charlie", result.Rows[2][0]);
        Assert.Equal("Marketing", result.Rows[2][1]);
    }

    [Fact(DisplayName = "测试 LEFT JOIN")]
    public void TestLeftJoin()
    {
        CreateJoinTables();

        var result = _engine.Execute(
            "SELECT e.name, d.name FROM employees e LEFT JOIN departments d ON e.dept_id = d.id ORDER BY e.name ASC");

        Assert.True(result.IsQuery);
        Assert.Equal(4, result.Rows.Count);

        // 按 name 排序后: Alice, Bob, Charlie, Diana
        Assert.Equal("Alice", result.Rows[0][0]);
        Assert.Equal("Engineering", result.Rows[0][1]);
        Assert.Equal("Bob", result.Rows[1][0]);
        Assert.Equal("Charlie", result.Rows[2][0]);
        Assert.Equal("Diana", result.Rows[3][0]);
        // Diana 没有 dept_id 匹配, LEFT JOIN 填充 NULL
        Assert.Null(result.Rows[3][1]);
    }

    [Fact(DisplayName = "测试 JOIN 带 WHERE")]
    public void TestJoinWithWhere()
    {
        CreateJoinTables();

        var result = _engine.Execute(
            "SELECT e.name, d.name FROM employees e JOIN departments d ON e.dept_id = d.id WHERE d.name = 'Engineering'");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 JOIN SELECT *")]
    public void TestJoinSelectAll()
    {
        CreateJoinTables();

        var result = _engine.Execute(
            "SELECT * FROM employees e JOIN departments d ON e.dept_id = d.id");

        Assert.True(result.IsQuery);
        Assert.Equal(3, result.Rows.Count);
        // 列数 = employees(3) + departments(2) = 5
        Assert.Equal(5, result.ColumnNames!.Length);
    }

    #endregion

    #region IN 表达式

    [Fact(DisplayName = "IN 值列表查询")]
    public void TestInValueList()
    {
        _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR, price INT)");
        _engine.Execute("INSERT INTO products (id, name, price) VALUES (1, 'Apple', 10)");
        _engine.Execute("INSERT INTO products (id, name, price) VALUES (2, 'Banana', 20)");
        _engine.Execute("INSERT INTO products (id, name, price) VALUES (3, 'Cherry', 30)");
        _engine.Execute("INSERT INTO products (id, name, price) VALUES (4, 'Date', 40)");

        var result = _engine.Execute("SELECT id, name FROM products WHERE id IN (1, 3)");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "NOT IN 值列表查询")]
    public void TestNotInValueList()
    {
        _engine.Execute("CREATE TABLE items (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO items (id, name) VALUES (1, 'A')");
        _engine.Execute("INSERT INTO items (id, name) VALUES (2, 'B')");
        _engine.Execute("INSERT INTO items (id, name) VALUES (3, 'C')");

        var result = _engine.Execute("SELECT id FROM items WHERE id NOT IN (1, 2)");
        Assert.Single(result.Rows);
        Assert.Equal(3, result.Rows[0][0]);
    }

    [Fact(DisplayName = "IN 子查询")]
    public void TestInSubquery()
    {
        _engine.Execute("CREATE TABLE categories (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO categories (id, name) VALUES (1, 'Fruit')");
        _engine.Execute("INSERT INTO categories (id, name) VALUES (2, 'Vegetable')");

        _engine.Execute("CREATE TABLE goods (id INT PRIMARY KEY, name VARCHAR, cat_id INT)");
        _engine.Execute("INSERT INTO goods (id, name, cat_id) VALUES (1, 'Apple', 1)");
        _engine.Execute("INSERT INTO goods (id, name, cat_id) VALUES (2, 'Carrot', 2)");
        _engine.Execute("INSERT INTO goods (id, name, cat_id) VALUES (3, 'Banana', 1)");
        _engine.Execute("INSERT INTO goods (id, name, cat_id) VALUES (4, 'Pea', 3)");

        var result = _engine.Execute("SELECT name FROM goods WHERE cat_id IN (SELECT id FROM categories)");
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact(DisplayName = "IN 字符串值列表")]
    public void TestInStringValues()
    {
        _engine.Execute("CREATE TABLE colors (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO colors (id, name) VALUES (1, 'red')");
        _engine.Execute("INSERT INTO colors (id, name) VALUES (2, 'green')");
        _engine.Execute("INSERT INTO colors (id, name) VALUES (3, 'blue')");

        var result = _engine.Execute("SELECT id FROM colors WHERE name IN ('red', 'blue')");
        Assert.Equal(2, result.Rows.Count);
    }

    #endregion

    #region 系统表查询

    [Fact(DisplayName = "测试 _sys.tables 系统表")]
    public void TestSysTables()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR, age INT)");
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, amount DOUBLE)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 30)");

        var result = _engine.Execute("SELECT * FROM _sys.tables");

        Assert.True(result.IsQuery);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(4, result.ColumnNames!.Length);
        Assert.Equal("name", result.ColumnNames[0]);
        Assert.Equal("column_count", result.ColumnNames[1]);
        Assert.Equal("primary_key", result.ColumnNames[2]);
        Assert.Equal("row_count", result.ColumnNames[3]);
    }

    [Fact(DisplayName = "测试 _sys.tables WHERE 过滤")]
    public void TestSysTablesWithWhere()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, amount DOUBLE)");

        var result = _engine.Execute("SELECT * FROM _sys.tables WHERE name = 'users'");

        Assert.Single(result.Rows);
        Assert.Equal("users", result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 _sys.columns 系统表")]
    public void TestSysColumns()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");

        var result = _engine.Execute("SELECT * FROM _sys.columns");

        Assert.True(result.IsQuery);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(6, result.ColumnNames!.Length);
        Assert.Equal("table_name", result.ColumnNames[0]);
        Assert.Equal("column_name", result.ColumnNames[1]);
        Assert.Equal("data_type", result.ColumnNames[2]);
    }

    [Fact(DisplayName = "测试 _sys.columns WHERE 过滤表名")]
    public void TestSysColumnsFilterByTable()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, amount DOUBLE)");

        var result = _engine.Execute("SELECT * FROM _sys.columns WHERE table_name = 'users'");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 _sys.indexes 系统表")]
    public void TestSysIndexes()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");

        var result = _engine.Execute("SELECT * FROM _sys.indexes");

        Assert.True(result.IsQuery);
        Assert.True(result.Rows.Count >= 1);
        Assert.Equal(4, result.ColumnNames!.Length);
        Assert.Equal("table_name", result.ColumnNames[0]);
        Assert.Equal("index_name", result.ColumnNames[1]);
    }

    [Fact(DisplayName = "测试 _sys.metrics 系统表")]
    public void TestSysMetrics()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
        _engine.Execute("SELECT * FROM users");

        var result = _engine.Execute("SELECT * FROM _sys.metrics");

        Assert.True(result.IsQuery);
        Assert.True(result.Rows.Count > 0);
        Assert.Equal(2, result.ColumnNames!.Length);
        Assert.Equal("metric", result.ColumnNames[0]);
        Assert.Equal("value", result.ColumnNames[1]);
    }

    [Fact(DisplayName = "测试 _sys.metrics WHERE 过滤")]
    public void TestSysMetricsFilter()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice')");

        var result = _engine.Execute("SELECT * FROM _sys.metrics WHERE metric = 'insert_count'");

        Assert.Single(result.Rows);
        Assert.Equal("insert_count", result.Rows[0][0]);
    }

    [Fact(DisplayName = "测试 _sys.version 系统表")]
    public void TestSysVersion()
    {
        var result = _engine.Execute("SELECT * FROM _sys.version");

        Assert.True(result.IsQuery);
        Assert.Single(result.Rows);
        Assert.Equal(3, result.ColumnNames!.Length);
        Assert.Equal("version", result.ColumnNames[0]);
        Assert.Equal("platform", result.ColumnNames[1]);
        Assert.Equal("start_time", result.ColumnNames[2]);
    }

    [Fact(DisplayName = "测试 _sys.tables 带 LIMIT")]
    public void TestSysTablesWithLimit()
    {
        _engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY)");
        _engine.Execute("CREATE TABLE t2 (id INT PRIMARY KEY)");
        _engine.Execute("CREATE TABLE t3 (id INT PRIMARY KEY)");

        var result = _engine.Execute("SELECT * FROM _sys.tables LIMIT 2");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "测试 _sys.tables 列投影")]
    public void TestSysTablesProjection()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");

        var result = _engine.Execute("SELECT name, column_count FROM _sys.tables");

        Assert.Equal(2, result.ColumnNames!.Length);
        Assert.Equal("name", result.ColumnNames[0]);
        Assert.Equal("column_count", result.ColumnNames[1]);
    }

    [Fact(DisplayName = "测试不存在的系统表")]
    public void TestSysTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("SELECT * FROM _sys.nonexistent"));

        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    #endregion

    #region 缺陷修复验证测试

    [Fact(DisplayName = "CREATE TABLE 无主键时在创建阶段就抛出明确错误")]
    public void TestCreateTableWithoutPrimaryKeyThrows()
    {
        // NovaTable 构造函数已强制要求主键，确保错误在 CREATE TABLE 时就被捕获
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("CREATE TABLE nopk (name VARCHAR, age INT)"));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "UPDATE 有主键表正常工作")]
    public void TestUpdateTableWithPrimaryKey()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");

        var result = _engine.Execute("UPDATE users SET age = 31 WHERE id = 1");
        Assert.Equal(1, result.AffectedRows);

        var selectResult = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Equal(1, selectResult.Rows.Count);
        Assert.Equal(31, Convert.ToInt32(selectResult.Rows[0][0]));
    }

    [Fact(DisplayName = "DELETE 有主键表正常工作")]
    public void TestDeleteFromTableWithPrimaryKey()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (2, 'Bob', 25)");

        var result = _engine.Execute("DELETE FROM users WHERE id = 1");
        Assert.Equal(1, result.AffectedRows);

        var selectResult = _engine.Execute("SELECT * FROM users");
        Assert.Equal(1, selectResult.Rows.Count);
    }

    #endregion
}

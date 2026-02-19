using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>SQL 执行引擎综合测试</summary>
public class SqlEngineFullTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public SqlEngineFullTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SqlEngineFullTests_{Guid.NewGuid():N}");
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

    private void SeedUsers()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");
    }

    #region UPSERT 测试

    [Fact(DisplayName = "UPSERT 行不存在时执行插入")]
    public void UpsertInsertWhenNotExists()
    {
        CreateUsersTable();

        var result = _engine.Execute(
            "INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25) ON DUPLICATE KEY UPDATE name = 'Alice2', age = 26");

        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT name, age FROM users WHERE id = 1");
        Assert.Single(query.Rows);
        Assert.Equal("Alice", query.Rows[0][0]);
        Assert.Equal(25, query.Rows[0][1]);
    }

    [Fact(DisplayName = "UPSERT 行已存在时执行更新")]
    public void UpsertUpdateWhenExists()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute(
            "INSERT INTO users (id, name, age) VALUES (1, 'Alice2', 99) ON DUPLICATE KEY UPDATE name = 'AliceUpdated', age = 26");

        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT name, age FROM users WHERE id = 1");
        Assert.Single(query.Rows);
        Assert.Equal("AliceUpdated", query.Rows[0][0]);
        Assert.Equal(26, query.Rows[0][1]);
    }

    [Fact(DisplayName = "UPSERT 不指定列名")]
    public void UpsertWithoutColumns()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var result = _engine.Execute(
            "INSERT INTO users VALUES (1, 'Bob', 99) ON DUPLICATE KEY UPDATE name = 'Updated'");

        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT name FROM users WHERE id = 1");
        Assert.Equal("Updated", query.Rows[0][0]);
    }

    [Fact(DisplayName = "UPSERT 多行混合插入和更新")]
    public void UpsertMultipleRowsMixed()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        // id=1 已存在（更新），id=2 不存在（插入）
        var result = _engine.Execute(
            "INSERT INTO users VALUES (1, 'X', 0), (2, 'Bob', 30) ON DUPLICATE KEY UPDATE name = 'AliceUpdated', age = 26");

        Assert.Equal(2, result.AffectedRows);

        var q1 = _engine.Execute("SELECT name, age FROM users WHERE id = 1");
        Assert.Equal("AliceUpdated", q1.Rows[0][0]);
        Assert.Equal(26, q1.Rows[0][1]);

        var q2 = _engine.Execute("SELECT name, age FROM users WHERE id = 2");
        Assert.Equal("Bob", q2.Rows[0][0]);
        Assert.Equal(30, q2.Rows[0][1]);
    }

    [Fact(DisplayName = "UPSERT 更新子句引用已有行值")]
    public void UpsertUpdateReferencesExistingValues()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        _engine.Execute(
            "INSERT INTO users VALUES (1, 'X', 0) ON DUPLICATE KEY UPDATE age = age + 1");

        var query = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Equal(26, query.Rows[0][0]);
    }

    [Fact(DisplayName = "UPSERT 插入 NULL 值")]
    public void UpsertWithNullValues()
    {
        CreateUsersTable();

        _engine.Execute(
            "INSERT INTO users VALUES (1, 'Alice', NULL) ON DUPLICATE KEY UPDATE age = 99");

        var query = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Single(query.Rows);
        Assert.Null(query.Rows[0][0]);
    }

    [Fact(DisplayName = "UPSERT 使用参数化值")]
    public void UpsertWithParameters()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");

        var parameters = new Dictionary<String, Object?>
        {
            ["@id"] = (Int64)1,
            ["@name"] = "ParamAlice",
            ["@age"] = (Int64)99,
            ["@newAge"] = (Int64)50
        };

        _engine.Execute(
            "INSERT INTO users (id, name, age) VALUES (@id, @name, @age) ON DUPLICATE KEY UPDATE age = @newAge",
            parameters);

        var query = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Equal(50, query.Rows[0][0]);
    }

    #endregion

    #region DDL 测试

    [Fact(DisplayName = "CREATE TABLE 支持多种数据类型")]
    public void CreateTableWithVariousDataTypes()
    {
        _engine.Execute(@"CREATE TABLE all_types (
            id INT PRIMARY KEY,
            flag BOOL,
            big_id BIGINT,
            price DOUBLE,
            amount DECIMAL,
            name VARCHAR,
            created DATETIME
        )");

        Assert.Contains("all_types", _engine.TableNames);

        var cols = _engine.Execute("SELECT * FROM _sys.columns WHERE table_name = 'all_types'");
        Assert.Equal(7, cols.Rows.Count);
    }

    [Fact(DisplayName = "CREATE TABLE 带 ENGINE 子句")]
    public void CreateTableWithEngine()
    {
        _engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY, name VARCHAR) ENGINE=Nova");
        Assert.Contains("t1", _engine.TableNames);
    }

    [Fact(DisplayName = "CREATE TABLE 带表注释")]
    public void CreateTableWithComment()
    {
        _engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY, name VARCHAR) COMMENT '用户表'");
        Assert.Contains("t1", _engine.TableNames);
    }

    [Fact(DisplayName = "CREATE TABLE 带列注释")]
    public void CreateTableWithColumnComment()
    {
        _engine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY COMMENT '主键', name VARCHAR COMMENT '姓名')");
        Assert.Contains("t1", _engine.TableNames);
    }

    [Fact(DisplayName = "CREATE INDEX 和 DROP INDEX")]
    public void CreateAndDropIndex()
    {
        CreateUsersTable();

        var result = _engine.Execute("CREATE INDEX idx_name ON users (name)");
        Assert.Equal(0, result.AffectedRows);

        result = _engine.Execute("DROP INDEX idx_name ON users");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "CREATE INDEX 表不存在异常")]
    public void CreateIndexTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("CREATE INDEX idx_name ON nonexistent (name)"));
        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "ALTER TABLE ADD COLUMN")]
    public void AlterTableAddColumn()
    {
        CreateUsersTable();

        _engine.Execute("ALTER TABLE users ADD COLUMN email VARCHAR");

        var cols = _engine.Execute("SELECT * FROM _sys.columns WHERE table_name = 'users'");
        Assert.Equal(4, cols.Rows.Count);
    }

    [Fact(DisplayName = "ALTER TABLE MODIFY COLUMN")]
    public void AlterTableModifyColumn()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users MODIFY COLUMN age BIGINT");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "ALTER TABLE DROP COLUMN")]
    public void AlterTableDropColumn()
    {
        CreateUsersTable();

        _engine.Execute("ALTER TABLE users DROP COLUMN age");

        var cols = _engine.Execute("SELECT * FROM _sys.columns WHERE table_name = 'users'");
        Assert.Equal(2, cols.Rows.Count);
    }

    [Fact(DisplayName = "ALTER TABLE 添加表注释")]
    public void AlterTableComment()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users COMMENT '用户信息表'");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "ALTER TABLE 表不存在异常")]
    public void AlterTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE nonexistent ADD COLUMN col1 INT"));
        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "CREATE DATABASE 和 DROP DATABASE")]
    public void CreateAndDropDatabase()
    {
        var result = _engine.Execute("CREATE DATABASE testdb");
        Assert.Equal(0, result.AffectedRows);

        result = _engine.Execute("DROP DATABASE testdb");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "CREATE DATABASE IF NOT EXISTS")]
    public void CreateDatabaseIfNotExists()
    {
        _engine.Execute("CREATE DATABASE testdb2");

        var result = _engine.Execute("CREATE DATABASE IF NOT EXISTS testdb2");
        Assert.Equal(0, result.AffectedRows);

        // 清理
        _engine.Execute("DROP DATABASE testdb2");
    }

    [Fact(DisplayName = "DROP DATABASE IF EXISTS 不存在")]
    public void DropDatabaseIfExistsNotFound()
    {
        var result = _engine.Execute("DROP DATABASE IF EXISTS nonexistentdb");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "CREATE DATABASE 已存在异常")]
    public void CreateDatabaseDuplicate()
    {
        _engine.Execute("CREATE DATABASE dupdb");

        try
        {
            var ex = Assert.Throws<NovaException>(() =>
                _engine.Execute("CREATE DATABASE dupdb"));
            Assert.Equal(ErrorCode.DatabaseExists, ex.Code);
        }
        finally
        {
            _engine.Execute("DROP DATABASE IF EXISTS dupdb");
        }
    }

    [Fact(DisplayName = "DROP DATABASE 不存在异常")]
    public void DropDatabaseNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("DROP DATABASE nonexistentdb"));
        Assert.Equal(ErrorCode.DatabaseNotFound, ex.Code);
    }

    #endregion

    #region DML 测试

    [Fact(DisplayName = "INSERT 指定列顺序与表定义不同")]
    public void InsertWithDifferentColumnOrder()
    {
        CreateUsersTable();

        _engine.Execute("INSERT INTO users (age, id, name) VALUES (25, 1, 'Alice')");

        var query = _engine.Execute("SELECT id, name, age FROM users");
        Assert.Single(query.Rows);
        Assert.Equal(1, query.Rows[0][0]);
        Assert.Equal("Alice", query.Rows[0][1]);
        Assert.Equal(25, query.Rows[0][2]);
    }

    [Fact(DisplayName = "INSERT 可空列使用 NULL")]
    public void InsertNullForNullableColumn()
    {
        CreateUsersTable();

        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', NULL)");
        _engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");

        var query = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Null(query.Rows[0][0]);
    }

    [Fact(DisplayName = "INSERT 值数量不匹配异常")]
    public void InsertWrongValueCount()
    {
        CreateUsersTable();

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("INSERT INTO users VALUES (1, 'Alice')"));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "UPDATE 无 WHERE 更新所有行")]
    public void UpdateWithoutWhere()
    {
        SeedUsers();

        var result = _engine.Execute("UPDATE users SET age = 99");
        Assert.Equal(3, result.AffectedRows);

        var query = _engine.Execute("SELECT age FROM users");
        foreach (var row in query.Rows)
        {
            Assert.Equal(99, row[0]);
        }
    }

    [Fact(DisplayName = "UPDATE SET 中使用算术表达式")]
    public void UpdateWithArithmeticExpression()
    {
        SeedUsers();

        _engine.Execute("UPDATE users SET age = age * 2 WHERE id = 1");

        var query = _engine.Execute("SELECT age FROM users WHERE id = 1");
        Assert.Equal(50, query.Rows[0][0]);
    }

    [Fact(DisplayName = "DELETE 无 WHERE 删除所有行")]
    public void DeleteWithoutWhere()
    {
        SeedUsers();

        var result = _engine.Execute("DELETE FROM users");
        Assert.Equal(3, result.AffectedRows);

        var query = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(0, query.Rows[0][0]);
    }

    [Fact(DisplayName = "DELETE 复合 WHERE 条件")]
    public void DeleteWithCompoundWhere()
    {
        SeedUsers();

        var result = _engine.Execute("DELETE FROM users WHERE age >= 30 AND name = 'Bob'");
        Assert.Equal(1, result.AffectedRows);

        var query = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(2, query.Rows[0][0]);
    }

    #endregion

    #region 高级 SELECT 测试

    [Fact(DisplayName = "SELECT 多列 ORDER BY")]
    public void SelectMultipleOrderBy()
    {
        _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, category VARCHAR, price INT)");
        _engine.Execute("INSERT INTO products VALUES (1, 'A', 30)");
        _engine.Execute("INSERT INTO products VALUES (2, 'B', 20)");
        _engine.Execute("INSERT INTO products VALUES (3, 'A', 10)");

        var result = _engine.Execute("SELECT * FROM products ORDER BY category ASC, price ASC");

        Assert.Equal("A", result.Rows[0][1]);
        Assert.Equal(10, result.Rows[0][2]);
        Assert.Equal("A", result.Rows[1][1]);
        Assert.Equal(30, result.Rows[1][2]);
        Assert.Equal("B", result.Rows[2][1]);
    }

    [Fact(DisplayName = "SELECT 表达式列（算术运算）")]
    public void SelectExpressionColumn()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT name, age + 1 FROM users WHERE id = 1");

        Assert.Equal("Alice", result.Rows[0][0]);
        Assert.Equal(26.0, result.Rows[0][1]);
    }

    [Fact(DisplayName = "SELECT IS NULL")]
    public void SelectIsNull()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', NULL)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var result = _engine.Execute("SELECT * FROM users WHERE age IS NULL");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0][1]);
    }

    [Fact(DisplayName = "SELECT IS NOT NULL")]
    public void SelectIsNotNull()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', NULL)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");

        var result = _engine.Execute("SELECT * FROM users WHERE age IS NOT NULL");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0][1]);
    }

    [Fact(DisplayName = "SELECT LIKE 下划线通配符")]
    public void SelectLikeUnderscore()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Al', 25)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Ax', 35)");

        var result = _engine.Execute("SELECT * FROM users WHERE name LIKE 'A_'");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "SELECT 不等于条件")]
    public void SelectNotCondition()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT * FROM users WHERE age <> 25");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "SELECT COUNT 非空列")]
    public void SelectCountColumn()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', NULL)");
        _engine.Execute("INSERT INTO users VALUES (2, 'Bob', 30)");
        _engine.Execute("INSERT INTO users VALUES (3, 'Charlie', 35)");

        var countAll = _engine.Execute("SELECT COUNT(*) FROM users");
        Assert.Equal(3, countAll.Rows[0][0]);

        var countAge = _engine.Execute("SELECT COUNT(age) FROM users");
        Assert.Equal(2, countAge.Rows[0][0]);
    }

    [Fact(DisplayName = "SELECT CASE WHEN 表达式")]
    public void SelectCaseWhen()
    {
        SeedUsers();

        var result = _engine.Execute(
            "SELECT name, CASE WHEN age >= 30 THEN 'senior' ELSE 'junior' END AS level FROM users ORDER BY id ASC");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("junior", result.Rows[0][1]);
        Assert.Equal("senior", result.Rows[1][1]);
        Assert.Equal("senior", result.Rows[2][1]);
    }

    [Fact(DisplayName = "SELECT CAST 表达式")]
    public void SelectCast()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT CAST(age AS BIGINT) FROM users WHERE id = 1");
        Assert.Single(result.Rows);
        Assert.IsType<Int64>(result.Rows[0][0]);
    }

    [Fact(DisplayName = "SELECT 计算列带别名")]
    public void SelectComputedColumnAlias()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT name, age * 2 AS double_age FROM users WHERE id = 1");
        Assert.Equal("double_age", result.ColumnNames![1]);
        Assert.Equal(50.0, result.Rows[0][1]);
    }

    [Fact(DisplayName = "SELECT BETWEEN 风格条件（>= AND <=）")]
    public void SelectBetweenStyle()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT * FROM users WHERE age >= 25 AND age <= 30");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "SELECT 负数")]
    public void SelectNegativeNumber()
    {
        _engine.Execute("CREATE TABLE data (id INT PRIMARY KEY, value INT)");
        _engine.Execute("INSERT INTO data VALUES (1, -10)");
        _engine.Execute("INSERT INTO data VALUES (2, 20)");

        var result = _engine.Execute("SELECT * FROM data WHERE value < 0");
        Assert.Single(result.Rows);
        Assert.Equal(-10, result.Rows[0][1]);
    }

    [Fact(DisplayName = "SELECT 1 + 1 无表")]
    public void SelectArithmeticNoTable()
    {
        var result = _engine.Execute("SELECT 1 + 1");
        Assert.Single(result.Rows);
        Assert.Equal(2.0, result.Rows[0][0]);
    }

    [Fact(DisplayName = "SELECT COALESCE 函数")]
    public void SelectCoalesce()
    {
        CreateUsersTable();
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', NULL)");

        var result = _engine.Execute("SELECT COALESCE(age, 0) FROM users WHERE id = 1");
        Assert.Single(result.Rows);
        Assert.Equal((Int64)0, result.Rows[0][0]);
    }

    [Fact(DisplayName = "SELECT 空结果集")]
    public void SelectEmptyResult()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT * FROM users WHERE age > 100");
        Assert.True(result.IsQuery);
        Assert.Empty(result.Rows);
    }

    #endregion

    #region 边界与错误处理

    [Fact(DisplayName = "DROP TABLE 不存在且无 IF EXISTS 应抛异常")]
    public void DropTableNotExistsThrows()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("DROP TABLE nonexistent"));
        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    [Fact(DisplayName = "只读模式阻止写操作")]
    public void ReadOnlyModeBlocksWrites()
    {
        var readOnlyDir = Path.Combine(Path.GetTempPath(), $"SqlEngineRO_{Guid.NewGuid():N}");
        Directory.CreateDirectory(readOnlyDir);

        try
        {
            using var roEngine = new SqlEngine(readOnlyDir, new DbOptions { Path = readOnlyDir, WalMode = WalMode.None, ReadOnly = true });

            var ex = Assert.Throws<NovaException>(() =>
                roEngine.Execute("CREATE TABLE t1 (id INT PRIMARY KEY)"));
            Assert.Equal(ErrorCode.ReadOnlyViolation, ex.Code);
        }
        finally
        {
            try { Directory.Delete(readOnlyDir, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "只读模式允许 SELECT")]
    public void ReadOnlyModeAllowsSelect()
    {
        var readOnlyDir = Path.Combine(Path.GetTempPath(), $"SqlEngineRO2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(readOnlyDir);

        try
        {
            using var roEngine = new SqlEngine(readOnlyDir, new DbOptions { Path = readOnlyDir, WalMode = WalMode.None, ReadOnly = true });

            var result = roEngine.Execute("SELECT 1");
            Assert.Single(result.Rows);
        }
        finally
        {
            try { Directory.Delete(readOnlyDir, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "SQL 语法错误异常")]
    public void SqlSyntaxError()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("SELECTT * FROM users"));
        Assert.Equal(ErrorCode.SyntaxError, ex.Code);
    }

    [Fact(DisplayName = "GetScalar 空结果集返回 null")]
    public void GetScalarEmptyResult()
    {
        SeedUsers();

        var result = _engine.Execute("SELECT * FROM users WHERE age > 100");
        Assert.Null(result.GetScalar());
    }

    #endregion

    #region 指标追踪

    [Fact(DisplayName = "验证 Metrics 计数")]
    public void VerifyMetricsCounts()
    {
        var initialExec = _engine.Metrics.ExecuteCount;

        // DDL
        CreateUsersTable();
        Assert.Equal(initialExec + 1, _engine.Metrics.ExecuteCount);
        Assert.Equal(1, _engine.Metrics.DdlCount);

        // INSERT
        _engine.Execute("INSERT INTO users VALUES (1, 'Alice', 25)");
        Assert.Equal(1, _engine.Metrics.InsertCount);

        // UPDATE
        _engine.Execute("UPDATE users SET age = 26 WHERE id = 1");
        Assert.Equal(1, _engine.Metrics.UpdateCount);

        // DELETE
        _engine.Execute("DELETE FROM users WHERE id = 1");
        Assert.Equal(1, _engine.Metrics.DeleteCount);

        // QUERY
        _engine.Execute("SELECT * FROM users");
        Assert.Equal(1, _engine.Metrics.QueryCount);

        Assert.Equal(initialExec + 5, _engine.Metrics.ExecuteCount);
    }

    [Fact(DisplayName = "UPSERT 计入 InsertCount")]
    public void UpsertTrackedAsInsert()
    {
        CreateUsersTable();
        var beforeInsert = _engine.Metrics.InsertCount;

        _engine.Execute(
            "INSERT INTO users VALUES (1, 'Alice', 25) ON DUPLICATE KEY UPDATE name = 'X'");

        Assert.Equal(beforeInsert + 1, _engine.Metrics.InsertCount);
    }

    #endregion
}

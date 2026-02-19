using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>扩展 DDL 功能单元测试（CREATE/DROP DATABASE、ALTER TABLE、ENGINE、COMMENT）</summary>
public class DdlExtendedTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public DdlExtendedTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DdlExtendedTests_{Guid.NewGuid():N}");
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

    #region CREATE DATABASE

    [Fact(DisplayName = "测试 CREATE DATABASE")]
    public void TestCreateDatabase()
    {
        var result = _engine.Execute("CREATE DATABASE testdb");
        Assert.Equal(0, result.AffectedRows);

        var dbPath = Path.Combine(Path.GetDirectoryName(_testDir)!, "testdb");
        Assert.True(Directory.Exists(dbPath));

        // 清理
        Directory.Delete(dbPath, recursive: true);
    }

    [Fact(DisplayName = "测试 CREATE DATABASE IF NOT EXISTS")]
    public void TestCreateDatabaseIfNotExists()
    {
        _engine.Execute("CREATE DATABASE testdb2");
        var result = _engine.Execute("CREATE DATABASE IF NOT EXISTS testdb2");
        Assert.Equal(0, result.AffectedRows);

        // 清理
        var dbPath = Path.Combine(Path.GetDirectoryName(_testDir)!, "testdb2");
        Directory.Delete(dbPath, recursive: true);
    }

    [Fact(DisplayName = "测试 CREATE DATABASE 已存在异常")]
    public void TestCreateDatabaseDuplicate()
    {
        _engine.Execute("CREATE DATABASE testdb3");

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("CREATE DATABASE testdb3"));

        Assert.Equal(ErrorCode.DatabaseExists, ex.Code);

        // 清理
        var dbPath = Path.Combine(Path.GetDirectoryName(_testDir)!, "testdb3");
        Directory.Delete(dbPath, recursive: true);
    }

    #endregion

    #region DROP DATABASE

    [Fact(DisplayName = "测试 DROP DATABASE")]
    public void TestDropDatabase()
    {
        _engine.Execute("CREATE DATABASE testdb4");
        var result = _engine.Execute("DROP DATABASE testdb4");
        Assert.Equal(0, result.AffectedRows);

        var dbPath = Path.Combine(Path.GetDirectoryName(_testDir)!, "testdb4");
        Assert.False(Directory.Exists(dbPath));
    }

    [Fact(DisplayName = "测试 DROP DATABASE IF EXISTS")]
    public void TestDropDatabaseIfExists()
    {
        var result = _engine.Execute("DROP DATABASE IF EXISTS nonexistentdb");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact(DisplayName = "测试 DROP DATABASE 不存在异常")]
    public void TestDropDatabaseNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("DROP DATABASE nonexistentdb"));

        Assert.Equal(ErrorCode.DatabaseNotFound, ex.Code);
    }

    #endregion

    #region ALTER TABLE ADD COLUMN

    [Fact(DisplayName = "测试 ALTER TABLE ADD COLUMN")]
    public void TestAlterTableAddColumn()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users ADD COLUMN email VARCHAR");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.True(schema.HasColumn("email"));
        Assert.Equal(3, schema.GetColumnIndex("email"));
    }

    [Fact(DisplayName = "测试 ALTER TABLE ADD 省略 COLUMN 关键字")]
    public void TestAlterTableAddWithoutColumnKeyword()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users ADD email VARCHAR");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.True(schema.HasColumn("email"));
    }

    [Fact(DisplayName = "测试 ALTER TABLE ADD COLUMN 重复列异常")]
    public void TestAlterTableAddDuplicateColumn()
    {
        CreateUsersTable();

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE users ADD COLUMN name VARCHAR"));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "测试 ALTER TABLE ADD COLUMN 表不存在异常")]
    public void TestAlterTableAddColumnTableNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE nonexistent ADD COLUMN col INT"));

        Assert.Equal(ErrorCode.TableNotFound, ex.Code);
    }

    #endregion

    #region ALTER TABLE MODIFY COLUMN

    [Fact(DisplayName = "测试 ALTER TABLE MODIFY COLUMN")]
    public void TestAlterTableModifyColumn()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users MODIFY COLUMN age LONG NOT NULL");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        var col = schema.GetColumn("age");
        Assert.Equal(DataType.Int64, col.DataType);
        Assert.False(col.Nullable);
    }

    [Fact(DisplayName = "测试 ALTER TABLE MODIFY COLUMN 带注释")]
    public void TestAlterTableModifyColumnWithComment()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users MODIFY COLUMN age INT NOT NULL COMMENT '用户年龄'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        var col = schema.GetColumn("age");
        Assert.Equal("用户年龄", col.Comment);
    }

    [Fact(DisplayName = "测试 ALTER TABLE MODIFY COLUMN 列不存在异常")]
    public void TestAlterTableModifyColumnNotFound()
    {
        CreateUsersTable();

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE users MODIFY COLUMN nonexistent INT"));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    #endregion

    #region ALTER TABLE DROP COLUMN

    [Fact(DisplayName = "测试 ALTER TABLE DROP COLUMN")]
    public void TestAlterTableDropColumn()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users DROP COLUMN age");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.False(schema.HasColumn("age"));
        Assert.Equal(2, schema.Columns.Count);
    }

    [Fact(DisplayName = "测试 ALTER TABLE DROP COLUMN 不能删除主键")]
    public void TestAlterTableDropPrimaryKey()
    {
        CreateUsersTable();

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE users DROP COLUMN id"));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "测试 ALTER TABLE DROP COLUMN 列不存在异常")]
    public void TestAlterTableDropColumnNotFound()
    {
        CreateUsersTable();

        var ex = Assert.Throws<NovaException>(() =>
            _engine.Execute("ALTER TABLE users DROP COLUMN nonexistent"));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    #endregion

    #region COMMENT

    [Fact(DisplayName = "测试 CREATE TABLE 带表注释")]
    public void TestCreateTableWithComment()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR) COMMENT '产品表'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("产品表", schema.Comment);
    }

    [Fact(DisplayName = "测试 CREATE TABLE 带等号注释")]
    public void TestCreateTableWithCommentEquals()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR) COMMENT = '产品表'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("产品表", schema.Comment);
    }

    [Fact(DisplayName = "测试 CREATE TABLE 带列注释")]
    public void TestCreateTableWithColumnComment()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY COMMENT '产品ID', name VARCHAR COMMENT '产品名称')");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("产品ID", schema.GetColumn("id").Comment);
        Assert.Equal("产品名称", schema.GetColumn("name").Comment);
    }

    [Fact(DisplayName = "测试 ALTER TABLE 表注释")]
    public void TestAlterTableComment()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users COMMENT '用户表'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.Equal("用户表", schema.Comment);
    }

    [Fact(DisplayName = "测试 ALTER TABLE 表注释带等号")]
    public void TestAlterTableCommentWithEquals()
    {
        CreateUsersTable();

        var result = _engine.Execute("ALTER TABLE users COMMENT = '用户信息表'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.Equal("用户信息表", schema.Comment);
    }

    #endregion

    #region ENGINE

    [Fact(DisplayName = "测试 CREATE TABLE 默认引擎为 Nova")]
    public void TestCreateTableDefaultEngine()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR)");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("Nova", schema.EngineName);
    }

    [Fact(DisplayName = "测试 CREATE TABLE ENGINE=Nova")]
    public void TestCreateTableWithNovaEngine()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR) ENGINE=Nova");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("Nova", schema.EngineName);
    }

    [Fact(DisplayName = "测试 CREATE TABLE ENGINE=Flux")]
    public void TestCreateTableWithFluxEngine()
    {
        var result = _engine.Execute("CREATE TABLE logs (id INT PRIMARY KEY, message VARCHAR) ENGINE=Flux");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("logs");
        Assert.NotNull(schema);
        Assert.Equal("Flux", schema.EngineName);
    }

    [Fact(DisplayName = "测试 CREATE TABLE ENGINE 和 COMMENT 组合")]
    public void TestCreateTableWithEngineAndComment()
    {
        var result = _engine.Execute("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR COMMENT '名称') ENGINE=Nova COMMENT '产品表'");
        Assert.Equal(0, result.AffectedRows);

        var schema = _engine.GetTableSchema("products");
        Assert.NotNull(schema);
        Assert.Equal("Nova", schema.EngineName);
        Assert.Equal("产品表", schema.Comment);
        Assert.Equal("名称", schema.GetColumn("name").Comment);
    }

    #endregion

    #region 综合

    [Fact(DisplayName = "测试 ALTER TABLE 多次操作")]
    public void TestAlterTableMultipleOperations()
    {
        CreateUsersTable();

        // 添加列
        _engine.Execute("ALTER TABLE users ADD COLUMN email VARCHAR");
        Assert.True(_engine.GetTableSchema("users")!.HasColumn("email"));

        // 修改列
        _engine.Execute("ALTER TABLE users MODIFY COLUMN email VARCHAR NOT NULL");
        Assert.False(_engine.GetTableSchema("users")!.GetColumn("email").Nullable);

        // 删除列
        _engine.Execute("ALTER TABLE users DROP COLUMN age");
        Assert.False(_engine.GetTableSchema("users")!.HasColumn("age"));

        // 添加表注释
        _engine.Execute("ALTER TABLE users COMMENT '用户表'");
        Assert.Equal("用户表", _engine.GetTableSchema("users")!.Comment);

        // 验证最终结构
        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.Equal(3, schema.Columns.Count); // id, name, email
        Assert.True(schema.HasColumn("id"));
        Assert.True(schema.HasColumn("name"));
        Assert.True(schema.HasColumn("email"));
    }

    [Fact(DisplayName = "测试 ALTER TABLE DROP COLUMN 后列序号正确")]
    public void TestAlterTableDropColumnOrdinals()
    {
        CreateUsersTable();

        // 删除中间的列
        _engine.Execute("ALTER TABLE users DROP COLUMN name");

        var schema = _engine.GetTableSchema("users");
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal(0, schema.GetColumnIndex("id"));
        Assert.Equal(1, schema.GetColumnIndex("age"));
    }

    #endregion
}

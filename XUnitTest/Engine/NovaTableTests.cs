using System;
using System.IO;
using System.Linq;
using Xunit;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

#nullable enable

namespace XUnitTest.Engine;

/// <summary>
/// NovaTable 单元测试
/// </summary>
public class NovaTableTests : IDisposable
{
    private readonly String _testDir;

    public NovaTableTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"NovaTableTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
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

    private TableSchema CreateTestSchema()
    {
        var schema = new TableSchema("users");
        schema.AddColumn(new ColumnDefinition("id", DataType.Int32, nullable: false, isPrimaryKey: true));
        schema.AddColumn(new ColumnDefinition("name", DataType.String, nullable: false));
        schema.AddColumn(new ColumnDefinition("age", DataType.Int32, nullable: true));
        return schema;
    }

    [Fact(DisplayName = "测试创建表")]
    public void TestCreateTable()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        Assert.NotNull(table);
        Assert.Equal(schema, table.Schema);
        // 表文件平铺在数据库目录下，不再创建表子目录
        Assert.True(File.Exists(Path.Combine(_testDir, "users.data")));
    }

    [Fact(DisplayName = "测试插入和查询")]
    public void TestInsertAndGet()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        // 插入一行
        var row = new Object?[] { 1, "Alice", 25 };
        table.Insert(tx, row);

        // 查询
        var result = table.Get(tx, 1);
        Assert.NotNull(result);
        Assert.Equal(1, result![0]);
        Assert.Equal("Alice", result[1]);
        Assert.Equal(25, result[2]);

        tx.Commit();
    }

    [Fact(DisplayName = "测试主键冲突")]
    public void TestPrimaryKeyConflict()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        var row1 = new Object?[] { 1, "Alice", 25 };
        var row2 = new Object?[] { 1, "Bob", 30 };

        table.Insert(tx, row1);

        // 尝试插入相同主键应该失败
        Assert.Throws<NovaException>(() => table.Insert(tx, row2));

        tx.Commit();
    }

    [Fact(DisplayName = "测试更新")]
    public void TestUpdate()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        // 插入
        var row = new Object?[] { 1, "Alice", 25 };
        table.Insert(tx, row);

        // 更新
        var newRow = new Object?[] { 1, "Alice Smith", 26 };
        var updated = table.Update(tx, 1, newRow);
        Assert.True(updated);

        // 验证更新
        var result = table.Get(tx, 1);
        Assert.NotNull(result);
        Assert.Equal("Alice Smith", result![1]);
        Assert.Equal(26, result[2]);

        tx.Commit();
    }

    [Fact(DisplayName = "测试删除")]
    public void TestDelete()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        // 插入
        var row = new Object?[] { 1, "Alice", 25 };
        table.Insert(tx, row);

        // 删除
        var deleted = table.Delete(tx, 1);
        Assert.True(deleted);

        // 验证删除
        var result = table.Get(tx, 1);
        Assert.Null(result);

        tx.Commit();
    }

    [Fact(DisplayName = "测试事务隔离")]
    public void TestTransactionIsolation()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        // 事务 1 插入数据但不提交
        using var tx1 = txManager.BeginTransaction();
        var row = new Object?[] { 1, "Alice", 25 };
        table.Insert(tx1, row);

        // 事务 2 不应该看到事务 1 的未提交数据
        using var tx2 = txManager.BeginTransaction();
        var result = table.Get(tx2, 1);
        Assert.Null(result);

        // 事务 1 提交后，事务 2 应该能看到（Read Committed）
        tx1.Commit();
        result = table.Get(tx2, 1);
        Assert.NotNull(result);

        // 新事务应该能看到已提交的数据
        using var tx3 = txManager.BeginTransaction();
        result = table.Get(tx3, 1);
        Assert.NotNull(result);
    }

    [Fact(DisplayName = "测试事务回滚")]
    public void TestTransactionRollback()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        // 事务 1 插入数据后回滚
        using (var tx1 = txManager.BeginTransaction())
        {
            var row = new Object?[] { 1, "Alice", 25 };
            table.Insert(tx1, row);
            tx1.Rollback();
        }

        // 新事务不应该看到回滚的数据
        using var tx2 = txManager.BeginTransaction();
        var result = table.Get(tx2, 1);
        Assert.Null(result);
    }

    [Fact(DisplayName = "测试获取所有行")]
    public void TestGetAll()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        // 插入多行
        table.Insert(tx, [1, "Alice", 25]);
        table.Insert(tx, [2, "Bob", 30]);
        table.Insert(tx, [3, "Charlie", 35]);

        // 获取所有行
        var all = table.GetAll(tx);
        Assert.Equal(3, all.Count);

        tx.Commit();
    }

    [Fact(DisplayName = "测试空主键异常")]
    public void TestNullPrimaryKey()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        var row = new Object?[] { null, "Alice", 25 };

        Assert.Throws<NovaException>(() => table.Insert(tx, row));
    }

    [Fact(DisplayName = "测试 WAL 模式")]
    public void TestWalMode()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.Full };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);
        using var tx = txManager.BeginTransaction();

        var row = new Object?[] { 1, "Alice", 25 };
        table.Insert(tx, row);

        tx.Commit();

        // 验证 WAL 文件存在（平铺在数据库目录下）
        var walPath = Path.Combine(_testDir, "users.wal");
        Assert.True(File.Exists(walPath));
    }

    [Fact(DisplayName = "测试冷热分离索引管理器集成")]
    public void TestHotIndexManagerIntegration()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        // 验证 HotIndexManager 已初始化
        Assert.NotNull(table.HotIndex);

        // 插入数据
        using var tx1 = txManager.BeginTransaction();
        table.Insert(tx1, [1, "Alice", 25]);
        tx1.Commit();

        // 读取触发热度追踪
        using var tx2 = txManager.BeginTransaction();
        var row = table.Get(tx2, 1);
        Assert.NotNull(row);
        tx2.Commit();
    }

    [Fact(DisplayName = "测试分片管理器集成")]
    public void TestShardManagerIntegration()
    {
        var schema = CreateTestSchema();
        var options = new DbOptions { Path = _testDir, WalMode = WalMode.None };
        var txManager = new TransactionManager();

        using var table = new NovaTable(schema, _testDir, options, txManager);

        // 验证 ShardManager 已初始化，且有默认分片
        Assert.NotNull(table.Shards);
        Assert.Equal(1, table.Shards.ShardCount);

        // 插入数据后分片统计应更新
        using var tx = txManager.BeginTransaction();
        table.Insert(tx, [1, "Alice", 25]);
        table.Insert(tx, [2, "Bob", 30]);
        tx.Commit();

        var writeShard = table.Shards.GetWriteShard();
        Assert.NotNull(writeShard);
        Assert.Equal(2, writeShard!.RowCount);
        Assert.True(writeShard.SizeBytes > 0);
    }
}

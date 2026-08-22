using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class TableFileManagerTests : IDisposable
{
    private readonly String _testPath;
    private readonly DbOptions _options;

    public TableFileManagerTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"NovaTest_{Guid.NewGuid()}");
        _options = new DbOptions
        {
            Path = _testPath,
            PageSize = 4096
        };

        Directory.CreateDirectory(_testPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }

    #region 构造函数
    [Fact(DisplayName = "测试数据库路径为空的构造异常")]
    public void TestConstructorNullDatabasePath()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(null!, "Users", _options));
    }

    [Fact(DisplayName = "测试表名为空的构造异常")]
    public void TestConstructorNullTableName()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(_testPath, null!, _options));
    }

    [Fact(DisplayName = "测试空表名构造抛出异常")]
    public void TestConstructorEmptyTableName()
    {
        Assert.Throws<ArgumentException>(() => new TableFileManager(_testPath, "", _options));
        Assert.Throws<ArgumentException>(() => new TableFileManager(_testPath, "  ", _options));
    }

    [Fact(DisplayName = "测试选项为空的构造异常")]
    public void TestConstructorNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(_testPath, "Users", null!));
    }

    [Fact(DisplayName = "测试构造函数属性赋值")]
    public void TestConstructorProperties()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);
        Assert.Equal(_testPath, manager.DatabasePath);
        Assert.Equal("Products", manager.TableName);
    }
    #endregion

    #region 路径生成
    [Fact(DisplayName = "测试数据文件路径生成")]
    public void TestGetDataFilePath()
    {
        var manager = new TableFileManager(_testPath, "Users", _options);

        var path1 = manager.GetDataFilePath();
        Assert.Equal(Path.Combine(_testPath, "Users.data"), path1);

        var path2 = manager.GetDataFilePath(0);
        Assert.Equal(Path.Combine(_testPath, "Users_0.data"), path2);

        var path3 = manager.GetDataFilePath(5);
        Assert.Equal(Path.Combine(_testPath, "Users_5.data"), path3);
    }

    [Fact(DisplayName = "测试主索引文件路径生成")]
    public void TestGetPrimaryIndexFilePath()
    {
        var manager = new TableFileManager(_testPath, "Orders", _options);

        var path1 = manager.GetPrimaryIndexFilePath();
        Assert.Equal(Path.Combine(_testPath, "Orders.idx"), path1);

        var path2 = manager.GetPrimaryIndexFilePath(0);
        Assert.Equal(Path.Combine(_testPath, "Orders_0.idx"), path2);
    }

    [Fact(DisplayName = "测试二级索引文件路径生成")]
    public void TestGetSecondaryIndexFilePath()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);

        var path1 = manager.GetSecondaryIndexFilePath("idx_name");
        Assert.Equal(Path.Combine(_testPath, "Products_idx_name.idx"), path1);

        var path2 = manager.GetSecondaryIndexFilePath("idx_category", 2);
        Assert.Equal(Path.Combine(_testPath, "Products_2_idx_category.idx"), path2);
    }

    [Fact(DisplayName = "测试空索引名路径生成抛出异常")]
    public void TestGetSecondaryIndexFilePathEmptyName()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);
        Assert.Throws<ArgumentException>(() => manager.GetSecondaryIndexFilePath(""));
        Assert.Throws<ArgumentException>(() => manager.GetSecondaryIndexFilePath("  "));
    }

    [Fact(DisplayName = "测试 WAL 文件路径生成")]
    public void TestGetWalFilePath()
    {
        var manager = new TableFileManager(_testPath, "Logs", _options);

        var path1 = manager.GetWalFilePath();
        Assert.Equal(Path.Combine(_testPath, "Logs.wal"), path1);

        var path2 = manager.GetWalFilePath(1);
        Assert.Equal(Path.Combine(_testPath, "Logs_1.wal"), path2);
    }
    #endregion

    #region ListDataShards
    [Fact(DisplayName = "测试列出数据分片")]
    public void TestListDataShards()
    {
        var manager = new TableFileManager(_testPath, "BigTable", _options);

        File.WriteAllText(manager.GetDataFilePath(0), "");
        File.WriteAllText(manager.GetDataFilePath(2), "");
        File.WriteAllText(manager.GetDataFilePath(5), "");

        var shards = manager.ListDataShards().ToList();

        Assert.Equal(3, shards.Count);
        Assert.Equal(0, shards[0]);
        Assert.Equal(2, shards[1]);
        Assert.Equal(5, shards[2]);
    }

    [Fact(DisplayName = "测试无数据分片时返回空列表")]
    public void TestListDataShardsEmpty()
    {
        var manager = new TableFileManager(_testPath, "EmptyTable", _options);
        var shards = manager.ListDataShards().ToList();
        Assert.Empty(shards);
    }

    [Fact(DisplayName = "测试目录不存在时列出分片返回空")]
    public void TestListDataShardsDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        var shards = manager.ListDataShards().ToList();
        Assert.Empty(shards);
    }
    #endregion

    #region ListSecondaryIndexes
    [Fact(DisplayName = "测试列出二级索引")]
    public void TestListSecondaryIndexes()
    {
        var manager = new TableFileManager(_testPath, "Users", _options);

        // 主键索引（应被忽略，因为不含 _ 分隔的索引名）
        File.WriteAllText(manager.GetPrimaryIndexFilePath(), "");

        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_email"), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_name"), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_age", 1), "");

        var indexes = manager.ListSecondaryIndexes().ToList();

        Assert.Equal(3, indexes.Count);
        Assert.Contains("idx_age", indexes);
        Assert.Contains("idx_email", indexes);
        Assert.Contains("idx_name", indexes);
    }

    [Fact(DisplayName = "测试无二级索引时返回空列表")]
    public void TestListSecondaryIndexesEmpty()
    {
        var manager = new TableFileManager(_testPath, "NoIndex", _options);
        var indexes = manager.ListSecondaryIndexes().ToList();
        Assert.Empty(indexes);
    }

    [Fact(DisplayName = "测试目录不存在时列出索引返回空")]
    public void TestListSecondaryIndexesDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        var indexes = manager.ListSecondaryIndexes().ToList();
        Assert.Empty(indexes);
    }

    [Fact(DisplayName = "测试二级索引跨分片去重")]
    public void TestListSecondaryIndexesDeduplication()
    {
        var manager = new TableFileManager(_testPath, "Multi", _options);

        // 同一个索引在不同分片存在
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_status", 0), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_status", 1), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_status", 2), "");

        var indexes = manager.ListSecondaryIndexes().ToList();
        Assert.Single(indexes); // 去重后只有一个
        Assert.Contains("idx_status", indexes);
    }
    #endregion

    #region DeleteAllFiles
    [Fact(DisplayName = "测试删除表全部文件")]
    public void TestDeleteAllFiles()
    {
        var manager = new TableFileManager(_testPath, "TempTable", _options);

        File.WriteAllText(manager.GetDataFilePath(), "");
        File.WriteAllText(manager.GetDataFilePath(0), "");
        File.WriteAllText(manager.GetPrimaryIndexFilePath(), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_test"), "");
        File.WriteAllText(manager.GetWalFilePath(), "");
        File.WriteAllText(manager.GetWalFilePath(0), "");

        manager.DeleteAllFiles();

        Assert.False(File.Exists(manager.GetDataFilePath()));
        Assert.False(File.Exists(manager.GetDataFilePath(0)));
        Assert.False(File.Exists(manager.GetPrimaryIndexFilePath()));
        Assert.False(File.Exists(manager.GetSecondaryIndexFilePath("idx_test")));
        Assert.False(File.Exists(manager.GetWalFilePath()));
        Assert.False(File.Exists(manager.GetWalFilePath(0)));
    }

    [Fact(DisplayName = "测试无文件时删除不抛异常")]
    public void TestDeleteAllFilesNoFiles()
    {
        var manager = new TableFileManager(_testPath, "Empty", _options);
        manager.DeleteAllFiles(); // 不应抛异常
    }

    [Fact(DisplayName = "测试目录不存在时删除不抛异常")]
    public void TestDeleteAllFilesDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        manager.DeleteAllFiles(); // 不应抛异常
    }

    [Fact(DisplayName = "测试删除不影响其他表文件")]
    public void TestDeleteAllFilesDoesNotAffectOtherTables()
    {
        var manager1 = new TableFileManager(_testPath, "TableA", _options);
        var manager2 = new TableFileManager(_testPath, "TableB", _options);

        File.WriteAllText(manager1.GetDataFilePath(), "");
        File.WriteAllText(manager2.GetDataFilePath(), "");

        manager1.DeleteAllFiles();

        Assert.False(File.Exists(manager1.GetDataFilePath()));
        Assert.True(File.Exists(manager2.GetDataFilePath())); // 另一张表不受影响
    }
    #endregion

    #region Exists
    [Fact(DisplayName = "测试表是否存在判断")]
    public void TestExists()
    {
        var manager = new TableFileManager(_testPath, "CheckTable", _options);

        Assert.False(manager.Exists());

        File.WriteAllText(manager.GetDataFilePath(), "");

        Assert.True(manager.Exists());
    }

    [Fact(DisplayName = "测试目录不存在时表不存在")]
    public void TestExistsDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        Assert.False(manager.Exists());
    }
    #endregion

    #region 分片文件综合
    [Fact(DisplayName = "测试分片文件命名与枚举")]
    public void TestShardedFileNaming()
    {
        var manager = new TableFileManager(_testPath, "ShardedTable", _options);

        File.WriteAllText(manager.GetDataFilePath(0), "");
        File.WriteAllText(manager.GetDataFilePath(1), "");
        File.WriteAllText(manager.GetPrimaryIndexFilePath(0), "");
        File.WriteAllText(manager.GetSecondaryIndexFilePath("idx_status", 0), "");
        File.WriteAllText(manager.GetWalFilePath(0), "");

        var shards = manager.ListDataShards().ToList();
        var indexes = manager.ListSecondaryIndexes().ToList();

        Assert.Equal(2, shards.Count);
        Assert.Contains(0, shards);
        Assert.Contains(1, shards);

        Assert.Single(indexes);
        Assert.Contains("idx_status", indexes);
    }
    #endregion
}

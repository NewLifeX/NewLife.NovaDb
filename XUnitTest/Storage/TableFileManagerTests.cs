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
    [Fact]
    public void TestConstructorNullDatabasePath()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(null!, "Users", _options));
    }

    [Fact]
    public void TestConstructorNullTableName()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(_testPath, null!, _options));
    }

    [Fact]
    public void TestConstructorEmptyTableName()
    {
        Assert.Throws<ArgumentException>(() => new TableFileManager(_testPath, "", _options));
        Assert.Throws<ArgumentException>(() => new TableFileManager(_testPath, "  ", _options));
    }

    [Fact]
    public void TestConstructorNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new TableFileManager(_testPath, "Users", null!));
    }

    [Fact]
    public void TestConstructorProperties()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);
        Assert.Equal(_testPath, manager.DatabasePath);
        Assert.Equal("Products", manager.TableName);
    }
    #endregion

    #region 路径生成
    [Fact]
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

    [Fact]
    public void TestGetPrimaryIndexFilePath()
    {
        var manager = new TableFileManager(_testPath, "Orders", _options);

        var path1 = manager.GetPrimaryIndexFilePath();
        Assert.Equal(Path.Combine(_testPath, "Orders.idx"), path1);

        var path2 = manager.GetPrimaryIndexFilePath(0);
        Assert.Equal(Path.Combine(_testPath, "Orders_0.idx"), path2);
    }

    [Fact]
    public void TestGetSecondaryIndexFilePath()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);

        var path1 = manager.GetSecondaryIndexFilePath("idx_name");
        Assert.Equal(Path.Combine(_testPath, "Products_idx_name.idx"), path1);

        var path2 = manager.GetSecondaryIndexFilePath("idx_category", 2);
        Assert.Equal(Path.Combine(_testPath, "Products_2_idx_category.idx"), path2);
    }

    [Fact]
    public void TestGetSecondaryIndexFilePathEmptyName()
    {
        var manager = new TableFileManager(_testPath, "Products", _options);
        Assert.Throws<ArgumentException>(() => manager.GetSecondaryIndexFilePath(""));
        Assert.Throws<ArgumentException>(() => manager.GetSecondaryIndexFilePath("  "));
    }

    [Fact]
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
    [Fact]
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

    [Fact]
    public void TestListDataShardsEmpty()
    {
        var manager = new TableFileManager(_testPath, "EmptyTable", _options);
        var shards = manager.ListDataShards().ToList();
        Assert.Empty(shards);
    }

    [Fact]
    public void TestListDataShardsDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        var shards = manager.ListDataShards().ToList();
        Assert.Empty(shards);
    }
    #endregion

    #region ListSecondaryIndexes
    [Fact]
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

    [Fact]
    public void TestListSecondaryIndexesEmpty()
    {
        var manager = new TableFileManager(_testPath, "NoIndex", _options);
        var indexes = manager.ListSecondaryIndexes().ToList();
        Assert.Empty(indexes);
    }

    [Fact]
    public void TestListSecondaryIndexesDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        var indexes = manager.ListSecondaryIndexes().ToList();
        Assert.Empty(indexes);
    }

    [Fact]
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
    [Fact]
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

    [Fact]
    public void TestDeleteAllFilesNoFiles()
    {
        var manager = new TableFileManager(_testPath, "Empty", _options);
        manager.DeleteAllFiles(); // 不应抛异常
    }

    [Fact]
    public void TestDeleteAllFilesDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        manager.DeleteAllFiles(); // 不应抛异常
    }

    [Fact]
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
    [Fact]
    public void TestExists()
    {
        var manager = new TableFileManager(_testPath, "CheckTable", _options);

        Assert.False(manager.Exists());

        File.WriteAllText(manager.GetDataFilePath(), "");

        Assert.True(manager.Exists());
    }

    [Fact]
    public void TestExistsDirectoryNotExists()
    {
        var manager = new TableFileManager("/nonexistent/path", "Users", _options);
        Assert.False(manager.Exists());
    }
    #endregion

    #region 分片文件综合
    [Fact]
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

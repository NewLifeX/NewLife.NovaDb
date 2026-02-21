using System;
using System.IO;
using System.Linq;
using NewLife;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class DatabaseManagerTests : IDisposable
{
    private readonly String _testPath;
    private readonly String _externalPath;
    private readonly DbOptions _options;

    public DatabaseManagerTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"NovaTest_{Guid.NewGuid()}");
        _externalPath = Path.Combine(Path.GetTempPath(), $"NovaExternal_{Guid.NewGuid()}");
        _options = new DbOptions
        {
            Path = _testPath,
            PageSize = 4096
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
            Directory.Delete(_testPath, true);

        if (Directory.Exists(_externalPath))
            Directory.Delete(_externalPath, true);
    }

    #region 构造函数
    [Fact]
    public void TestConstructorNullBasePath()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseManager(null!, _options));
    }

    [Fact]
    public void TestConstructorNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseManager(_testPath, null!));
    }

    [Fact]
    public void TestConstructorEmptyBasePath()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseManager("", _options));
        Assert.Throws<ArgumentException>(() => new DatabaseManager("  ", _options));
    }
    #endregion

    #region Initialize
    [Fact]
    public void TestInitializeCreatesSystemDatabase()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        // 验证系统库目录和元数据文件已创建
        var systemPath = Path.Combine(_testPath, DatabaseManager.SystemDatabaseName);
        Assert.True(Directory.Exists(systemPath));
        Assert.True(File.Exists(Path.Combine(systemPath, "nova.db")));
        Assert.NotNull(manager.SystemDatabase);
    }

    [Fact]
    public void TestInitializeTwiceDoesNotThrow()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();
        manager.Initialize(); // 第二次应打开而非重新创建
    }

    [Fact]
    public void TestInitializeVerifySystemDatabaseMetadata()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var systemMetaPath = Path.Combine(_testPath, DatabaseManager.SystemDatabaseName, "nova.db");
        var metaBytes = File.ReadAllBytes(systemMetaPath);
        var header = FileHeader.Read(new ArrayPacket(metaBytes));

        Assert.Equal(1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
    }
    #endregion

    #region 阶段一：扫描默认目录
    [Fact]
    public void TestScanDiscoversValidDatabases()
    {
        CreateFakeDatabase("TestDb1", _testPath);
        CreateFakeDatabase("TestDb2", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Equal(2, databases.Count);
        Assert.Contains(databases, d => d.Name == "TestDb1");
        Assert.Contains(databases, d => d.Name == "TestDb2");
    }

    [Fact]
    public void TestScanSkipsSystemDatabase()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListAllDatabases();
        Assert.DoesNotContain(databases, d => String.Equals(d.Name, DatabaseManager.SystemDatabaseName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestScanSkipsDirectoriesWithoutMetadata()
    {
        Directory.CreateDirectory(Path.Combine(_testPath, "NotADatabase"));
        CreateFakeDatabase("RealDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Single(databases);
        Assert.Equal("RealDb", databases[0].Name);
    }

    [Fact]
    public void TestScanSkipsCorruptedMetadata()
    {
        var corruptedPath = Path.Combine(_testPath, "CorruptedDb");
        Directory.CreateDirectory(corruptedPath);
        File.WriteAllBytes(Path.Combine(corruptedPath, "nova.db"), new Byte[32]);

        CreateFakeDatabase("GoodDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Single(databases);
        Assert.Equal("GoodDb", databases[0].Name);
    }

    [Fact]
    public void TestScanSkipsTruncatedMetadata()
    {
        var shortPath = Path.Combine(_testPath, "ShortDb");
        Directory.CreateDirectory(shortPath);
        File.WriteAllBytes(Path.Combine(shortPath, "nova.db"), new Byte[10]);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Empty(databases);
    }

    [Fact]
    public void TestScanDatabaseInfoContainsCorrectData()
    {
        CreateFakeDatabase("InfoDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var info = manager.GetDatabase("InfoDb");
        Assert.NotNull(info);
        Assert.Equal("InfoDb", info.Name);
        Assert.Equal(Path.Combine(_testPath, "InfoDb"), info.Path);
        Assert.Equal(DatabaseStatus.Online, info.Status);
        Assert.False(info.IsExternal);
        Assert.Equal(1, info.Version);
        Assert.Equal(4096u, info.PageSize);
        Assert.True(info.CreateTime.Year >= 2020);
        Assert.True(info.LastSeenAt > 0);
    }

    [Fact]
    public void TestScanEmptyBaseDirectory()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Empty(databases);
    }

    [Fact]
    public void TestScanDefaultDirectorySkipsAlreadyRegistered()
    {
        // 先创建两个数据库
        CreateFakeDatabase("Db1", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        // 第一次扫描后应有 1 个
        Assert.Single(manager.ListOnlineDatabases());

        // 手动创建第二个数据库
        CreateFakeDatabase("Db2", _testPath);

        // 再次扫描默认目录，仅发现新库
        manager.ScanDefaultDirectory();

        Assert.Equal(2, manager.ListOnlineDatabases().Count);
    }
    #endregion

    #region 阶段二：检查已登记数据库状态
    [Fact]
    public void TestCheckRegisteredDatabasesDetectsOffline()
    {
        CreateFakeDatabase("TempDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("TempDb")!.Status);

        // 删除数据库目录
        Directory.Delete(Path.Combine(_testPath, "TempDb"), true);

        // 阶段二：检查已登记数据库
        manager.CheckRegisteredDatabases();

        var info = manager.GetDatabase("TempDb");
        Assert.NotNull(info);
        Assert.Equal(DatabaseStatus.Offline, info.Status);
    }

    [Fact]
    public void TestCheckRegisteredDatabasesRestoresOnline()
    {
        CreateFakeDatabase("FlickerDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        // 删除 → Offline
        Directory.Delete(Path.Combine(_testPath, "FlickerDb"), true);
        manager.CheckRegisteredDatabases();
        Assert.Equal(DatabaseStatus.Offline, manager.GetDatabase("FlickerDb")!.Status);

        // 重新创建 → Online
        CreateFakeDatabase("FlickerDb", _testPath);
        manager.CheckRegisteredDatabases();
        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("FlickerDb")!.Status);
    }

    [Fact]
    public void TestCheckMultipleDatabasesStatusSync()
    {
        CreateFakeDatabase("Db1", _testPath);
        CreateFakeDatabase("Db2", _testPath);
        CreateFakeDatabase("Db3", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        // 删除 Db2
        Directory.Delete(Path.Combine(_testPath, "Db2"), true);
        manager.CheckRegisteredDatabases();

        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("Db1")!.Status);
        Assert.Equal(DatabaseStatus.Offline, manager.GetDatabase("Db2")!.Status);
        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("Db3")!.Status);
    }

    [Fact]
    public void TestCheckDetectsCorruptedMetadata()
    {
        CreateFakeDatabase("CorruptDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("CorruptDb")!.Status);

        // 篡改元数据文件使其损坏
        var metaPath = Path.Combine(_testPath, "CorruptDb", "nova.db");
        File.WriteAllBytes(metaPath, new Byte[32]);

        manager.CheckRegisteredDatabases();
        Assert.Equal(DatabaseStatus.Offline, manager.GetDatabase("CorruptDb")!.Status);
    }
    #endregion

    #region 外部目录数据库
    [Fact]
    public void TestRegisterExternalDatabase()
    {
        var externalDbPath = Path.Combine(_externalPath, "ExtDb");
        CreateFakeDatabase("ExtDb", _externalPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var info = manager.RegisterDatabase("ExtDb", externalDbPath);
        Assert.Equal("ExtDb", info.Name);
        Assert.Equal(DatabaseStatus.Online, info.Status);
        Assert.True(info.IsExternal);
        Assert.Equal(4096u, info.PageSize);
    }

    [Fact]
    public void TestRegisterDatabaseInsideBasePath()
    {
        // 在默认目录内的手动注册不算外部
        CreateFakeDatabase("InternalDb", _testPath);
        var dbPath = Path.Combine(_testPath, "InternalDb");

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        // ScanDefaultDirectory 已自动发现
        var info = manager.GetDatabase("InternalDb");
        Assert.NotNull(info);
        Assert.False(info.IsExternal);
    }

    [Fact]
    public void TestRegisterDuplicateThrows()
    {
        CreateFakeDatabase("DupDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var ex = Assert.Throws<NovaException>(() =>
            manager.RegisterDatabase("DupDb", Path.Combine(_testPath, "DupDb")));
        Assert.Equal(ErrorCode.DatabaseExists, ex.Code);
    }

    [Fact]
    public void TestRegisterNonExistentPathThrows()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var ex = Assert.Throws<NovaException>(() =>
            manager.RegisterDatabase("Ghost", Path.Combine(_externalPath, "Ghost")));
        Assert.Equal(ErrorCode.DatabaseNotFound, ex.Code);
    }

    [Fact]
    public void TestRegisterCorruptedMetadataThrows()
    {
        var corruptedPath = Path.Combine(_externalPath, "CorruptExt");
        Directory.CreateDirectory(corruptedPath);
        File.WriteAllBytes(Path.Combine(corruptedPath, "nova.db"), new Byte[32]);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var ex = Assert.Throws<NovaException>(() =>
            manager.RegisterDatabase("CorruptExt", corruptedPath));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
    }

    [Fact]
    public void TestExternalDatabaseStatusCheck()
    {
        var externalDbPath = Path.Combine(_externalPath, "ExtCheck");
        CreateFakeDatabase("ExtCheck", _externalPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();
        manager.RegisterDatabase("ExtCheck", externalDbPath);

        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("ExtCheck")!.Status);

        // 删除外部数据库
        Directory.Delete(externalDbPath, true);
        manager.CheckRegisteredDatabases();

        Assert.Equal(DatabaseStatus.Offline, manager.GetDatabase("ExtCheck")!.Status);

        // 恢复外部数据库
        CreateFakeDatabase("ExtCheck", _externalPath);
        manager.CheckRegisteredDatabases();

        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("ExtCheck")!.Status);
    }

    [Fact]
    public void TestExternalDatabasePersistence()
    {
        var externalDbPath = Path.Combine(_externalPath, "ExtPersist");
        CreateFakeDatabase("ExtPersist", _externalPath);

        var manager1 = new DatabaseManager(_testPath, _options);
        manager1.Initialize();
        manager1.RegisterDatabase("ExtPersist", externalDbPath);

        // 创建新管理器，验证外部数据库信息从目录文件中恢复
        var manager2 = new DatabaseManager(_testPath, _options);
        manager2.Initialize();

        var info = manager2.GetDatabase("ExtPersist");
        Assert.NotNull(info);
        Assert.Equal("ExtPersist", info.Name);
        Assert.True(info.IsExternal);
        Assert.Equal(DatabaseStatus.Online, info.Status);
    }

    [Fact]
    public void TestRegisterEmptyNameThrows()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.Throws<ArgumentException>(() => manager.RegisterDatabase("", "/some/path"));
        Assert.Throws<ArgumentException>(() => manager.RegisterDatabase("  ", "/some/path"));
    }

    [Fact]
    public void TestRegisterEmptyPathThrows()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.Throws<ArgumentException>(() => manager.RegisterDatabase("Db", ""));
        Assert.Throws<ArgumentException>(() => manager.RegisterDatabase("Db", "  "));
    }
    #endregion

    #region 两阶段启动流程
    [Fact]
    public void TestTwoPhaseStartupWithMixedDatabases()
    {
        // 默认目录内创建数据库
        CreateFakeDatabase("LocalDb", _testPath);

        // 外部目录创建数据库
        var externalDbPath = Path.Combine(_externalPath, "RemoteDb");
        CreateFakeDatabase("RemoteDb", _externalPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();
        manager.RegisterDatabase("RemoteDb", externalDbPath);

        // 两个库都在线
        Assert.Equal(2, manager.ListOnlineDatabases().Count);

        // 删除本地库
        Directory.Delete(Path.Combine(_testPath, "LocalDb"), true);

        // 重新执行两阶段启动
        manager.ScanDefaultDirectory();
        manager.CheckRegisteredDatabases();

        Assert.Equal(DatabaseStatus.Offline, manager.GetDatabase("LocalDb")!.Status);
        Assert.Equal(DatabaseStatus.Online, manager.GetDatabase("RemoteDb")!.Status);
    }

    [Fact]
    public void TestFullRestartWithCatalog()
    {
        // 默认目录 + 外部目录各一个
        CreateFakeDatabase("Local1", _testPath);
        var extPath = Path.Combine(_externalPath, "Ext1");
        CreateFakeDatabase("Ext1", _externalPath);

        var manager1 = new DatabaseManager(_testPath, _options);
        manager1.Initialize();
        manager1.RegisterDatabase("Ext1", extPath);

        // 模拟服务器重启
        var manager2 = new DatabaseManager(_testPath, _options);
        manager2.Initialize();

        // 验证两个库都被正确加载
        Assert.Equal(2, manager2.ListOnlineDatabases().Count);

        var local = manager2.GetDatabase("Local1");
        Assert.NotNull(local);
        Assert.False(local.IsExternal);

        var ext = manager2.GetDatabase("Ext1");
        Assert.NotNull(ext);
        Assert.True(ext.IsExternal);
    }

    [Fact]
    public void TestRestartDetectsNewDatabaseInDefaultDir()
    {
        var manager1 = new DatabaseManager(_testPath, _options);
        manager1.Initialize();
        Assert.Empty(manager1.ListOnlineDatabases());

        // 外部工具在默认目录下创建了新库
        CreateFakeDatabase("NewDb", _testPath);

        // 模拟重启
        var manager2 = new DatabaseManager(_testPath, _options);
        manager2.Initialize();

        Assert.Single(manager2.ListOnlineDatabases());
        Assert.Equal("NewDb", manager2.ListOnlineDatabases()[0].Name);
    }
    #endregion

    #region 目录持久化
    [Fact]
    public void TestCatalogPersistence()
    {
        CreateFakeDatabase("PersistDb", _testPath);

        var manager1 = new DatabaseManager(_testPath, _options);
        manager1.Initialize();

        var catalogPath = Path.Combine(_testPath, DatabaseManager.SystemDatabaseName, "databases.json");
        Assert.True(File.Exists(catalogPath));

        var manager2 = new DatabaseManager(_testPath, _options);
        manager2.Initialize();

        var info = manager2.GetDatabase("PersistDb");
        Assert.NotNull(info);
        Assert.Equal("PersistDb", info.Name);
        Assert.Equal(DatabaseStatus.Online, info.Status);
    }

    [Fact]
    public void TestCatalogPersistenceWithOfflineDatabase()
    {
        CreateFakeDatabase("WillGoOfflineDb", _testPath);

        var manager1 = new DatabaseManager(_testPath, _options);
        manager1.Initialize();

        // 删除数据库目录
        Directory.Delete(Path.Combine(_testPath, "WillGoOfflineDb"), true);

        // 重新初始化：阶段二会检测到已登记数据库离线，并持久化到目录文件
        var manager2 = new DatabaseManager(_testPath, _options);
        manager2.Initialize();

        var info = manager2.GetDatabase("WillGoOfflineDb");
        Assert.NotNull(info);
        Assert.Equal(DatabaseStatus.Offline, info.Status);
    }
    #endregion

    #region GetDatabase
    [Fact]
    public void TestGetDatabaseReturnsNull()
    {
        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.Null(manager.GetDatabase("NonExistent"));
        Assert.Null(manager.GetDatabase(""));
        Assert.Null(manager.GetDatabase("  "));
    }

    [Fact]
    public void TestGetDatabaseCaseInsensitive()
    {
        CreateFakeDatabase("CaseTestDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Assert.NotNull(manager.GetDatabase("casetestdb"));
        Assert.NotNull(manager.GetDatabase("CASETESTDB"));
        Assert.NotNull(manager.GetDatabase("CaseTestDb"));
    }
    #endregion

    #region ListDatabases
    [Fact]
    public void TestListOnlineDatabasesSorted()
    {
        CreateFakeDatabase("Zebra", _testPath);
        CreateFakeDatabase("Alpha", _testPath);
        CreateFakeDatabase("Middle", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        var databases = manager.ListOnlineDatabases();
        Assert.Equal(3, databases.Count);
        Assert.Equal("Alpha", databases[0].Name);
        Assert.Equal("Middle", databases[1].Name);
        Assert.Equal("Zebra", databases[2].Name);
    }

    [Fact]
    public void TestListAllDatabasesIncludesOffline()
    {
        CreateFakeDatabase("OnlineDb", _testPath);
        CreateFakeDatabase("WillBeOfflineDb", _testPath);

        var manager = new DatabaseManager(_testPath, _options);
        manager.Initialize();

        Directory.Delete(Path.Combine(_testPath, "WillBeOfflineDb"), true);
        manager.CheckRegisteredDatabases();

        var allDatabases = manager.ListAllDatabases();
        Assert.Equal(2, allDatabases.Count);

        var onlineDatabases = manager.ListOnlineDatabases();
        Assert.Single(onlineDatabases);
        Assert.Equal("OnlineDb", onlineDatabases[0].Name);
    }
    #endregion

    #region 辅助方法
    /// <summary>在指定父目录下创建一个具有有效 FileHeader 的模拟数据库目录</summary>
    /// <param name="name">数据库名称（子目录名）</param>
    /// <param name="parentPath">父目录路径</param>
    private void CreateFakeDatabase(String name, String parentPath)
    {
        var dbPath = Path.Combine(parentPath, name);
        var dbOptions = new DbOptions { Path = dbPath, PageSize = 4096 };
        var dbDir = new DatabaseDirectory(dbPath, dbOptions);
        dbDir.Create();
    }
    #endregion
}

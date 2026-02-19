using System;
using System.IO;
using System.Linq;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class DatabaseDirectoryTests : IDisposable
{
    private readonly String _testPath;
    private readonly DbOptions _options;

    public DatabaseDirectoryTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"NovaTest_{Guid.NewGuid()}");
        _options = new DbOptions
        {
            Path = _testPath,
            PageSize = 4096
        };
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
    public void TestConstructorNullPath()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseDirectory(null!, _options));
    }

    [Fact]
    public void TestConstructorNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseDirectory(_testPath, null!));
    }

    [Fact]
    public void TestConstructorEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseDirectory("", _options));
        Assert.Throws<ArgumentException>(() => new DatabaseDirectory("  ", _options));
    }
    #endregion

    #region Create
    [Fact]
    public void TestCreateDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.True(Directory.Exists(_testPath));
        Assert.True(File.Exists(Path.Combine(_testPath, "nova.db")));
    }

    [Fact]
    public void TestCreateDatabaseVerifyMetadata()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 验证元数据文件内容
        var metaBytes = File.ReadAllBytes(Path.Combine(_testPath, "nova.db"));
        Assert.Equal(FileHeader.HeaderSize, metaBytes.Length);

        var header = FileHeader.Read(new ArrayPacket(metaBytes));
        Assert.Equal(1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
        Assert.True(header.CreatedAt > 0);
    }

    [Fact]
    public void TestCreateDatabaseAlreadyExists()
    {
        Directory.CreateDirectory(_testPath);
        var metaPath = Path.Combine(_testPath, "nova.db");
        File.WriteAllBytes(metaPath, new Byte[] { 1 });

        var db = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db.Create());
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void TestCreateDatabaseWithExistingDirectory()
    {
        Directory.CreateDirectory(_testPath);
        File.WriteAllText(Path.Combine(_testPath, "readme.txt"), "data");

        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var metaPath = Path.Combine(_testPath, "nova.db");
        Assert.True(File.Exists(metaPath));
        Assert.Equal(FileHeader.HeaderSize, new FileInfo(metaPath).Length);
    }

    [Fact]
    public void TestCreateDatabaseWithEmptyMetadata()
    {
        Directory.CreateDirectory(_testPath);
        var metaPath = Path.Combine(_testPath, "nova.db");
        File.WriteAllBytes(metaPath, Array.Empty<Byte>());

        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.Equal(FileHeader.HeaderSize, new FileInfo(metaPath).Length);
    }
    #endregion

    #region Open
    [Fact]
    public void TestOpenDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var db2 = new DatabaseDirectory(_testPath, _options);
        db2.Open(); // Should not throw
    }

    [Fact]
    public void TestOpenDatabaseNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        var ex = Assert.Throws<NovaException>(() => db.Open());
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void TestOpenDatabaseMissingMetadata()
    {
        // 创建目录但不写入元数据
        Directory.CreateDirectory(_testPath);

        var db = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db.Open());
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("metadata file not found", ex.Message);
    }

    [Fact]
    public void TestOpenDatabaseCorruptedMetadata()
    {
        // 创建目录并写入损坏的元数据
        Directory.CreateDirectory(_testPath);
        var metaPath = Path.Combine(_testPath, "nova.db");
        File.WriteAllBytes(metaPath, new Byte[32]); // 全零，魔数不对

        var db = new DatabaseDirectory(_testPath, _options);
        Assert.Throws<NovaException>(() => db.Open());
    }

    [Fact]
    public void TestOpenDatabaseUnsupportedVersion()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 篡改版本号为 99
        var metaPath = Path.Combine(_testPath, "nova.db");
        var metaBytes = File.ReadAllBytes(metaPath);
        BitConverter.GetBytes((UInt16)99).CopyTo(metaBytes, 4);
        File.WriteAllBytes(metaPath, metaBytes);

        var db2 = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db2.Open());
        Assert.Equal(ErrorCode.IncompatibleFileFormat, ex.Code);
        Assert.Contains("Unsupported database version", ex.Message);
    }

    [Fact]
    public void TestOpenDatabaseTruncatedMetadata()
    {
        // 创建目录并写入过短的元数据
        Directory.CreateDirectory(_testPath);
        var metaPath = Path.Combine(_testPath, "nova.db");
        File.WriteAllBytes(metaPath, new Byte[10]); // 只有 10 字节

        var db = new DatabaseDirectory(_testPath, _options);
        Assert.ThrowsAny<Exception>(() => db.Open()); // FileHeader.Read 拒绝短 buffer
    }
    #endregion

    #region ListTables
    [Fact]
    public void TestListTables()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 创建表文件（模拟表的存在）
        var table1 = db.GetTableFileManager("Users");
        File.WriteAllText(table1.GetDataFilePath(), "");

        var table2 = db.GetTableFileManager("Orders");
        File.WriteAllText(table2.GetDataFilePath(), "");

        var tables = db.ListTables().ToList();

        Assert.Equal(2, tables.Count);
        Assert.Contains("Users", tables);
        Assert.Contains("Orders", tables);
    }

    [Fact]
    public void TestListTablesEmpty()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var tables = db.ListTables().ToList();
        Assert.Empty(tables);
    }

    [Fact]
    public void TestListTablesExcludesSystemTables()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 创建系统表文件
        File.WriteAllText(Path.Combine(_testPath, "_sys_tables.data"), "");
        File.WriteAllText(Path.Combine(_testPath, "_sys_columns.data"), "");

        // 创建用户表
        File.WriteAllText(Path.Combine(_testPath, "Users.data"), "");

        var tables = db.ListTables().ToList();
        Assert.Single(tables);
        Assert.Contains("Users", tables);
    }

    [Fact]
    public void TestListTablesWithShardFiles()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 同一张表有多个分片文件
        File.WriteAllText(Path.Combine(_testPath, "BigTable.data"), "");
        File.WriteAllText(Path.Combine(_testPath, "BigTable_0.data"), "");
        File.WriteAllText(Path.Combine(_testPath, "BigTable_1.data"), "");

        var tables = db.ListTables().ToList();
        Assert.Single(tables); // 仍然只显示一张表
        Assert.Contains("BigTable", tables);
    }

    [Fact]
    public void TestListTablesDirectoryNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        // 目录不存在时返回空
        var tables = db.ListTables().ToList();
        Assert.Empty(tables);
    }

    [Fact]
    public void TestListTablesReturnsSorted()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        File.WriteAllText(Path.Combine(_testPath, "Zebra.data"), "");
        File.WriteAllText(Path.Combine(_testPath, "Alpha.data"), "");
        File.WriteAllText(Path.Combine(_testPath, "Middle.data"), "");

        var tables = db.ListTables().ToList();
        Assert.Equal(3, tables.Count);
        Assert.Equal("Alpha", tables[0]);
        Assert.Equal("Middle", tables[1]);
        Assert.Equal("Zebra", tables[2]);
    }
    #endregion

    #region GetTableFileManager
    [Fact]
    public void TestGetTableFileManager()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var manager = db.GetTableFileManager("Products");
        Assert.Equal("Products", manager.TableName);
        Assert.Equal(_testPath, manager.DatabasePath);
    }

    [Fact]
    public void TestGetTableFileManagerEmptyName()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        Assert.Throws<ArgumentException>(() => db.GetTableFileManager(""));
        Assert.Throws<ArgumentException>(() => db.GetTableFileManager("  "));
    }
    #endregion

    #region Drop
    [Fact]
    public void TestDropDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.True(Directory.Exists(_testPath));

        db.Drop();

        Assert.False(Directory.Exists(_testPath));
    }

    [Fact]
    public void TestDropDatabaseNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        // 删除不存在的库不应抛异常
        db.Drop();
    }

    [Fact]
    public void TestDropDatabaseWithFiles()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 添加一些文件
        File.WriteAllText(Path.Combine(_testPath, "Users.data"), "test");
        File.WriteAllText(Path.Combine(_testPath, "Users.idx"), "test");
        File.WriteAllText(Path.Combine(_testPath, "Users.wal"), "test");

        db.Drop();

        Assert.False(Directory.Exists(_testPath));
    }
    #endregion
}

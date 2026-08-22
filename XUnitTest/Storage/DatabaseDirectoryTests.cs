using System;
using System.IO;
using System.Linq;
using NewLife;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.Security;
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
    [Fact(DisplayName = "构造函数路径为 null 时抛异常")]
    public void TestConstructorNullPath()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseDirectory(null!, _options));
    }

    [Fact(DisplayName = "构造函数选项为 null 时抛异常")]
    public void TestConstructorNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseDirectory(_testPath, null!));
    }

    [Fact(DisplayName = "构造函数路径为空字符串时抛异常")]
    public void TestConstructorEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseDirectory("", _options));
        Assert.Throws<ArgumentException>(() => new DatabaseDirectory("  ", _options));
    }
    #endregion

    #region Create
    [Fact(DisplayName = "创建数据库并生成目录与元数据文件")]
    public void TestCreateDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.True(Directory.Exists(_testPath));
        Assert.True(_testPath.CombinePath("nova.db").AsFile().Exists);
    }

    [Fact(DisplayName = "创建数据库并验证元数据文件内容")]
    public void TestCreateDatabaseVerifyMetadata()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 验证元数据文件内容
        var metaBytes = File.ReadAllBytes(_testPath.CombinePath("nova.db"));
        Assert.Equal(FileHeader.HeaderSize, metaBytes.Length);

        var header = FileHeader.Read(new ArrayPacket(metaBytes));
        Assert.Equal(1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
        Assert.True(header.CreateTime.Year >= 2020);
    }

    [Fact(DisplayName = "创建已存在数据库时抛异常")]
    public void TestCreateDatabaseAlreadyExists()
    {
        Directory.CreateDirectory(_testPath);
        var metaFile = _testPath.CombinePath("nova.db").AsFile();
        File.WriteAllBytes(metaFile.FullName, [1]);

        var db = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db.Create());
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact(DisplayName = "在已有目录中创建数据库成功")]
    public void TestCreateDatabaseWithExistingDirectory()
    {
        Directory.CreateDirectory(_testPath);
        File.WriteAllText(Path.Combine(_testPath, "readme.txt"), "data");

        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var metaFile = _testPath.CombinePath("nova.db").AsFile();
        Assert.True(metaFile.Exists);
        Assert.Equal(FileHeader.HeaderSize, metaFile.Length);
    }

    [Fact(DisplayName = "元数据文件为空时创建数据库成功")]
    public void TestCreateDatabaseWithEmptyMetadata()
    {
        Directory.CreateDirectory(_testPath);
        var metaFile = _testPath.CombinePath("nova.db").AsFile();
        File.WriteAllBytes(metaFile.FullName, Array.Empty<Byte>());

        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.Equal(FileHeader.HeaderSize, metaFile.Length);
    }
    #endregion

    #region Open
    [Fact(DisplayName = "打开数据库成功")]
    public void TestOpenDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var db2 = new DatabaseDirectory(_testPath, _options);
        db2.Open(); // Should not throw
    }

    [Fact(DisplayName = "打开不存在的数据库抛异常")]
    public void TestOpenDatabaseNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        var ex = Assert.Throws<NovaException>(() => db.Open());
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact(DisplayName = "打开缺少元数据文件的数据库抛异常")]
    public void TestOpenDatabaseMissingMetadata()
    {
        // 创建目录但不写入元数据
        Directory.CreateDirectory(_testPath);

        var db = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db.Open());
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
        Assert.Contains("metadata file not found", ex.Message);
    }

    [Fact(DisplayName = "打开元数据损坏的数据库抛异常")]
    public void TestOpenDatabaseCorruptedMetadata()
    {
        // 创建目录并写入损坏的元数据
        Directory.CreateDirectory(_testPath);
        var metaPath = Path.Combine(_testPath, "nova.db");
        File.WriteAllBytes(metaPath, new Byte[32]); // 全零，魔数不对

        var db = new DatabaseDirectory(_testPath, _options);
        Assert.Throws<NovaException>(() => db.Open());
    }

    [Fact(DisplayName = "打开不支持版本的数据库抛异常")]
    public void TestOpenDatabaseUnsupportedVersion()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        // 篡改版本号为 99（offset 4，1 字节）并重新计算 Checksum
        var metaPath = Path.Combine(_testPath, "nova.db");
        var metaBytes = File.ReadAllBytes(metaPath);
        metaBytes[4] = 99;
        BitConverter.GetBytes(Crc32.Compute(metaBytes.AsSpan(0, 28))).CopyTo(metaBytes, 28);
        File.WriteAllBytes(metaPath, metaBytes);

        var db2 = new DatabaseDirectory(_testPath, _options);
        var ex = Assert.Throws<NovaException>(() => db2.Open());
        Assert.Equal(ErrorCode.IncompatibleFileFormat, ex.Code);
        Assert.Contains("Unsupported database version", ex.Message);
    }

    [Fact(DisplayName = "打开元数据截断的数据库抛异常")]
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
    [Fact(DisplayName = "列出数据库中的数据表")]
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

    [Fact(DisplayName = "空数据库列出数据表为空")]
    public void TestListTablesEmpty()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var tables = db.ListTables().ToList();
        Assert.Empty(tables);
    }

    [Fact(DisplayName = "列出数据表排除系统表")]
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

    [Fact(DisplayName = "列出数据表时合并分片文件")]
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

    [Fact(DisplayName = "目录不存在时列出数据表返回空")]
    public void TestListTablesDirectoryNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        // 目录不存在时返回空
        var tables = db.ListTables().ToList();
        Assert.Empty(tables);
    }

    [Fact(DisplayName = "列出数据表按名称排序")]
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
    [Fact(DisplayName = "获取表文件管理器")]
    public void TestGetTableFileManager()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var manager = db.GetTableFileManager("Products");
        Assert.Equal("Products", manager.TableName);
        Assert.Equal(_testPath, manager.DatabasePath);
    }

    [Fact(DisplayName = "获取空名称的表文件管理器抛异常")]
    public void TestGetTableFileManagerEmptyName()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        Assert.Throws<ArgumentException>(() => db.GetTableFileManager(""));
        Assert.Throws<ArgumentException>(() => db.GetTableFileManager("  "));
    }
    #endregion

    #region Drop
    [Fact(DisplayName = "删除数据库目录")]
    public void TestDropDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.True(Directory.Exists(_testPath));

        db.Drop();

        Assert.False(Directory.Exists(_testPath));
    }

    [Fact(DisplayName = "删除不存在的数据库不抛异常")]
    public void TestDropDatabaseNotExists()
    {
        var db = new DatabaseDirectory(_testPath, _options);

        // 删除不存在的库不应抛异常
        db.Drop();
    }

    [Fact(DisplayName = "删除含文件的数据库目录")]
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

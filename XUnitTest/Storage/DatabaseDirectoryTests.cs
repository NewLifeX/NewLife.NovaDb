using System;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class DatabaseDirectoryTests : IDisposable
{
    private readonly string _testPath;
    private readonly DbOptions _options;

    public DatabaseDirectoryTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"NovaDbTest_{Guid.NewGuid()}");
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

    [Fact]
    public void TestCreateDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        Assert.True(Directory.Exists(_testPath));
        Assert.True(Directory.Exists(db.SystemPath));
        Assert.True(File.Exists(Path.Combine(_testPath, "nova.db")));
    }

    [Fact]
    public void TestOpenDatabase()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var db2 = new DatabaseDirectory(_testPath, _options);
        db2.Open(); // Should not throw
    }

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
    public void TestListTables()
    {
        var db = new DatabaseDirectory(_testPath, _options);
        db.Create();

        var table1 = db.GetTableDirectory("Users");
        table1.Create();

        var table2 = db.GetTableDirectory("Orders");
        table2.Create();

        var tables = db.ListTables().ToList();

        Assert.Equal(2, tables.Count);
        Assert.Contains("Users", tables);
        Assert.Contains("Orders", tables);
    }
}

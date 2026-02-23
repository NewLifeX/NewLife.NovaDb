using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

/// <summary>备份恢复管理器单元测试</summary>
public class BackupManagerTests : IDisposable
{
    private readonly String _testDir;
    private readonly String _dbDir;
    private readonly String _backupDir;

    public BackupManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"BackupTests_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(_testDir, "db");
        _backupDir = Path.Combine(_testDir, "backup");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    [Fact(DisplayName = "全量备份数据库")]
    public void TestBackup()
    {
        // 创建数据库并插入数据
        using (var engine = new SqlEngine(_dbDir, new DbOptions { Path = _dbDir, WalMode = WalMode.None }))
        {
            engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");
        }

        // 执行备份
        var result = BackupManager.Backup(_dbDir, _backupDir);

        Assert.True(result.FileCount > 0);
        Assert.True(result.TotalBytes > 0);
        Assert.True(Directory.Exists(_backupDir));
        Assert.Equal(_dbDir, result.SourcePath);
        Assert.Equal(_backupDir, result.BackupPath);
    }

    [Fact(DisplayName = "备份并恢复数据库")]
    public void TestBackupAndRestore()
    {
        // 创建数据库并插入数据
        using (var engine = new SqlEngine(_dbDir, new DbOptions { Path = _dbDir, WalMode = WalMode.None }))
        {
            engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");
        }

        // 备份
        BackupManager.Backup(_dbDir, _backupDir);

        // 恢复到新位置
        var restoreDir = Path.Combine(_testDir, "restored");
        var result = BackupManager.Restore(_backupDir, restoreDir);

        Assert.True(result.FileCount > 0);
        Assert.True(Directory.Exists(restoreDir));

        // 验证恢复的文件与源文件一致
        var sourceFiles = Directory.GetFiles(_dbDir);
        var restoredFiles = Directory.GetFiles(restoreDir);

        // 排除 .lock 文件比较
        var srcDataFiles = Array.FindAll(sourceFiles, f => !f.EndsWith(".lock"));
        var rstDataFiles = Array.FindAll(restoredFiles, f => !f.EndsWith(".lock"));
        Assert.Equal(srcDataFiles.Length, rstDataFiles.Length);

        // 验证文件内容一致
        foreach (var srcFile in srcDataFiles)
        {
            var fileName = Path.GetFileName(srcFile);
            var rstFile = Path.Combine(restoreDir, fileName);
            Assert.True(File.Exists(rstFile), $"Restored file missing: {fileName}");
            Assert.Equal(new FileInfo(srcFile).Length, new FileInfo(rstFile).Length);
        }
    }

    [Fact(DisplayName = "备份排除WAL文件")]
    public void TestBackupExcludeWal()
    {
        // 创建数据库（启用 WAL）
        using (var engine = new SqlEngine(_dbDir, new DbOptions { Path = _dbDir, WalMode = WalMode.Normal }))
        {
            engine.Execute("CREATE TABLE test (id INT PRIMARY KEY, val INT)");
            engine.Execute("INSERT INTO test (id, val) VALUES (1, 100)");
        }

        // 备份排除 WAL
        var result = BackupManager.Backup(_dbDir, _backupDir, excludeWal: true);

        // 验证备份目录中无 .wal 文件
        var walFiles = Directory.GetFiles(_backupDir, "*.wal");
        Assert.Empty(walFiles);
    }

    [Fact(DisplayName = "备份源不存在异常")]
    public void TestBackupSourceNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            BackupManager.Backup("/nonexistent/path", _backupDir));
        Assert.Equal(ErrorCode.DatabaseNotFound, ex.Code);
    }

    [Fact(DisplayName = "备份目标已存在异常")]
    public void TestBackupTargetExists()
    {
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_backupDir);

        var ex = Assert.Throws<NovaException>(() =>
            BackupManager.Backup(_dbDir, _backupDir));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "恢复源不存在异常")]
    public void TestRestoreSourceNotFound()
    {
        var ex = Assert.Throws<NovaException>(() =>
            BackupManager.Restore("/nonexistent/path", _backupDir));
        Assert.Equal(ErrorCode.DatabaseNotFound, ex.Code);
    }

    [Fact(DisplayName = "验证备份完整性")]
    public void TestVerifyBackup()
    {
        // 创建数据库
        using (var engine = new SqlEngine(_dbDir, new DbOptions { Path = _dbDir, WalMode = WalMode.None }))
        {
            engine.Execute("CREATE TABLE test (id INT PRIMARY KEY)");
        }

        // 备份
        BackupManager.Backup(_dbDir, _backupDir);

        // 验证
        Assert.True(BackupManager.VerifyBackup(_backupDir));
    }

    [Fact(DisplayName = "列出表文件")]
    public void TestListTableFiles()
    {
        using (var engine = new SqlEngine(_dbDir, new DbOptions { Path = _dbDir, WalMode = WalMode.None }))
        {
            engine.Execute("CREATE TABLE test (id INT PRIMARY KEY, val INT)");
            engine.Execute("INSERT INTO test (id, val) VALUES (1, 100)");
        }

        var files = BackupManager.ListTableFiles(_dbDir);
        Assert.True(files.Count > 0);
    }
}

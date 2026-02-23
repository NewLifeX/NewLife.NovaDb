using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>备份恢复管理器，支持物理目录备份和恢复</summary>
public class BackupManager
{
    /// <summary>执行全量备份，将数据库目录复制到指定备份路径</summary>
    /// <param name="sourcePath">数据库源目录</param>
    /// <param name="backupPath">备份目标目录</param>
    /// <param name="excludeWal">是否排除 WAL 文件（默认 false）</param>
    /// <returns>备份结果</returns>
    public static BackupResult Backup(String sourcePath, String backupPath, Boolean excludeWal = false)
    {
        if (sourcePath == null) throw new ArgumentNullException(nameof(sourcePath));
        if (backupPath == null) throw new ArgumentNullException(nameof(backupPath));

        if (!Directory.Exists(sourcePath))
            throw new NovaException(ErrorCode.DatabaseNotFound, $"Source directory '{sourcePath}' not found");

        if (Directory.Exists(backupPath))
            throw new NovaException(ErrorCode.InvalidArgument, $"Backup directory '{backupPath}' already exists");

        Directory.CreateDirectory(backupPath);

        var fileCount = 0;
        var totalBytes = 0L;

        // 复制所有文件
        foreach (var file in Directory.GetFiles(sourcePath))
        {
            var fileName = Path.GetFileName(file);

            // 排除锁文件
            if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                continue;

            // 可选排除 WAL 文件
            if (excludeWal && fileName.EndsWith(".wal", StringComparison.OrdinalIgnoreCase))
                continue;

            var destFile = Path.Combine(backupPath, fileName);
            File.Copy(file, destFile, overwrite: false);

            fileCount++;
            totalBytes += new FileInfo(file).Length;
        }

        // 递归复制子目录
        foreach (var dir in Directory.GetDirectories(sourcePath))
        {
            var dirName = Path.GetFileName(dir);
            var destDir = Path.Combine(backupPath, dirName);
            var subResult = CopyDirectory(dir, destDir, excludeWal);
            fileCount += subResult.FileCount;
            totalBytes += subResult.TotalBytes;
        }

        return new BackupResult
        {
            SourcePath = sourcePath,
            BackupPath = backupPath,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            BackupTime = DateTime.UtcNow
        };
    }

    /// <summary>从备份恢复数据库到目标路径</summary>
    /// <param name="backupPath">备份源目录</param>
    /// <param name="restorePath">恢复目标目录</param>
    /// <returns>恢复结果</returns>
    public static BackupResult Restore(String backupPath, String restorePath)
    {
        if (backupPath == null) throw new ArgumentNullException(nameof(backupPath));
        if (restorePath == null) throw new ArgumentNullException(nameof(restorePath));

        if (!Directory.Exists(backupPath))
            throw new NovaException(ErrorCode.DatabaseNotFound, $"Backup directory '{backupPath}' not found");

        if (Directory.Exists(restorePath))
            throw new NovaException(ErrorCode.InvalidArgument, $"Restore directory '{restorePath}' already exists");

        // 验证备份完整性
        VerifyBackup(backupPath);

        Directory.CreateDirectory(restorePath);

        var fileCount = 0;
        var totalBytes = 0L;

        // 复制所有文件
        foreach (var file in Directory.GetFiles(backupPath))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(restorePath, fileName);
            File.Copy(file, destFile, overwrite: false);

            fileCount++;
            totalBytes += new FileInfo(file).Length;
        }

        // 递归复制子目录
        foreach (var dir in Directory.GetDirectories(backupPath))
        {
            var dirName = Path.GetFileName(dir);
            var destDir = Path.Combine(restorePath, dirName);
            var subResult = CopyDirectory(dir, destDir, excludeWal: false);
            fileCount += subResult.FileCount;
            totalBytes += subResult.TotalBytes;
        }

        return new BackupResult
        {
            SourcePath = backupPath,
            BackupPath = restorePath,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            BackupTime = DateTime.UtcNow
        };
    }

    /// <summary>验证备份目录完整性</summary>
    /// <param name="backupPath">备份目录</param>
    /// <returns>验证通过返回 true</returns>
    public static Boolean VerifyBackup(String backupPath)
    {
        if (backupPath == null) throw new ArgumentNullException(nameof(backupPath));
        if (!Directory.Exists(backupPath))
            throw new NovaException(ErrorCode.DatabaseNotFound, $"Backup directory '{backupPath}' not found");

        // 检查是否有数据文件
        var dataFiles = Directory.GetFiles(backupPath, "*.data");
        var novaDbFile = Path.Combine(backupPath, "nova.db");

        // 至少需要有 nova.db 或数据文件
        if (!File.Exists(novaDbFile) && dataFiles.Length == 0)
            throw new NovaException(ErrorCode.InvalidArgument, "Backup directory contains no recognizable database files");

        // 验证 nova.db 文件头
        if (File.Exists(novaDbFile))
        {
            var header = DatabaseManager.TryReadFileHeader(novaDbFile);
            if (header == null)
                throw new NovaException(ErrorCode.InvalidArgument, "Invalid nova.db file header in backup");
        }

        return true;
    }

    /// <summary>列出数据库目录中的表文件</summary>
    /// <param name="dbPath">数据库目录</param>
    /// <returns>表文件列表</returns>
    public static List<String> ListTableFiles(String dbPath)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        if (!Directory.Exists(dbPath))
            return [];

        var result = new List<String>();
        foreach (var file in Directory.GetFiles(dbPath))
        {
            var ext = Path.GetExtension(file).ToLower();
            if (ext is ".data" or ".idx" or ".wal" or ".db" or ".log" or ".binlog")
                result.Add(file);
        }

        return result;
    }

    /// <summary>递归复制目录</summary>
    private static BackupResult CopyDirectory(String sourceDir, String destDir, Boolean excludeWal)
    {
        Directory.CreateDirectory(destDir);

        var fileCount = 0;
        var totalBytes = 0L;

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
            if (excludeWal && fileName.EndsWith(".wal", StringComparison.OrdinalIgnoreCase)) continue;

            File.Copy(file, Path.Combine(destDir, fileName), overwrite: false);
            fileCount++;
            totalBytes += new FileInfo(file).Length;
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var sub = CopyDirectory(dir, Path.Combine(destDir, dirName), excludeWal);
            fileCount += sub.FileCount;
            totalBytes += sub.TotalBytes;
        }

        return new BackupResult
        {
            SourcePath = sourceDir,
            BackupPath = destDir,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            BackupTime = DateTime.UtcNow
        };
    }
}

/// <summary>备份/恢复结果</summary>
public class BackupResult
{
    /// <summary>源路径</summary>
    public String SourcePath { get; set; } = String.Empty;

    /// <summary>备份/恢复目标路径</summary>
    public String BackupPath { get; set; } = String.Empty;

    /// <summary>文件数量</summary>
    public Int32 FileCount { get; set; }

    /// <summary>总字节数</summary>
    public Int64 TotalBytes { get; set; }

    /// <summary>操作时间</summary>
    public DateTime BackupTime { get; set; }
}

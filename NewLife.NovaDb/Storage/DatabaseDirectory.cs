using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>
/// 数据库目录管理（文件夹即数据库）
/// </summary>
public class DatabaseDirectory
{
    private readonly String _basePath;
    private readonly DbOptions _options;

    /// <summary>
    /// 数据库路径
    /// </summary>
    public String Path => _basePath;

    /// <summary>
    /// 系统表路径
    /// </summary>
    public String SystemPath => System.IO.Path.Combine(_basePath, "_sys");

    public DatabaseDirectory(String path, DbOptions options)
    {
        _basePath = path ?? throw new ArgumentNullException(nameof(path));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 创建数据库（创建目录结构）
    /// </summary>
    public void Create()
    {
        if (Directory.Exists(_basePath))
        {
            throw new NovaDbException(ErrorCode.InvalidArgument,
                $"Database directory already exists: {_basePath}");
        }

        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(SystemPath);

        // 创建数据库元数据文件
        var metaPath = System.IO.Path.Combine(_basePath, "nova.db");
        var header = new FileHeader
        {
            Version = 1,
            FileType = FileType.Data,
            PageSize = (UInt32)_options.PageSize,
            CreatedAt = DateTime.UtcNow.Ticks,
            OptionsHash = ComputeOptionsHash(_options)
        };

        File.WriteAllBytes(metaPath, header.ToBytes());
    }

    /// <summary>
    /// 打开数据库（验证目录存在且格式正确）
    /// </summary>
    public void Open()
    {
        if (!Directory.Exists(_basePath))
        {
            throw new NovaDbException(ErrorCode.InvalidArgument,
                $"Database directory does not exist: {_basePath}");
        }

        var metaPath = System.IO.Path.Combine(_basePath, "nova.db");
        if (!File.Exists(metaPath))
        {
            throw new NovaDbException(ErrorCode.FileCorrupted,
                $"Database metadata file not found: {metaPath}");
        }

        // 验证文件格式
        var metaBytes = File.ReadAllBytes(metaPath);
        var header = FileHeader.FromBytes(metaBytes);

        if (header.Version > 1)
        {
            throw new NovaDbException(ErrorCode.IncompatibleFileFormat,
                $"Unsupported database version: {header.Version}");
        }

        // 验证配置一致性
        var currentHash = ComputeOptionsHash(_options);
        if (header.OptionsHash != currentHash && header.PageSize != _options.PageSize)
        {
            NewLife.Log.XTrace.WriteLine($"Warning: Database options mismatch. " +
                $"File PageSize={header.PageSize}, Current PageSize={_options.PageSize}");
        }
    }

    /// <summary>
    /// 列举所有表
    /// </summary>
    public IEnumerable<String> ListTables()
    {
        if (!Directory.Exists(_basePath))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(_basePath))
        {
            var tableName = System.IO.Path.GetFileName(dir);
            if (!tableName.StartsWith("_"))
            {
                yield return tableName;
            }
        }
    }

    /// <summary>
    /// 获取表目录
    /// </summary>
    public TableDirectory GetTableDirectory(String tableName)
    {
        if (String.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Table name cannot be empty", nameof(tableName));
        }

        var tablePath = System.IO.Path.Combine(_basePath, tableName);
        return new TableDirectory(tablePath, tableName, _options);
    }

    /// <summary>
    /// 删除数据库
    /// </summary>
    public void Drop()
    {
        if (Directory.Exists(_basePath))
        {
            Directory.Delete(_basePath, recursive: true);
        }
    }

    /// <summary>
    /// 计算配置哈希
    /// </summary>
    private UInt32 ComputeOptionsHash(DbOptions options)
    {
        // 简单的哈希计算（使用页大小作为主要标识）
        return (UInt32)options.PageSize;
    }
}

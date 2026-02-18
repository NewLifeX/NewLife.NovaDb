using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>数据库目录管理（文件夹即数据库）</summary>
/// <remarks>
/// 数据库目录结构：
/// - nova.db: 元数据文件（32 字节 FileHeader）
/// - {TableName}.data/.wal/.idx: 表文件组（平铺在目录下）
/// - _sys_*.data: 系统表文件
/// </remarks>
public class DatabaseDirectory
{
    private readonly String _basePath;
    private readonly DbOptions _options;

    /// <summary>数据库路径</summary>
    public String Path => _basePath;

    /// <summary>实例化数据库目录管理器</summary>
    /// <param name="path">数据库目录路径</param>
    /// <param name="options">数据库配置</param>
    public DatabaseDirectory(String path, DbOptions options)
    {
        _basePath = path ?? throw new ArgumentNullException(nameof(path));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (String.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Database path cannot be empty", nameof(path));
    }

    /// <summary>创建数据库（创建目录并写入元数据文件）</summary>
    /// <exception cref="NovaException">目录已存在时抛出</exception>
    public void Create()
    {
        if (Directory.Exists(_basePath))
            throw new NovaException(ErrorCode.InvalidArgument, $"Database directory already exists: {_basePath}");

        Directory.CreateDirectory(_basePath);

        // 写入元数据文件
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

    /// <summary>打开数据库（验证目录存在且格式正确）</summary>
    /// <exception cref="NovaException">目录不存在、元数据文件缺失或版本不兼容时抛出</exception>
    public void Open()
    {
        if (!Directory.Exists(_basePath))
            throw new NovaException(ErrorCode.InvalidArgument, $"Database directory does not exist: {_basePath}");

        var metaPath = System.IO.Path.Combine(_basePath, "nova.db");
        if (!File.Exists(metaPath))
            throw new NovaException(ErrorCode.FileCorrupted, $"Database metadata file not found: {metaPath}");

        // 验证文件格式
        var metaBytes = File.ReadAllBytes(metaPath);
        var header = FileHeader.FromBytes(metaBytes);

        if (header.Version > 1)
            throw new NovaException(ErrorCode.IncompatibleFileFormat, $"Unsupported database version: {header.Version}");

        // 配置一致性警告
        var currentHash = ComputeOptionsHash(_options);
        if (header.OptionsHash != currentHash && header.PageSize != _options.PageSize)
        {
            NewLife.Log.XTrace.WriteLine($"Warning: Database options mismatch. " +
                $"File PageSize={header.PageSize}, Current PageSize={_options.PageSize}");
        }
    }

    /// <summary>列举所有用户表</summary>
    /// <remarks>通过扫描数据库目录下的 .data 文件提取表名，自动排除系统表</remarks>
    /// <returns>按字母排序的表名集合</returns>
    public IEnumerable<String> ListTables()
    {
        if (!Directory.Exists(_basePath))
            yield break;

        var tableNames = new HashSet<String>();
        var dataFiles = Directory.GetFiles(_basePath, "*.data");

        foreach (var file in dataFiles)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);

            // 跳过系统表
            if (fileName.StartsWith("_sys"))
                continue;

            // 提取表名：{TableName}.data 或 {TableName}_{ShardId}.data
            var parts = fileName.Split('_');
            var tableName = parts[0];

            if (!String.IsNullOrEmpty(tableName))
                tableNames.Add(tableName);
        }

        foreach (var name in tableNames.OrderBy(x => x))
        {
            yield return name;
        }
    }

    /// <summary>获取表文件管理器</summary>
    /// <param name="tableName">表名</param>
    /// <returns>表文件管理器</returns>
    /// <exception cref="ArgumentException">表名为空时抛出</exception>
    public TableFileManager GetTableFileManager(String tableName)
    {
        if (String.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be empty", nameof(tableName));

        return new TableFileManager(_basePath, tableName, _options);
    }

    /// <summary>删除数据库（删除整个目录）</summary>
    public void Drop()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    /// <summary>计算配置哈希</summary>
    /// <param name="options">数据库配置</param>
    /// <returns>配置哈希值</returns>
    private UInt32 ComputeOptionsHash(DbOptions options) => (UInt32)options.PageSize;
}

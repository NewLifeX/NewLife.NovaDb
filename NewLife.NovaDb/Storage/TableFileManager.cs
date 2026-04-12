using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>表文件管理器（文件路径生成与命名规则）</summary>
/// <remarks>
/// 负责生成表相关文件的路径，基于新的平铺文件布局：
/// - 数据文件：{TableName}.data 或 {TableName}_{ShardId}.data
/// - 索引文件：{TableName}.idx（主键）或 {TableName}_{IndexName}.idx（二级索引）
/// - WAL 文件：{TableName}.wal 或 {TableName}_{ShardId}.wal
/// 所有文件平铺在数据库目录下，无需为每表创建独立目录
/// </remarks>
public class TableFileManager
{
    private readonly String _databasePath;
    private readonly String _tableName;
    private readonly DbOptions _options;

    /// <summary>数据库路径</summary>
    public String DatabasePath => _databasePath;

    /// <summary>表名</summary>
    public String TableName => _tableName;

    /// <summary>构造表文件管理器</summary>
    /// <param name="databasePath">数据库目录路径</param>
    /// <param name="tableName">表名</param>
    /// <param name="options">数据库配置</param>
    public TableFileManager(String databasePath, String tableName, DbOptions options)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (String.IsNullOrWhiteSpace(_tableName))
            throw new ArgumentException("Table name cannot be empty", nameof(tableName));
    }

    /// <summary>获取数据文件路径</summary>
    /// <param name="shardId">分片 ID（可选，默认 null 表示无分片）</param>
    /// <returns>数据文件完整路径</returns>
    public String GetDataFilePath(Int32? shardId = null)
    {
        var fileName = shardId.HasValue
            ? $"{_tableName}_{shardId.Value}.data"
            : $"{_tableName}.data";

        return Path.Combine(_databasePath, fileName);
    }

    /// <summary>获取主键索引文件路径</summary>
    /// <param name="shardId">分片 ID（可选，默认 null 表示无分片）</param>
    /// <returns>主键索引文件完整路径</returns>
    public String GetPrimaryIndexFilePath(Int32? shardId = null)
    {
        var fileName = shardId.HasValue
            ? $"{_tableName}_{shardId.Value}.idx"
            : $"{_tableName}.idx";

        return Path.Combine(_databasePath, fileName);
    }

    /// <summary>获取二级索引文件路径</summary>
    /// <param name="indexName">索引名称</param>
    /// <param name="shardId">分片 ID（可选，默认 null 表示无分片）</param>
    /// <returns>二级索引文件完整路径</returns>
    public String GetSecondaryIndexFilePath(String indexName, Int32? shardId = null)
    {
        if (String.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name cannot be empty", nameof(indexName));

        var fileName = shardId.HasValue
            ? $"{_tableName}_{shardId.Value}_{indexName}.idx"
            : $"{_tableName}_{indexName}.idx";

        return Path.Combine(_databasePath, fileName);
    }

    /// <summary>获取 WAL 文件路径</summary>
    /// <param name="shardId">分片 ID（可选，默认 null 表示无分片）</param>
    /// <returns>WAL 文件完整路径</returns>
    public String GetWalFilePath(Int32? shardId = null)
    {
        var fileName = shardId.HasValue
            ? $"{_tableName}_{shardId.Value}.wal"
            : $"{_tableName}.wal";

        return Path.Combine(_databasePath, fileName);
    }

    /// <summary>列举所有数据分片 ID</summary>
    /// <returns>分片 ID 列表，按从小到大排序</returns>
    public IEnumerable<Int32> ListDataShards()
    {
        if (!Directory.Exists(_databasePath))
            return Enumerable.Empty<Int32>();

        var pattern = $"{_tableName}_*.data";
        var files = Directory.GetFiles(_databasePath, pattern);

        var shardIds = new List<Int32>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // fileName 格式：{TableName}_{ShardId}
            var parts = fileName.Split('_');
            if (parts.Length >= 2 && Int32.TryParse(parts[^1], out var shardId))
            {
                shardIds.Add(shardId);
            }
        }

        return shardIds.OrderBy(x => x);
    }

    /// <summary>列举所有二级索引名称</summary>
    /// <returns>索引名称列表</returns>
    public IEnumerable<String> ListSecondaryIndexes()
    {
        if (!Directory.Exists(_databasePath))
            return Enumerable.Empty<String>();

        var pattern = $"{_tableName}_*.idx";
        var files = Directory.GetFiles(_databasePath, pattern);

        var indexes = new HashSet<String>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // fileName 格式：{TableName}_{IndexName} 或 {TableName}_{ShardId}_{IndexName}

            // 移除表名前缀
            var suffix = fileName.Substring(_tableName.Length + 1); // +1 for '_'
            var parts = suffix.Split('_');

            if (parts.Length >= 1)
            {
                // 检查第一部分是否为数字（分片 ID）
                if (Int32.TryParse(parts[0], out _))
                {
                    // {TableName}_{ShardId}_{IndexName} - 提取索引名（剩余部分）
                    if (parts.Length >= 2)
                    {
                        var indexName = String.Join("_", parts.Skip(1));
                        if (!String.IsNullOrEmpty(indexName))
                        {
                            indexes.Add(indexName);
                        }
                    }
                }
                else
                {
                    // {TableName}_{IndexName} - 整个 suffix 就是索引名
                    indexes.Add(suffix);
                }
            }
        }

        return indexes.OrderBy(x => x);
    }

    /// <summary>删除表的所有文件</summary>
    /// <remarks>删除数据文件、索引文件、WAL 文件</remarks>
    public void DeleteAllFiles()
    {
        if (!Directory.Exists(_databasePath))
            return;

        // 删除数据文件（包括分片）
        var dataPattern = $"{_tableName}*.data";
        foreach (var file in Directory.GetFiles(_databasePath, dataPattern))
        {
            File.Delete(file);
        }

        // 删除索引文件（主键和二级索引）
        var indexPattern = $"{_tableName}*.idx";
        foreach (var file in Directory.GetFiles(_databasePath, indexPattern))
        {
            File.Delete(file);
        }

        // 删除 WAL 文件
        var walPattern = $"{_tableName}*.wal";
        foreach (var file in Directory.GetFiles(_databasePath, walPattern))
        {
            File.Delete(file);
        }
    }

    /// <summary>检查表文件是否存在</summary>
    /// <returns>如果至少存在一个表相关文件则返回 true</returns>
    public Boolean Exists()
    {
        if (!Directory.Exists(_databasePath))
            return false;

        var dataFile = GetDataFilePath();
        return File.Exists(dataFile);
    }
}

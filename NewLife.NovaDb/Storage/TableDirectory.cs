using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>
/// 表目录管理（每表独立文件组）
/// </summary>
public class TableDirectory
{
    private readonly String _tablePath;
    private readonly String _tableName;
    private readonly DbOptions _options;

    /// <summary>
    /// 表路径
    /// </summary>
    public String Path => _tablePath;

    /// <summary>
    /// 表名
    /// </summary>
    public String Name => _tableName;

    public TableDirectory(String tablePath, String tableName, DbOptions options)
    {
        _tablePath = tablePath ?? throw new ArgumentNullException(nameof(tablePath));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 创建表目录
    /// </summary>
    public void Create()
    {
        if (Directory.Exists(_tablePath))
        {
            throw new NovaException(ErrorCode.TableExists,
                $"Table directory already exists: {_tablePath}");
        }

        Directory.CreateDirectory(_tablePath);
    }

    /// <summary>
    /// 打开表目录（验证存在）
    /// </summary>
    public void Open()
    {
        if (!Directory.Exists(_tablePath))
        {
            throw new NovaException(ErrorCode.TableNotFound,
                $"Table directory does not exist: {_tablePath}");
        }
    }

    /// <summary>
    /// 删除表目录
    /// </summary>
    public void Drop()
    {
        if (Directory.Exists(_tablePath))
        {
            Directory.Delete(_tablePath, recursive: true);
        }
    }

    /// <summary>
    /// 获取数据分片文件路径
    /// </summary>
    public String GetDataFilePath(Int32 shardId = 0)
    {
        return System.IO.Path.Combine(_tablePath, $"{shardId}.data");
    }

    /// <summary>
    /// 获取索引文件路径
    /// </summary>
    public String GetIndexFilePath(String indexName)
    {
        if (String.IsNullOrWhiteSpace(indexName))
        {
            throw new ArgumentException("Index name cannot be empty", nameof(indexName));
        }

        return System.IO.Path.Combine(_tablePath, $"{indexName}.idx");
    }

    /// <summary>
    /// 获取 WAL 文件路径
    /// </summary>
    public String GetWalFilePath(Int32 shardId = 0)
    {
        return System.IO.Path.Combine(_tablePath, $"{shardId}.wal");
    }

    /// <summary>
    /// 列举所有数据分片
    /// </summary>
    public IEnumerable<Int32> ListDataShards()
    {
        if (!Directory.Exists(_tablePath))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(_tablePath, "*.data"))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            if (Int32.TryParse(fileName, out var shardId))
            {
                yield return shardId;
            }
        }
    }

    /// <summary>
    /// 列举所有索引
    /// </summary>
    public IEnumerable<String> ListIndexes()
    {
        if (!Directory.Exists(_tablePath))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(_tablePath, "*.idx"))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            yield return fileName;
        }
    }
}

using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine;

/// <summary>索引定义</summary>
public class IndexDefinition
{
    /// <summary>索引名</summary>
    public String IndexName { get; set; } = String.Empty;

    /// <summary>索引列名列表</summary>
    public List<String> Columns { get; set; } = [];

    /// <summary>是否唯一索引</summary>
    public Boolean IsUnique { get; set; }

    /// <summary>创建索引定义</summary>
    /// <param name="indexName">索引名</param>
    /// <param name="columns">索引列</param>
    /// <param name="isUnique">是否唯一</param>
    public IndexDefinition(String indexName, List<String> columns, Boolean isUnique = false)
    {
        IndexName = indexName ?? throw new ArgumentNullException(nameof(indexName));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        IsUnique = isUnique;
    }
}

/// <summary>列定义</summary>
public class ColumnDefinition
{
    /// <summary>列名</summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>数据类型</summary>
    public DataType DataType { get; set; }

    /// <summary>是否允许为空</summary>
    public Boolean Nullable { get; set; } = true;

    /// <summary>是否为主键</summary>
    public Boolean IsPrimaryKey { get; set; }

    /// <summary>列序号（从 0 开始）</summary>
    public Int32 Ordinal { get; set; }

    /// <summary>列注释</summary>
    public String? Comment { get; set; }

    /// <summary>创建列定义</summary>
    /// <param name="name">列名</param>
    /// <param name="dataType">数据类型</param>
    /// <param name="nullable">是否允许为空</param>
    /// <param name="isPrimaryKey">是否为主键</param>
    public ColumnDefinition(String name, DataType dataType, Boolean nullable = true, Boolean isPrimaryKey = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DataType = dataType;
        Nullable = nullable;
        IsPrimaryKey = isPrimaryKey;
    }
}

/// <summary>表架构定义</summary>
public class TableSchema
{
    private readonly List<ColumnDefinition> _columns = [];
    private readonly Dictionary<String, Int32> _columnIndexes = [];
    private Int32? _primaryKeyIndex;
    private readonly List<IndexDefinition> _indexes = [];

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>列定义列表</summary>
    public IReadOnlyList<ColumnDefinition> Columns => _columns;

    /// <summary>主键列索引（如果没有主键则为 null）</summary>
    public Int32? PrimaryKeyIndex => _primaryKeyIndex;

    /// <summary>二级索引列表</summary>
    public IReadOnlyList<IndexDefinition> Indexes => _indexes;

    /// <summary>表注释</summary>
    public String? Comment { get; set; }

    /// <summary>存储引擎名称（默认 Nova）</summary>
    public String EngineName { get; set; } = "Nova";

    /// <summary>创建表架构</summary>
    /// <param name="tableName">表名</param>
    public TableSchema(String tableName)
    {
        TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>添加列</summary>
    /// <param name="column">列定义</param>
    public void AddColumn(ColumnDefinition column)
    {
        if (column == null)
            throw new ArgumentNullException(nameof(column));

        if (_columnIndexes.ContainsKey(column.Name))
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{column.Name}' already exists");

        column.Ordinal = _columns.Count;
        _columns.Add(column);
        _columnIndexes[column.Name] = column.Ordinal;

        if (column.IsPrimaryKey)
        {
            if (_primaryKeyIndex.HasValue)
                throw new NovaException(ErrorCode.InvalidArgument, "Table can only have one primary key");

            _primaryKeyIndex = column.Ordinal;
        }
    }

    /// <summary>删除列</summary>
    /// <param name="columnName">列名</param>
    public void RemoveColumn(String columnName)
    {
        if (columnName == null) throw new ArgumentNullException(nameof(columnName));

        if (!_columnIndexes.TryGetValue(columnName, out var index))
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{columnName}' not found");

        var column = _columns[index];
        if (column.IsPrimaryKey)
            throw new NovaException(ErrorCode.InvalidArgument, $"Cannot drop primary key column '{columnName}'");

        _columns.RemoveAt(index);
        _columnIndexes.Remove(columnName);

        // 重建索引映射
        RebuildColumnIndexes();
    }

    /// <summary>修改列定义</summary>
    /// <param name="columnName">列名</param>
    /// <param name="newDataType">新数据类型</param>
    /// <param name="nullable">是否允许为空</param>
    /// <param name="comment">列注释</param>
    public void ModifyColumn(String columnName, DataType newDataType, Boolean nullable, String? comment = null)
    {
        if (columnName == null) throw new ArgumentNullException(nameof(columnName));

        if (!_columnIndexes.TryGetValue(columnName, out var index))
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{columnName}' not found");

        var column = _columns[index];
        column.DataType = newDataType;
        column.Nullable = nullable;
        if (comment != null)
            column.Comment = comment;
    }

    /// <summary>重建列索引映射</summary>
    private void RebuildColumnIndexes()
    {
        _columnIndexes.Clear();
        _primaryKeyIndex = null;

        for (var i = 0; i < _columns.Count; i++)
        {
            _columns[i].Ordinal = i;
            _columnIndexes[_columns[i].Name] = i;

            if (_columns[i].IsPrimaryKey)
                _primaryKeyIndex = i;
        }
    }

    /// <summary>获取列索引</summary>
    /// <param name="columnName">列名</param>
    /// <returns>列索引</returns>
    public Int32 GetColumnIndex(String columnName)
    {
        if (columnName == null)
            throw new ArgumentNullException(nameof(columnName));

        if (!_columnIndexes.TryGetValue(columnName, out var index))
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{columnName}' not found");

        return index;
    }

    /// <summary>获取列定义</summary>
    /// <param name="columnName">列名</param>
    /// <returns>列定义</returns>
    public ColumnDefinition GetColumn(String columnName)
    {
        var index = GetColumnIndex(columnName);
        return _columns[index];
    }

    /// <summary>获取主键列定义</summary>
    /// <returns>主键列定义</returns>
    public ColumnDefinition? GetPrimaryKeyColumn()
    {
        if (!_primaryKeyIndex.HasValue)
            return null;

        return _columns[_primaryKeyIndex.Value];
    }

    /// <summary>检查列是否存在</summary>
    /// <param name="columnName">列名</param>
    /// <returns>是否存在</returns>
    public Boolean HasColumn(String columnName)
    {
        if (columnName == null)
            return false;

        return _columnIndexes.ContainsKey(columnName);
    }

    /// <summary>添加二级索引</summary>
    /// <param name="index">索引定义</param>
    public void AddIndex(IndexDefinition index)
    {
        if (index == null) throw new ArgumentNullException(nameof(index));

        // 检查索引名是否重复
        foreach (var existing in _indexes)
        {
            if (String.Equals(existing.IndexName, index.IndexName, StringComparison.OrdinalIgnoreCase))
                throw new NovaException(ErrorCode.InvalidArgument, $"Index '{index.IndexName}' already exists");
        }

        // 验证索引列是否都存在
        foreach (var col in index.Columns)
        {
            if (!_columnIndexes.ContainsKey(col))
                throw new NovaException(ErrorCode.InvalidArgument, $"Column '{col}' not found in table '{TableName}'");
        }

        _indexes.Add(index);
    }

    /// <summary>删除二级索引</summary>
    /// <param name="indexName">索引名</param>
    public void RemoveIndex(String indexName)
    {
        if (indexName == null) throw new ArgumentNullException(nameof(indexName));

        var removed = false;
        for (var i = _indexes.Count - 1; i >= 0; i--)
        {
            if (String.Equals(_indexes[i].IndexName, indexName, StringComparison.OrdinalIgnoreCase))
            {
                _indexes.RemoveAt(i);
                removed = true;
                break;
            }
        }

        if (!removed)
            throw new NovaException(ErrorCode.InvalidArgument, $"Index '{indexName}' not found");
    }

    /// <summary>根据索引名获取索引定义</summary>
    /// <param name="indexName">索引名</param>
    /// <returns>索引定义，不存在返回 null</returns>
    public IndexDefinition? GetIndex(String indexName)
    {
        foreach (var idx in _indexes)
        {
            if (String.Equals(idx.IndexName, indexName, StringComparison.OrdinalIgnoreCase))
                return idx;
        }
        return null;
    }
}

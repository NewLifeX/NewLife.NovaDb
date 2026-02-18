using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Engine;

/// <summary>
/// 列定义
/// </summary>
public class ColumnDefinition
{
    /// <summary>
    /// 列名
    /// </summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>
    /// 数据类型
    /// </summary>
    public DataType DataType { get; set; }

    /// <summary>
    /// 是否允许为空
    /// </summary>
    public Boolean Nullable { get; set; } = true;

    /// <summary>
    /// 是否为主键
    /// </summary>
    public Boolean IsPrimaryKey { get; set; }

    /// <summary>
    /// 列序号（从 0 开始）
    /// </summary>
    public Int32 Ordinal { get; set; }

    /// <summary>
    /// 创建列定义
    /// </summary>
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

/// <summary>
/// 表架构定义
/// </summary>
public class TableSchema
{
    private readonly List<ColumnDefinition> _columns = new();
    private readonly Dictionary<String, Int32> _columnIndexes = new();
    private Int32? _primaryKeyIndex;

    /// <summary>
    /// 表名
    /// </summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>
    /// 列定义列表
    /// </summary>
    public IReadOnlyList<ColumnDefinition> Columns => _columns;

    /// <summary>
    /// 主键列索引（如果没有主键则为 null）
    /// </summary>
    public Int32? PrimaryKeyIndex => _primaryKeyIndex;

    /// <summary>
    /// 创建表架构
    /// </summary>
    /// <param name="tableName">表名</param>
    public TableSchema(String tableName)
    {
        TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>
    /// 添加列
    /// </summary>
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

    /// <summary>
    /// 获取列索引
    /// </summary>
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

    /// <summary>
    /// 获取列定义
    /// </summary>
    /// <param name="columnName">列名</param>
    /// <returns>列定义</returns>
    public ColumnDefinition GetColumn(String columnName)
    {
        var index = GetColumnIndex(columnName);
        return _columns[index];
    }

    /// <summary>
    /// 获取主键列定义
    /// </summary>
    /// <returns>主键列定义</returns>
    public ColumnDefinition? GetPrimaryKeyColumn()
    {
        if (!_primaryKeyIndex.HasValue)
            return null;

        return _columns[_primaryKeyIndex.Value];
    }

    /// <summary>
    /// 检查列是否存在
    /// </summary>
    /// <param name="columnName">列名</param>
    /// <returns>是否存在</returns>
    public Boolean HasColumn(String columnName)
    {
        if (columnName == null)
            return false;

        return _columnIndexes.ContainsKey(columnName);
    }
}

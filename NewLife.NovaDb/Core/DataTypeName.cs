using NewLife;

namespace NewLife.NovaDb.Core;

/// <summary>数据类型名称表。规范名用于对外输出，别名只用于输入解析</summary>
/// <remarks>
/// 规则：输入宽松，输出唯一。
/// 建表、CAST、元数据回灌等输入侧接受各类 SQL 方言别名；
/// SHOW COLUMNS、information_schema、驱动元数据等输出侧只允许出现规范名。
/// </remarks>
public static class DataTypeNames
{
    /// <summary>规范名（大写）</summary>
    private static readonly Dictionary<DataType, String> _names = new()
    {
        [DataType.Boolean] = "BOOL",
        [DataType.Int32] = "INT",
        [DataType.Int64] = "BIGINT",
        [DataType.Double] = "DOUBLE",
        [DataType.Decimal] = "DECIMAL",
        [DataType.DateTime] = "DATETIME",
        [DataType.String] = "STRING",
        [DataType.Binary] = "BINARY",
        [DataType.GeoPoint] = "GEOPOINT",
        [DataType.Vector] = "VECTOR",
    };

    /// <summary>可接受的全部类型名（规范名 + 别名），不区分大小写</summary>
    private static readonly Dictionary<String, DataType> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = DataType.Boolean,
        ["BOOLEAN"] = DataType.Boolean,
        ["BIT"] = DataType.Boolean,

        ["INT"] = DataType.Int32,
        ["INT32"] = DataType.Int32,
        ["INTEGER"] = DataType.Int32,
        ["TINYINT"] = DataType.Int32,
        ["SMALLINT"] = DataType.Int32,
        ["MEDIUMINT"] = DataType.Int32,

        ["BIGINT"] = DataType.Int64,
        ["INT64"] = DataType.Int64,
        ["LONG"] = DataType.Int64,

        ["DOUBLE"] = DataType.Double,
        ["FLOAT"] = DataType.Double,
        ["REAL"] = DataType.Double,

        ["DECIMAL"] = DataType.Decimal,
        ["NUMERIC"] = DataType.Decimal,

        ["DATETIME"] = DataType.DateTime,
        ["TIMESTAMP"] = DataType.DateTime,
        ["DATE"] = DataType.DateTime,
        ["TIME"] = DataType.DateTime,
        ["YEAR"] = DataType.DateTime,

        ["STRING"] = DataType.String,
        ["CHAR"] = DataType.String,
        ["NCHAR"] = DataType.String,
        ["VARCHAR"] = DataType.String,
        ["NVARCHAR"] = DataType.String,
        ["TEXT"] = DataType.String,
        ["TINYTEXT"] = DataType.String,
        ["MEDIUMTEXT"] = DataType.String,
        ["LONGTEXT"] = DataType.String,
        ["JSON"] = DataType.String,
        ["ENUM"] = DataType.String,
        ["SET"] = DataType.String,

        ["BINARY"] = DataType.Binary,
        ["VARBINARY"] = DataType.Binary,
        ["BYTES"] = DataType.Binary,
        ["BLOB"] = DataType.Binary,
        ["TINYBLOB"] = DataType.Binary,
        ["MEDIUMBLOB"] = DataType.Binary,
        ["LONGBLOB"] = DataType.Binary,

        ["GEOPOINT"] = DataType.GeoPoint,
        ["VECTOR"] = DataType.Vector,
    };

    /// <summary>获取数据类型的规范名（大写）</summary>
    /// <param name="type">数据类型</param>
    /// <returns>规范名</returns>
    public static String GetName(DataType type) => _names.TryGetValue(type, out var name)
        ? name
        : throw new NotSupportedException(L.T("Unsupported data type: {0}", type));

    /// <summary>尝试按名称解析数据类型，支持别名，不区分大小写</summary>
    /// <param name="name">类型名</param>
    /// <param name="type">解析结果</param>
    /// <returns>是否解析成功</returns>
    public static Boolean TryParse(String? name, out DataType type)
    {
        type = default;
        if (name.IsNullOrEmpty()) return false;

        return _aliases.TryGetValue(name.Trim(), out type);
    }

    /// <summary>按名称解析数据类型，未知名称抛出语法错误</summary>
    /// <param name="name">类型名</param>
    /// <returns>数据类型</returns>
    public static DataType Parse(String? name)
    {
        if (TryParse(name, out var type)) return type;

        throw new NovaException(ErrorCode.SyntaxError, "Unknown data type: {0}", name);
    }

    /// <summary>判断数据类型是否支持长度声明。字符串为最大字符数，二进制为最大字节数，向量为维度</summary>
    /// <param name="type">数据类型</param>
    /// <returns>是否支持长度声明</returns>
    public static Boolean SupportsLength(DataType type) => type is DataType.String or DataType.Binary or DataType.Vector;

    /// <summary>格式化类型名用于元数据展示，小写规范名加可选长度，如 string(50)</summary>
    /// <param name="type">数据类型</param>
    /// <param name="length">长度，0 表示未声明</param>
    /// <returns>展示用类型名</returns>
    public static String Format(DataType type, Int32 length = 0)
    {
        var name = GetName(type).ToLowerInvariant();

        return length > 0 && SupportsLength(type) ? $"{name}({length})" : name;
    }
}

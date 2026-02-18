namespace NewLife.NovaDb.Core;

/// <summary>
/// NovaDb 支持的数据类型（严格映射 C# 类型）
/// </summary>
public enum DataType : byte
{
    /// <summary>
    /// 布尔型（1 字节）
    /// </summary>
    Boolean = 1,

    /// <summary>
    /// 32 位整数
    /// </summary>
    Int32 = 2,

    /// <summary>
    /// 64 位整数
    /// </summary>
    Int64 = 3,

    /// <summary>
    /// 双精度浮点
    /// </summary>
    Double = 4,

    /// <summary>
    /// 128 位高精度十进制
    /// </summary>
    Decimal = 5,

    /// <summary>
    /// UTF-8 字符串
    /// </summary>
    String = 6,

    /// <summary>
    /// 字节数组
    /// </summary>
    ByteArray = 7,

    /// <summary>
    /// 日期时间（内部存 Ticks）
    /// </summary>
    DateTime = 8
}

/// <summary>
/// 数据类型扩展方法
/// </summary>
public static class DataTypeExtensions
{
    /// <summary>
    /// 获取数据类型的 C# 类型
    /// </summary>
    public static Type GetClrType(this DataType dataType)
    {
        return dataType switch
        {
            DataType.Boolean => typeof(Boolean),
            DataType.Int32 => typeof(Int32),
            DataType.Int64 => typeof(Int64),
            DataType.Double => typeof(Double),
            DataType.Decimal => typeof(Decimal),
            DataType.String => typeof(String),
            DataType.ByteArray => typeof(Byte[]),
            DataType.DateTime => typeof(DateTime),
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    /// <summary>
    /// 从 C# 类型获取数据类型
    /// </summary>
    public static DataType FromClrType(Type type)
    {
        if (type == typeof(Boolean)) return DataType.Boolean;
        if (type == typeof(Int32)) return DataType.Int32;
        if (type == typeof(Int64)) return DataType.Int64;
        if (type == typeof(Double)) return DataType.Double;
        if (type == typeof(Decimal)) return DataType.Decimal;
        if (type == typeof(String)) return DataType.String;
        if (type == typeof(Byte[])) return DataType.ByteArray;
        if (type == typeof(DateTime)) return DataType.DateTime;

        throw new NotSupportedException($"Unsupported CLR type: {type.FullName}");
    }
}

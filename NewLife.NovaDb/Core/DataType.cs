namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 支持的数据类型（严格映射 C# 类型）</summary>
/// <remarks>基础类型的枚举值与 TypeCode 保持一致</remarks>
public enum DataType : Byte
{
    /// <summary>布尔型（1 字节）- 对应 TypeCode.Boolean</summary>
    Boolean = 3,

    /// <summary>32 位整数（4 字节）- 对应 TypeCode.Int32</summary>
    Int32 = 9,

    /// <summary>64 位整数（8 字节）- 对应 TypeCode.Int64</summary>
    Int64 = 11,

    /// <summary>双精度浮点（8 字节）- 对应 TypeCode.Double</summary>
    Double = 14,

    /// <summary>128 位高精度十进制 - 对应 TypeCode.Decimal</summary>
    Decimal = 15,

    /// <summary>日期时间（精确到 Ticks）- 对应 TypeCode.DateTime</summary>
    DateTime = 16,

    /// <summary>UTF-8 字符串 - 对应 TypeCode.String</summary>
    String = 18,

    /// <summary>字节数组（BINARY/VARBINARY/BLOB）</summary>
    Binary = 101,

    /// <summary>地理坐标（经纬度，16 字节）</summary>
    GeoPoint = 102,

    /// <summary>向量（定长浮点数组，用于 AI 检索）</summary>
    Vector = 103
}

/// <summary>数据类型扩展方法</summary>
public static class DataTypeExtensions
{
    /// <summary>获取数据类型的 C# 类型</summary>
    /// <param name="dataType">数据类型</param>
    /// <returns>C# 类型</returns>
    public static Type GetClrType(this DataType dataType)
    {
        return dataType switch
        {
            DataType.Boolean => typeof(Boolean),
            DataType.Int32 => typeof(Int32),
            DataType.Int64 => typeof(Int64),
            DataType.Double => typeof(Double),
            DataType.Decimal => typeof(Decimal),
            DataType.DateTime => typeof(DateTime),
            DataType.String => typeof(String),
            DataType.Binary => typeof(Byte[]),
            DataType.GeoPoint => typeof(GeoPoint),
            DataType.Vector => typeof(Single[]),
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }

    /// <summary>从 C# 类型获取数据类型</summary>
    /// <param name="type">C# 类型</param>
    /// <returns>数据类型</returns>
    public static DataType FromClrType(Type type)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        // 处理可空类型
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type == typeof(Boolean)) return DataType.Boolean;
        if (type == typeof(Int32)) return DataType.Int32;
        if (type == typeof(Int64)) return DataType.Int64;
        if (type == typeof(Double)) return DataType.Double;
        if (type == typeof(Decimal)) return DataType.Decimal;
        if (type == typeof(DateTime)) return DataType.DateTime;
        if (type == typeof(String)) return DataType.String;
        if (type == typeof(Byte[])) return DataType.Binary;
        if (type == typeof(GeoPoint)) return DataType.GeoPoint;
        if (type == typeof(Single[])) return DataType.Vector;

        throw new NotSupportedException($"Unsupported CLR type: {type.FullName}");
    }
}

/// <summary>地理坐标点（经纬度）</summary>
/// <remarks>初始化地理坐标点</remarks>
/// <param name="latitude">纬度</param>
/// <param name="longitude">经度</param>
public struct GeoPoint(Double latitude, Double longitude)
{
    /// <summary>纬度（-90 到 90）</summary>
    public Double Latitude { get; set; } = latitude;

    /// <summary>经度（-180 到 180）</summary>
    public Double Longitude { get; set; } = longitude;

    /// <summary>返回字符串表示</summary>
    public override readonly String ToString() => $"({Latitude}, {Longitude})";
}

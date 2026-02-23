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

/// <summary>地理编码类型，表示经纬度坐标点</summary>
/// <remarks>初始化地理坐标点</remarks>
/// <param name="latitude">纬度（-90 到 90）</param>
/// <param name="longitude">经度（-180 到 180）</param>
public readonly struct GeoPoint(Double latitude, Double longitude) : IEquatable<GeoPoint>
{
    /// <summary>地球平均半径（米）</summary>
    private const Double EarthRadiusMeters = 6_371_000.0;

    /// <summary>纬度（-90 到 90）</summary>
    public Double Latitude { get; } = latitude;

    /// <summary>经度（-180 到 180）</summary>
    public Double Longitude { get; } = longitude;

    /// <summary>计算到另一个坐标点的距离（米），使用 Haversine 公式</summary>
    /// <param name="other">另一个坐标点</param>
    /// <returns>距离（米）</returns>
    public Double Distance(GeoPoint other)
    {
        var lat1 = Latitude * Math.PI / 180.0;
        var lat2 = other.Latitude * Math.PI / 180.0;
        var dLat = (other.Latitude - Latitude) * Math.PI / 180.0;
        var dLon = (other.Longitude - Longitude) * Math.PI / 180.0;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    /// <summary>判断是否在指定中心点的半径范围内</summary>
    /// <param name="center">中心坐标点</param>
    /// <param name="radiusMeters">半径（米）</param>
    /// <returns>是否在范围内</returns>
    public Boolean WithinRadius(GeoPoint center, Double radiusMeters) => Distance(center) <= radiusMeters;

    /// <summary>判断点是否在多边形内，使用射线法（Ray Casting）</summary>
    /// <param name="polygon">多边形顶点数组，首尾自动闭合</param>
    /// <returns>是否在多边形内</returns>
    public Boolean WithinPolygon(GeoPoint[] polygon)
    {
        if (polygon == null || polygon.Length < 3) return false;

        var inside = false;
        var n = polygon.Length;

        for (Int32 i = 0, j = n - 1; i < n; j = i++)
        {
            var yi = polygon[i].Latitude;
            var xi = polygon[i].Longitude;
            var yj = polygon[j].Latitude;
            var xj = polygon[j].Longitude;

            // 射线法：从测试点向右发射水平射线，统计与多边形边的交点数
            if ((yi > Latitude) != (yj > Latitude) &&
                Longitude < (xj - xi) * (Latitude - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>从 WKT 格式的多边形字符串解析顶点数组</summary>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>顶点数组</returns>
    public static GeoPoint[] ParsePolygonWkt(String wkt)
    {
        if (wkt == null) throw new ArgumentNullException(nameof(wkt));

        var trimmed = wkt.Trim();

        // 支持 POLYGON((lon lat, lon lat, ...)) 格式
        if (trimmed.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        {
            var start = trimmed.IndexOf("((", StringComparison.Ordinal);
            var end = trimmed.LastIndexOf("))", StringComparison.Ordinal);
            if (start < 0 || end < 0)
                throw new FormatException($"Invalid POLYGON WKT format: '{wkt}'");

            trimmed = trimmed.Substring(start + 2, end - start - 2);
        }

        var pointStrings = trimmed.Split(',');
        var points = new GeoPoint[pointStrings.Length];

        for (var i = 0; i < pointStrings.Length; i++)
        {
            var parts = pointStrings[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new FormatException($"Invalid coordinate in polygon: '{pointStrings[i]}'");

            // WKT 标准格式为 "经度 纬度"
            var lon = Double.Parse(parts[0].Trim());
            var lat = Double.Parse(parts[1].Trim());
            points[i] = new GeoPoint(lat, lon);
        }

        return points;
    }

    /// <summary>从字符串解析坐标点，格式为 "(lat, lon)"</summary>
    /// <param name="s">字符串</param>
    /// <returns>坐标点</returns>
    public static GeoPoint Parse(String s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));

        var trimmed = s.Trim();
        if (trimmed.StartsWith("(") && trimmed.EndsWith(")"))
            trimmed = trimmed.Substring(1, trimmed.Length - 2);

        var parts = trimmed.Split(',');
        if (parts.Length != 2)
            throw new FormatException($"Invalid GeoPoint format: '{s}', expected '(lat, lon)'");

        return new GeoPoint(Double.Parse(parts[0].Trim()), Double.Parse(parts[1].Trim()));
    }

    /// <summary>判断是否相等</summary>
    /// <param name="other">另一个坐标点</param>
    /// <returns>是否相等</returns>
    public Boolean Equals(GeoPoint other) => Latitude == other.Latitude && Longitude == other.Longitude;

    /// <summary>判断是否相等</summary>
    /// <param name="obj">对象</param>
    /// <returns>是否相等</returns>
    public override Boolean Equals(Object? obj) => obj is GeoPoint other && Equals(other);

    /// <summary>获取哈希码</summary>
    /// <returns>哈希码</returns>
    public override Int32 GetHashCode()
    {
        unchecked
        {
            return (Latitude.GetHashCode() * 397) ^ Longitude.GetHashCode();
        }
    }

    /// <summary>返回字符串表示</summary>
    /// <returns>字符串表示</returns>
    public override String ToString() => $"({Latitude}, {Longitude})";

    /// <summary>相等运算符</summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>是否相等</returns>
    public static Boolean operator ==(GeoPoint left, GeoPoint right) => left.Equals(right);

    /// <summary>不等运算符</summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>是否不等</returns>
    public static Boolean operator !=(GeoPoint left, GeoPoint right) => !left.Equals(right);
}

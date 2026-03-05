using System.Globalization;

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
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>纬度（-90 到 90）</summary>
    public Double Latitude { get; } = latitude;

    /// <summary>经度（-180 到 180）</summary>
    public Double Longitude { get; } = longitude;

    /// <summary>计算到另一个坐标点的距离（米），使用 Haversine 公式</summary>
    /// <param name="other">另一个坐标点</param>
    /// <returns>距离（米）</returns>
    public Double Distance(GeoPoint other)
    {
        var lat1 = Latitude * DegToRad;
        var lat2 = other.Latitude * DegToRad;
        var dLat = (other.Latitude - Latitude) * DegToRad;
        var dLon = (other.Longitude - Longitude) * DegToRad;

        var sinDLat = Math.Sin(dLat * 0.5);
        var sinDLon = Math.Sin(dLon * 0.5);

        var a = sinDLat * sinDLat + Math.Cos(lat1) * Math.Cos(lat2) * (sinDLon * sinDLon);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    ///// <summary>判断是否在指定中心点的半径范围内</summary>
    ///// <param name="center">中心坐标点</param>
    ///// <param name="radiusMeters">半径（米）</param>
    ///// <returns>是否在范围内</returns>
    //public Boolean WithinRadius(ref readonly GeoPoint center, Double radiusMeters) => Distance(center) <= radiusMeters;

    /// <summary>判断是否在指定中心点的半径范围内</summary>
    /// <param name="center">中心坐标点</param>
    /// <param name="radiusMeters">半径（米）</param>
    /// <returns>是否在范围内</returns>
    /// <remarks>用于半径判断的更快版本：不必算出最终距离（少一次 Atan2 + 少一次乘法）</remarks>
    public Boolean WithinRadius(ref readonly GeoPoint center, Double radiusMeters)
    {
        // 由 haversine：distance = R * 2 * asin(sqrt(a))
        // 比较 distance <= radius 等价于：asin(sqrt(a)) <= radius/(2R)
        // 再利用 asin 单调：sqrt(a) <= sin(radius/(2R)) => a <= sin^2(...)

        var max = radiusMeters / (2.0 * EarthRadiusMeters);
        var sinMax = Math.Sin(max);
        var aMax = sinMax * sinMax;

        var lat1 = Latitude * DegToRad;
        var lat2 = center.Latitude * DegToRad;
        var dLat = (center.Latitude - Latitude) * DegToRad;
        var dLon = (center.Longitude - Longitude) * DegToRad;

        var sinDLat = Math.Sin(dLat * 0.5);
        var sinDLon = Math.Sin(dLon * 0.5);

        var a = sinDLat * sinDLat + Math.Cos(lat1) * Math.Cos(lat2) * (sinDLon * sinDLon);

        return a <= aMax;
    }

    /// <summary>判断点是否在多边形内，使用射线法（Ray Casting）</summary>
    /// <param name="polygon">多边形顶点数组，首尾自动闭合</param>
    /// <returns>是否在多边形内</returns>
    public Boolean WithinPolygon(GeoPoint[] polygon) => polygon != null && polygon.Length >= 3 && WithinPolygon(polygon.AsSpan());

    /// <summary>判断点是否在多边形内，使用射线法（Ray Casting）</summary>
    /// <param name="polygon">多边形顶点数组，首尾自动闭合</param>
    /// <returns>是否在多边形内</returns>
    public Boolean WithinPolygon(ReadOnlySpan<GeoPoint> polygon)
    {
        var n = polygon.Length;
        if (n < 3) return false;

        // 将当前点坐标读到局部，避免循环内反复访问属性
        var y = Latitude;
        var x = Longitude;

        var inside = false;

        // 射线法（Ray Casting）：从测试点向右发射水平射线，统计与多边形边的交点数
        for (Int32 i = 0, j = n - 1; i < n; j = i++)
        {
            var yi = polygon[i].Latitude;
            var xi = polygon[i].Longitude;
            var yj = polygon[j].Latitude;
            var xj = polygon[j].Longitude;

            var intersect = yi > y != yj > y;
            if (!intersect) continue;

            var xIntersect = (xj - xi) * (y - yi) / (yj - yi) + xi;
            if (x < xIntersect)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>从字符串解析坐标点，格式为 "(lat, lon)"</summary>
    /// <param name="s">字符串</param>
    /// <returns>坐标点</returns>
    public static GeoPoint Parse(String s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return Parse(s.AsSpan());
#else
        var trimmed = s.Trim();
        if (trimmed.StartsWith("(") && trimmed.EndsWith(")"))
            trimmed = trimmed.Substring(1, trimmed.Length - 2);

        var parts = trimmed.Split(',');
        if (parts.Length != 2)
            throw new FormatException($"Invalid GeoPoint format: '{s}', expected '(lat, lon)'");

        var lat = Double.Parse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        var lon = Double.Parse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return new GeoPoint(lat, lon);
#endif
    }

    /// <summary>从 WKT 格式的多边形字符串解析顶点数组</summary>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>顶点数组</returns>
    public static GeoPoint[] ParsePolygonWkt(String wkt)
    {
        if (wkt == null) throw new ArgumentNullException(nameof(wkt));

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return ParsePolygonWkt(wkt.AsSpan());
#else
        var trimmed = wkt.Trim();

        // 支持 POLYGON((lon lat, lon lat, ...)) 格式
        if (trimmed.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        {
            var start = IndexOfDoubleParenOpen(trimmed.AsSpan());
            var end = LastIndexOfDoubleParenClose(trimmed.AsSpan());
            if (start < 0 || end < 0 || end <= start + 1)
                throw new FormatException($"Invalid POLYGON WKT format: '{wkt}'");

            trimmed = trimmed.Substring(start + 2, end - start - 2);
        }

        var pointStrings = trimmed.Split(',');
        var points = new GeoPoint[pointStrings.Length];

        var idx = 0;
        foreach (var pointString in pointStrings)
        {
            var parts = pointString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length < 2)
                throw new FormatException($"Invalid coordinate in polygon: '{pointString}'");

            // WKT 标准格式为 "经度 纬度"
            var lon = Double.Parse(parts[0].Trim());
            var lat = Double.Parse(parts[1].Trim());
            points[idx] = new GeoPoint(lat, lon);
            idx++;
        }

        if (idx != points.Length)
            Array.Resize(ref points, idx);

        return points;
#endif
    }

    /// <summary>
    /// 获取 WKT 格式的多边形字符串中坐标点的数量（逗号分隔的坐标对数量），不解析坐标值
    /// </summary>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>返回逗号分隔的坐标对数量，忽略空项；如果格式不正确（如缺少 POLYGON((...)) 包装），也会尽量统计逗号分隔的项数，而不是抛异常。</returns>
    public static Int32 GetPolygonWktCount(String wkt)
    {
        if (wkt == null) return 0;
        return GetPolygonWktCount(wkt.AsSpan());
    }

    /// <summary>
    /// 获取 WKT 格式的多边形字符串中坐标点的数量（逗号分隔的坐标对数量），不解析坐标值
    /// </summary>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>返回逗号分隔的坐标对数量，忽略空项；如果格式不正确（如缺少 POLYGON((...)) 包装），也会尽量统计逗号分隔的项数，而不是抛异常。</returns>
    public static Int32 GetPolygonWktCount(ReadOnlySpan<Char> wkt)
    {
        if (wkt == null) return 0;

        wkt = wkt.Trim();
        if (wkt.IsEmpty) return 0;

        //if (wkt.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        //{
        //    var start = IndexOfDoubleParenOpen(wkt);
        //    var end = LastIndexOfDoubleParenClose(wkt);
        //    if (start < 0 || end < 0 || end <= start + 1)
        //        throw new FormatException($"Invalid POLYGON WKT format: '{wkt.ToString()}'");
        //    wkt = wkt.Slice(start + 2, end - start - 2).Trim();
        //}

        var count = 0;
        while (!wkt.IsEmpty)
        {
            var comma = wkt.IndexOf(',');
            var item = comma >= 0 ? wkt.Slice(0, comma) : wkt;
            wkt = comma >= 0 ? wkt.Slice(comma + 1) : ReadOnlySpan<Char>.Empty;
            item = item.Trim();
            if (item.IsEmpty) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// 从 WKT 格式的多边形字符串解析坐标点到预分配的 <paramref name="points"/> 中，返回实际解析的点数量
    /// </summary>
    /// <param name="points">预分配的坐标点数组</param>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>实际解析的点数量</returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="FormatException"></exception>
    public static Int32 GetPolygonWkts(GeoPoint[] points, String wkt)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        if (wkt == null) throw new ArgumentNullException(nameof(wkt));

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return GetPolygonWkts(points.AsSpan(), wkt.AsSpan());
#else
        var trimmed = wkt.Trim();

        // 支持 POLYGON((lon lat, lon lat, ...)) 格式
        if (trimmed.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        {
            var start = IndexOfDoubleParenOpen(trimmed.AsSpan());
            var end = LastIndexOfDoubleParenClose(trimmed.AsSpan());
            if (start < 0 || end < 0 || end <= start + 1)
                throw new FormatException($"Invalid POLYGON WKT format: '{wkt}'");

            trimmed = trimmed.Substring(start + 2, end - start - 2);
        }

        var count = 0;
        var pointStrings = trimmed.Split(',');
        foreach (var pointString in pointStrings)
        {
            var parts = pointString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length < 2)
                throw new FormatException($"Invalid coordinate in polygon: '{pointString}'");

            // WKT 标准格式为 "经度 纬度"
            var lon = Double.Parse(parts[0].Trim());
            var lat = Double.Parse(parts[1].Trim());
            points[count] = new GeoPoint(lat, lon);
            count++;
        }
        return count;
#endif
    }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
    /// <summary>从字符串解析坐标点，格式为 "(lat, lon)"</summary>
    /// <param name="span">字符串</param>
    /// <returns>坐标点</returns>
    public static GeoPoint Parse(ReadOnlySpan<Char> span)
    {
        span = span.Trim();
        if (span.Length >= 2 && span[0] == '(' && span[^1] == ')')
            span = span.Slice(1, span.Length - 2).Trim();

        var comma = span.IndexOf(',');
        if (comma < 0)
            throw new FormatException($"Invalid GeoPoint format: '{span.ToString()}', expected '(lat, lon)'");

        var latSpan = span.Slice(0, comma).Trim();
        var lonSpan = span.Slice(comma + 1).Trim();

        var lat = Double.Parse(latSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        var lon = Double.Parse(lonSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new GeoPoint(lat, lon);
    }

    /// <summary>从 WKT 格式的多边形字符串解析顶点数组</summary>
    /// <param name="wkt">WKT 格式字符串，如 "POLYGON((lon1 lat1, lon2 lat2, ...))"</param>
    /// <returns>顶点数组</returns>
    public static GeoPoint[] ParsePolygonWkt(ReadOnlySpan<Char> wkt)
    {
        var s = wkt.Trim();

        // 支持 "POLYGON((lon lat, lon lat, ...))"
        if (s.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        {
            var start = IndexOfDoubleParenOpen(s);
            var end = LastIndexOfDoubleParenClose(s);
            if (start < 0 || end < 0 || end <= start + 1)
                throw new FormatException($"Invalid POLYGON WKT format: '{wkt.ToString()}'");

            s = s.Slice(start + 2, end - (start + 2)).Trim();
        }

        // 先数逗号，预分配 points
        var count = 1;
        foreach (var t in s)
            if (t == ',') count++;

        var points = new GeoPoint[count];

        var idx = 0;
        while (!s.IsEmpty)
        {
            var comma = s.IndexOf(',');
            var item = comma >= 0 ? s.Slice(0, comma) : s;
            s = comma >= 0 ? s.Slice(comma + 1) : ReadOnlySpan<Char>.Empty;

            item = item.Trim();
            if (item.IsEmpty) continue;

            // "lon lat"（空格分隔，可能有多空格）
            var sp = item.IndexOf(' ');
            if (sp < 0) throw new FormatException($"Invalid coordinate in polygon: '{item.ToString()}'");

            // 找到第一个非空格分隔点
            var lonSpan = item.Slice(0, sp).Trim();
            var rest = item.Slice(sp + 1).TrimStart();
            var sp2 = rest.IndexOf(' ');
            var latSpan = (sp2 >= 0 ? rest.Slice(0, sp2) : rest).Trim();

            var lon = Double.Parse(lonSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
            var lat = Double.Parse(latSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
            points[idx++] = new GeoPoint(lat, lon);
        }

        if (idx != points.Length)
            Array.Resize(ref points, idx);

        return points;
    }

    /// <summary>
    /// 从 WKT 格式的多边形字符串解析坐标点到预分配的 <paramref name="points"/> 中，返回实际解析的点数量
    /// </summary>
    /// <param name="points">预分配的坐标点数组</param>
    /// <param name="wkt">WKT 格式的多边形字符串</param>
    /// <returns>实际解析的点数量</returns>
    /// <exception cref="FormatException"></exception>
    public static Int32 GetPolygonWkts(Span<GeoPoint> points, ReadOnlySpan<Char> wkt)
    {
        var s = wkt.Trim();

        // 支持 "POLYGON((lon lat, lon lat, ...))"
        if (s.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase))
        {
            var start = IndexOfDoubleParenOpen(s);
            var end = LastIndexOfDoubleParenClose(s);
            if (start < 0 || end < 0 || end <= start + 1)
                throw new FormatException($"Invalid POLYGON WKT format: '{wkt.ToString()}'");

            s = s.Slice(start + 2, end - (start + 2)).Trim();
        }

        var count = 0;
        while (!s.IsEmpty)
        {
            var comma = s.IndexOf(',');
            var item = comma >= 0 ? s.Slice(0, comma) : s;
            s = comma >= 0 ? s.Slice(comma + 1) : ReadOnlySpan<Char>.Empty;
            item = item.Trim();
            if (item.IsEmpty) continue;
            var sp = item.IndexOf(' ');
            if (sp < 0) throw new FormatException($"Invalid coordinate in polygon: '{item.ToString()}'");
            var lonSpan = item.Slice(0, sp).Trim();
            var rest = item.Slice(sp + 1).TrimStart();
            var sp2 = rest.IndexOf(' ');
            var latSpan = (sp2 >= 0 ? rest.Slice(0, sp2) : rest).Trim();
            var lon = Double.Parse(lonSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
            var lat = Double.Parse(latSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
            points[count++] = new GeoPoint(lat, lon);
        }
        return count;
    }
#endif

    /// <summary>
    /// 从前往后找到第一个 "((" 的位置，返回索引；找不到返回 -1
    /// </summary>
    private static Int32 IndexOfDoubleParenOpen(ReadOnlySpan<Char> s)
    {
        for (var i = 0; i + 1 < s.Length; i++)
            if (s[i] == '(' && s[i + 1] == '(')
                return i;
        return -1;
    }

    /// <summary>
    /// 从后往前找到最后一个 "))" 的位置，返回索引；找不到返回 -1
    /// </summary>
    private static Int32 LastIndexOfDoubleParenClose(ReadOnlySpan<Char> s)
    {
        // 找最后一个 "))"
        for (var i = s.Length - 2; i >= 0; i--)
            if (s[i] == ')' && s[i + 1] == ')')
                return i;
        return -1;
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
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        return HashCode.Combine(Latitude, Longitude);
#else
        unchecked
        {
            return (Latitude.GetHashCode() * 397) ^ Longitude.GetHashCode();
        }
#endif
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

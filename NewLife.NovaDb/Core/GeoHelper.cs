namespace NewLife.NovaDb.Core;

/// <summary>地理工具类。提供国内常用坐标系（WGS84 / GCJ-02 / BD-09）互转与 geohash 编码</summary>
/// <remarks>
/// 国内地图底图（高德/腾讯/百度）使用加偏坐标系，GPS 原始数据为 WGS84：
/// - WGS84：GPS 原始经纬度（国际通用）
/// - GCJ-02：国测局坐标（高德/腾讯地图）
/// - BD-09：百度坐标（百度地图，在 GCJ-02 上二次加密）
/// 坐标系转换在 GIS 应用中为刚需（设备 GPS 数据 → 国内底图展示）。
/// </remarks>
public static class GeoHelper
{
    private const Double Pi = Math.PI;
    private const Double A = 6378245.0;   // 长半轴
    private const Double Ee = 0.00669342162296594323;  // 偏心率平方

    /// <summary>坐标系统</summary>
    public enum GeoCrs
    {
        /// <summary>WGS84 国际标准</summary>
        WGS84,
        /// <summary>GCJ-02 国测局坐标（高德/腾讯）</summary>
        GCJ02,
        /// <summary>BD-09 百度坐标</summary>
        BD09,
    }

    /// <summary>是否在中国境外（超出中国范围不进行偏移）</summary>
    /// <param name="lat">纬度</param>
    /// <param name="lon">经度</param>
    /// <returns>是否在境外</returns>
    public static Boolean OutOfChina(Double lat, Double lon)
        => lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271;

    #region WGS84 ↔ GCJ-02

    /// <summary>WGS84 转 GCJ-02</summary>
    /// <param name="lat">WGS84 纬度</param>
    /// <param name="lon">WGS84 经度</param>
    /// <returns>GCJ-02 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Wgs84ToGcj02(Double lat, Double lon)
    {
        if (OutOfChina(lat, lon)) return (lat, lon);

        var dLat = TransformLat(lon - 105.0, lat - 35.0);
        var dLon = TransformLon(lon - 105.0, lat - 35.0);
        var radLat = lat / 180.0 * Pi;
        var magic = Math.Sin(radLat);
        magic = 1 - Ee * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        dLat = dLat * 180.0 / ((A * (1 - Ee)) / (magic * sqrtMagic) * Pi);
        dLon = dLon * 180.0 / (A / sqrtMagic * Math.Cos(radLat) * Pi);

        return (lat + dLat, lon + dLon);
    }

    /// <summary>GCJ-02 转 WGS84</summary>
    /// <param name="lat">GCJ-02 纬度</param>
    /// <param name="lon">GCJ-02 经度</param>
    /// <returns>WGS84 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Gcj02ToWgs84(Double lat, Double lon)
    {
        if (OutOfChina(lat, lon)) return (lat, lon);

        var (gLat, gLon) = Wgs84ToGcj02(lat, lon);
        return (lat * 2 - gLat, lon * 2 - gLon);
    }

    #endregion

    #region GCJ-02 ↔ BD-09

    /// <summary>GCJ-02 转 BD-09</summary>
    /// <param name="lat">GCJ-02 纬度</param>
    /// <param name="lon">GCJ-02 经度</param>
    /// <returns>BD-09 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Gcj02ToBd09(Double lat, Double lon)
    {
        var z = Math.Sqrt(lon * lon + lat * lat) + 0.00002 * Math.Sin(lat * Pi * 3000.0 / 180.0);
        var theta = Math.Atan2(lat, lon) + 0.000003 * Math.Cos(lon * Pi * 3000.0 / 180.0);
        var bdLon = z * Math.Cos(theta) + 0.0065;
        var bdLat = z * Math.Sin(theta) + 0.006;
        return (bdLat, bdLon);
    }

    /// <summary>BD-09 转 GCJ-02</summary>
    /// <param name="lat">BD-09 纬度</param>
    /// <param name="lon">BD-09 经度</param>
    /// <returns>GCJ-02 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Bd09ToGcj02(Double lat, Double lon)
    {
        var x = lon - 0.0065;
        var y = lat - 0.006;
        var z = Math.Sqrt(x * x + y * y) - 0.00002 * Math.Sin(y * Pi * 3000.0 / 180.0);
        var theta = Math.Atan2(y, x) - 0.000003 * Math.Cos(x * Pi * 3000.0 / 180.0);
        var gcjLon = z * Math.Cos(theta);
        var gcjLat = z * Math.Sin(theta);
        return (gcjLat, gcjLon);
    }

    #endregion

    #region WGS84 ↔ BD-09

    /// <summary>WGS84 转 BD-09（先转 GCJ-02 再转 BD-09）</summary>
    /// <param name="lat">WGS84 纬度</param>
    /// <param name="lon">WGS84 经度</param>
    /// <returns>BD-09 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Wgs84ToBd09(Double lat, Double lon)
    {
        var (gLat, gLon) = Wgs84ToGcj02(lat, lon);
        return Gcj02ToBd09(gLat, gLon);
    }

    /// <summary>BD-09 转 WGS84（先转 GCJ-02 再转 WGS84）</summary>
    /// <param name="lat">BD-09 纬度</param>
    /// <param name="lon">BD-09 经度</param>
    /// <returns>WGS84 坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Bd09ToWgs84(Double lat, Double lon)
    {
        var (gLat, gLon) = Bd09ToGcj02(lat, lon);
        return Gcj02ToWgs84(gLat, gLon);
    }

    /// <summary>通用坐标系转换</summary>
    /// <param name="lat">源纬度</param>
    /// <param name="lon">源经度</param>
    /// <param name="from">源坐标系</param>
    /// <param name="to">目标坐标系</param>
    /// <returns>目标坐标系坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) Transform(Double lat, Double lon, GeoCrs from, GeoCrs to)
    {
        if (from == to) return (lat, lon);

        return (from, to) switch
        {
            (GeoCrs.WGS84, GeoCrs.GCJ02) => Wgs84ToGcj02(lat, lon),
            (GeoCrs.WGS84, GeoCrs.BD09) => Wgs84ToBd09(lat, lon),
            (GeoCrs.GCJ02, GeoCrs.WGS84) => Gcj02ToWgs84(lat, lon),
            (GeoCrs.GCJ02, GeoCrs.BD09) => Gcj02ToBd09(lat, lon),
            (GeoCrs.BD09, GeoCrs.WGS84) => Bd09ToWgs84(lat, lon),
            (GeoCrs.BD09, GeoCrs.GCJ02) => Bd09ToGcj02(lat, lon),
            _ => (lat, lon),
        };
    }

    #endregion

    #region Geohash

    /// <summary>geohash 基础字符集（32 个字符，Base32 编码）</summary>
    private const String GeoHashBase32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    /// <summary>编码坐标点为 geohash 字符串</summary>
    /// <param name="lat">纬度（-90 到 90）</param>
    /// <param name="lon">经度（-180 到 180）</param>
    /// <param name="precision">精度（字符数，默认 8，约 19m×19m）</param>
    /// <returns>geohash 字符串</returns>
    public static String EncodeGeohash(Double lat, Double lon, Int32 precision = 8)
    {
        if (precision < 1) throw new ArgumentOutOfRangeException(nameof(precision));
        if (precision > 12) precision = 12;

        var latMin = -90.0;
        var latMax = 90.0;
        var lonMin = -180.0;
        var lonMax = 180.0;

        var sb = new System.Text.StringBuilder(precision);
        var even = true;
        var bit = 0;
        var ch = 0;

        while (sb.Length < precision)
        {
            if (even)
            {
                var mid = (lonMin + lonMax) / 2;
                if (lon >= mid)
                {
                    ch = ch * 2 + 1;
                    lonMin = mid;
                }
                else
                {
                    ch = ch * 2;
                    lonMax = mid;
                }
            }
            else
            {
                var mid = (latMin + latMax) / 2;
                if (lat >= mid)
                {
                    ch = ch * 2 + 1;
                    latMin = mid;
                }
                else
                {
                    ch = ch * 2;
                    latMax = mid;
                }
            }

            even = !even;

            if (++bit == 5)
            {
                sb.Append(GeoHashBase32[ch]);
                bit = 0;
                ch = 0;
            }
        }

        return sb.ToString();
    }

    /// <summary>获取指定 geohash 前缀的所有邻居前缀（8 个方向，含自身）</summary>
    /// <param name="geohash">geohash 前缀</param>
    /// <returns>含自身在内的 9 个相邻前缀</returns>
    public static String[] GetNeighbors(String geohash)
    {
        if (String.IsNullOrEmpty(geohash)) return [];

        var (lat, lon) = DecodeGeohash(geohash);

        // 以当前位置为中心，计算 8 个方向的邻居前缀
        // 使用前缀长度对应的格子尺寸的偏移
        var cellSize = GetCellSize(geohash.Length);

        var result = new HashSet<String>(StringComparer.Ordinal)
        {
            geohash
        };

        for (var dLat = -1; dLat <= 1; dLat++)
        {
            for (var dLon = -1; dLon <= 1; dLon++)
            {
                if (dLat == 0 && dLon == 0) continue;
                result.Add(EncodeGeohash(lat + dLat * cellSize.Lat, lon + dLon * cellSize.Lon, geohash.Length));
            }
        }

        return result.ToArray();
    }

    /// <summary>解码 geohash 字符串为坐标（返回格子中心点）</summary>
    /// <param name="geohash">geohash 字符串</param>
    /// <returns>中心点坐标（纬度, 经度）</returns>
    public static (Double Lat, Double Lon) DecodeGeohash(String geohash)
    {
        if (String.IsNullOrEmpty(geohash)) throw new ArgumentNullException(nameof(geohash));

        var latMin = -90.0;
        var latMax = 90.0;
        var lonMin = -180.0;
        var lonMax = 180.0;

        var even = true;
        foreach (var c in geohash)
        {
            var cd = GeoHashBase32.IndexOf(c);
            if (cd < 0) throw new FormatException($"Invalid geohash character: '{c}'");

            for (var mask = 16; mask != 0; mask >>= 1)
            {
                if (even)
                {
                    var mid = (lonMin + lonMax) / 2;
                    if ((cd & mask) != 0)
                        lonMin = mid;
                    else
                        lonMax = mid;
                }
                else
                {
                    var mid = (latMin + latMax) / 2;
                    if ((cd & mask) != 0)
                        latMin = mid;
                    else
                        latMax = mid;
                }

                even = !even;
            }
        }

        return ((latMin + latMax) / 2, (lonMin + lonMax) / 2);
    }

    /// <summary>获取指定精度的格子尺寸（约）</summary>
    /// <param name="precision">精度（字符数）</param>
    /// <returns>格子尺寸（纬度差, 经度差）</returns>
    private static (Double Lat, Double Lon) GetCellSize(Int32 precision)
    {
        // 每个 geohash 字符代表 5 位，纬度和经度各占约 2.5 位
        // 粗略近似：纬度范围 180 度 / 2^ceil(bits/2)，经度 360 / 2^floor(bits/2)
        var bits = precision * 5;
        var latBits = (bits + 1) / 2;
        var lonBits = bits / 2;

        var latCells = Math.Pow(2, latBits);
        var lonCells = Math.Pow(2, lonBits);

        return (180.0 / latCells, 360.0 / lonCells);
    }

    #endregion

    #region 辅助

    private static Double TransformLat(Double x, Double y)
        => -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x))
           + (20.0 * Math.Sin(6.0 * x * Pi) + 20.0 * Math.Sin(2.0 * x * Pi)) * 2.0 / 3.0
           + (20.0 * Math.Sin(y * Pi) + 40.0 * Math.Sin(y / 3.0 * Pi)) * 2.0 / 3.0
           + (160.0 * Math.Sin(y / 12.0 * Pi) + 320 * Math.Sin(y * Pi / 30.0)) * 2.0 / 3.0;

    private static Double TransformLon(Double x, Double y)
        => 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x))
           + (20.0 * Math.Sin(6.0 * x * Pi) + 20.0 * Math.Sin(2.0 * x * Pi)) * 2.0 / 3.0
           + (20.0 * Math.Sin(x * Pi) + 40.0 * Math.Sin(x / 3.0 * Pi)) * 2.0 / 3.0
           + (150.0 * Math.Sin(x / 12.0 * Pi) + 300.0 * Math.Sin(x / 30.0 * Pi)) * 2.0 / 3.0;

    #endregion
}

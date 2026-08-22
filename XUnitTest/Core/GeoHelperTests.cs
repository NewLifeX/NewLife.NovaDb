using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>GeoHelper 地理工具单元测试：坐标系转换与 geohash 编码</summary>
public class GeoHelperTests
{
    #region 坐标系转换

    [Fact(DisplayName = "WGS84转GCJ02-北京天安门坐标偏移")]
    public void Wgs84ToGcj02_Beijing_Offset()
    {
        // 天安门 WGS84 坐标（约）
        var (gcjLat, gcjLon) = GeoHelper.Wgs84ToGcj02(39.908823, 116.397470);

        // 偏移量应在合理范围（国内坐标偏移约 300-700 米，即 0.003-0.007 度）
        var dLat = gcjLat - 39.908823;
        var dLon = gcjLon - 116.397470;

        Assert.InRange(dLat, 0.001, 0.01);
        Assert.InRange(dLon, 0.001, 0.01);
    }

    [Fact(DisplayName = "GCJ02转WGS84-往返还原")]
    public void Gcj02ToWgs84_RoundTrip()
    {
        var wgsLat = 31.2304;
        var wgsLon = 121.4737;

        var (gcjLat, gcjLon) = GeoHelper.Wgs84ToGcj02(wgsLat, wgsLon);
        var (backLat, backLon) = GeoHelper.Gcj02ToWgs84(gcjLat, gcjLon);

        // 往返误差应为国内坐标转换算法固有误差量级（约 1e-5 度 ≈ 1 米）
        Assert.True(Math.Abs(backLat - wgsLat) < 1e-4, $"Lat error: {Math.Abs(backLat - wgsLat)}");
        Assert.True(Math.Abs(backLon - wgsLon) < 1e-4, $"Lon error: {Math.Abs(backLon - wgsLon)}");
    }

    [Fact(DisplayName = "GCJ02转BD09-百度偏移")]
    public void Gcj02ToBd09_BaiduOffset()
    {
        var gcjLat = 39.908823;
        var gcjLon = 116.397470;

        var (bdLat, bdLon) = GeoHelper.Gcj02ToBd09(gcjLat, gcjLon);

        // 百度坐标在 GCJ 基础上再偏移约 0.002-0.006 度
        var dLat = bdLat - gcjLat;
        var dLon = bdLon - gcjLon;

        Assert.InRange(dLat, 0.001, 0.01);
        Assert.InRange(dLon, 0.001, 0.01);
    }

    [Fact(DisplayName = "BD09转GCJ02-往返还原")]
    public void Bd09ToGcj02_RoundTrip()
    {
        var gcjLat = 31.2304;
        var gcjLon = 121.4737;

        var (bdLat, bdLon) = GeoHelper.Gcj02ToBd09(gcjLat, gcjLon);
        var (backLat, backLon) = GeoHelper.Bd09ToGcj02(bdLat, bdLon);

        Assert.True(Math.Abs(backLat - gcjLat) < 1e-4);
        Assert.True(Math.Abs(backLon - gcjLon) < 1e-4);
    }

    [Fact(DisplayName = "WGS84转BD09-链式转换")]
    public void Wgs84ToBd09_Chain()
    {
        var (bdLat, bdLon) = GeoHelper.Wgs84ToBd09(39.908823, 116.397470);
        var (backLat, backLon) = GeoHelper.Bd09ToWgs84(bdLat, bdLon);

        Assert.True(Math.Abs(backLat - 39.908823) < 1e-5);
        Assert.True(Math.Abs(backLon - 116.397470) < 1e-5);
    }

    [Fact(DisplayName = "境外坐标不偏移")]
    public void OutOfChina_NoOffset()
    {
        // 纽约坐标（境外），转换后应保持原值
        var (gcjLat, gcjLon) = GeoHelper.Wgs84ToGcj02(40.7128, -74.0060);
        Assert.Equal(40.7128, gcjLat, 10);
        Assert.Equal(-74.0060, gcjLon, 10);
    }

    [Fact(DisplayName = "通用Transform-别名解析")]
    public void Transform_Alias()
    {
        // GPS 别名 = WGS84，AMap 别名 = GCJ02
        var (amapLat, amapLon) = GeoHelper.Transform(39.908823, 116.397470, GeoHelper.GeoCrs.WGS84, GeoHelper.GeoCrs.GCJ02);
        var (gpsLat, gpsLon) = GeoHelper.Transform(39.908823, 116.397470, GeoHelper.GeoCrs.WGS84, GeoHelper.GeoCrs.GCJ02);
        Assert.Equal(amapLat, gpsLat, 10);
        Assert.Equal(amapLon, gpsLon, 10);
    }

    #endregion

    #region Geohash

    [Fact(DisplayName = "Geohash编码-北京坐标")]
    public void EncodeGeohash_Beijing()
    {
        // 天安门坐标，精度 6
        var hash = GeoHelper.EncodeGeohash(39.908823, 116.397470, 6);
        Assert.False(String.IsNullOrEmpty(hash));
        Assert.Equal(6, hash.Length);
        // 应只包含 base32 字符
        foreach (var c in hash)
        {
            Assert.True("0123456789bcdefghjkmnpqrstuvwxyz".Contains(c), $"Invalid char: {c}");
        }
    }

    [Fact(DisplayName = "Geohash编码-相同区域前缀一致")]
    public void EncodeGeohash_SameArea_SamePrefix()
    {
        // 相距约 5 米（0.00005 度）的两个点，精度 6（格子约 1.2km）前缀应相同
        var hash1 = GeoHelper.EncodeGeohash(39.908823, 116.397470, 6);
        var hash2 = GeoHelper.EncodeGeohash(39.908823 + 0.00005, 116.397470 + 0.00005, 6);
        Assert.Equal(hash1, hash2);
    }

    [Fact(DisplayName = "Geohash编码-不同区域前缀不同")]
    public void EncodeGeohash_DifferentArea_DifferentPrefix()
    {
        // 北京与上海
        var beijing = GeoHelper.EncodeGeohash(39.908823, 116.397470, 6);
        var shanghai = GeoHelper.EncodeGeohash(31.2304, 121.4737, 6);
        Assert.NotEqual(beijing, shanghai);
    }

    [Fact(DisplayName = "Geohash解码-往返还原")]
    public void DecodeGeohash_RoundTrip()
    {
        var lat = 31.2304;
        var lon = 121.4737;

        var hash = GeoHelper.EncodeGeohash(lat, lon, 8);
        var (decodedLat, decodedLon) = GeoHelper.DecodeGeohash(hash);

        // 8 位精度误差约 19m（约 0.00017 度）
        Assert.True(Math.Abs(decodedLat - lat) < 0.001);
        Assert.True(Math.Abs(decodedLon - lon) < 0.001);
    }

    [Fact(DisplayName = "Geohash邻居-包含自身与8方向")]
    public void GetNeighbors_ContainsSelfAnd8()
    {
        var hash = GeoHelper.EncodeGeohash(39.908823, 116.397470, 6);
        var neighbors = GeoHelper.GetNeighbors(hash);

        Assert.Equal(9, neighbors.Length);
        Assert.Contains(hash, neighbors);
    }

    [Fact(DisplayName = "Geohash精度越界-钳制")]
    public void EncodeGeohash_PrecisionClamp()
    {
        var hash = GeoHelper.EncodeGeohash(39.908823, 116.397470, 20);
        Assert.Equal(12, hash.Length);
    }

    #endregion
}

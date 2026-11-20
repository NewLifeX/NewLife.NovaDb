using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Localization;

/// <summary>数据语义跨区域性一致性测试。SQL 数值/日期解析与格式化、标识符大小写不受进程区域性影响</summary>
/// <remarks>
/// 数据库文本协议必须使用固定区域性：非英文系统（如 de-DE 用逗号作小数点、tr-TR 的 i/I 大小写规则）
/// 若按当前区域性解析或格式化，会出现字面量 1.5 变成 15、标识符 Int32 变成 ınt32 一类数据错误。
/// 与 <see cref="LocalizationTests"/> 同属 Localization 集合，避免全局区域性状态互相干扰。
/// </remarks>
[Collection("Localization")]
public class CultureInvariantTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public CultureInvariantTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CultureInvariantTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();

        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }

    private static T WithCulture<T>(String name, Func<T> func)
    {
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUICulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return func();
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUICulture;
        }
    }

    [Fact(DisplayName = "德文区域下 SQL 浮点字面量按固定区域性解析")]
    public void SqlFloatLiteral_InvariantUnderGermanCulture()
    {
        _engine.Execute("CREATE TABLE t_price (id INT PRIMARY KEY, amount DOUBLE)");

        // de-DE 以逗号作小数点，按当前区域性解析会把 2.5 读成 25
        WithCulture("de-DE", () => _engine.Execute("INSERT INTO t_price VALUES (1, 2.5), (2, 3.75)"));

        var result = WithCulture("de-DE", () => _engine.Execute("SELECT SUM(amount) FROM t_price"));

        Assert.Equal(6.25, Convert.ToDouble(result.GetScalar(), CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "德文区域下数值转字符串使用固定区域性")]
    public void SqlCastNumberToString_InvariantUnderGermanCulture()
    {
        var result = WithCulture("de-DE", () => _engine.Execute("SELECT CAST(2.5 AS VARCHAR)"));

        Assert.Equal("2.5", result.GetScalar());
    }

    [Fact(DisplayName = "中文区域下 DATE_FORMAT 月份名使用固定区域性")]
    public void SqlDateFormat_InvariantUnderChineseCulture()
    {
        var result = WithCulture("zh-CN", () => _engine.Execute("SELECT DATE_FORMAT('2026-01-15 08:30:00', '%M')"));

        Assert.Equal("January", result.GetScalar());
    }

    [Fact(DisplayName = "土耳其区域下 UPPER/LOWER 使用固定区域性大小写规则")]
    public void SqlUpperLower_InvariantUnderTurkishCulture()
    {
        // tr-TR 中 'i' 的大写为 'İ'，数据库函数必须按不变区域性得到 'I'
        Assert.Equal("I", WithCulture("tr-TR", () => _engine.Execute("SELECT UPPER('i')")).GetScalar());
        Assert.Equal("i", WithCulture("tr-TR", () => _engine.Execute("SELECT LOWER('I')")).GetScalar());
    }

    [Fact(DisplayName = "土耳其区域下 SHOW COLUMNS 类型名小写不受区域性影响")]
    public void ShowColumns_TypeNameLowerInvariantUnderTurkishCulture()
    {
        _engine.Execute("CREATE TABLE t_show (id INT PRIMARY KEY, name VARCHAR(64))");

        var result = WithCulture("tr-TR", () => _engine.Execute("SHOW COLUMNS FROM t_show"));
        var type = result.Rows.First(r => r[0] as String == "id")[1];

        // 非不变小写会得到土耳其语无点 i：ınt
        Assert.Equal("int", type);
    }

    [Fact(DisplayName = "德文区域下 GeoPoint 文本解析使用固定区域性")]
    public void GeoPointParse_InvariantUnderGermanCulture()
    {
        var point = WithCulture("de-DE", () => GeoPoint.Parse("(39.9, 116.4)"));

        Assert.Equal(39.9, point.Latitude, 6);
        Assert.Equal(116.4, point.Longitude, 6);
    }

    [Fact(DisplayName = "德文区域下消息 ID 往返保持一致")]
    public void MessageId_RoundTripUnderGermanCulture()
    {
        var id = WithCulture("de-DE", () => MessageId.Parse("1700000000000000000-7"));

        Assert.Equal(1700000000000000000L, id.Timestamp);
        Assert.Equal(7, id.Sequence);
        Assert.Equal("1700000000000000000-7", id.ToString());
    }
}

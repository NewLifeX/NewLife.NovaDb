using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>SQL 标量函数与扩展聚合函数单元测试</summary>
public class SqlFunctionTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public SqlFunctionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SqlFuncTests_{Guid.NewGuid():N}");
        _engine = new SqlEngine(_testDir, new DbOptions { Path = _testDir, WalMode = WalMode.None });
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    private void CreateUsersTable()
    {
        _engine.Execute("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (2, 'Bob', 25)");
        _engine.Execute("INSERT INTO users (id, name, age) VALUES (3, 'Charlie', 35)");
    }

    #region 字符串函数

    [Fact(DisplayName = "UPPER/LOWER 大小写转换")]
    public void TestUpperLower()
    {
        var r = _engine.Execute("SELECT UPPER('hello'), LOWER('WORLD')");
        Assert.Equal("HELLO", r.Rows[0][0]);
        Assert.Equal("world", r.Rows[0][1]);
    }

    [Fact(DisplayName = "CONCAT 字符串拼接")]
    public void TestConcat()
    {
        var r = _engine.Execute("SELECT CONCAT('Hello', ' ', 'World')");
        Assert.Equal("Hello World", r.Rows[0][0]);
    }

    [Fact(DisplayName = "LENGTH/LEN 字符串长度")]
    public void TestLength()
    {
        var r = _engine.Execute("SELECT LENGTH('Hello')");
        Assert.Equal(5, r.Rows[0][0]);
    }

    [Fact(DisplayName = "SUBSTRING 子串截取")]
    public void TestSubstring()
    {
        var r = _engine.Execute("SELECT SUBSTRING('Hello World', 7, 5)");
        Assert.Equal("World", r.Rows[0][0]);
    }

    [Fact(DisplayName = "TRIM/LTRIM/RTRIM 空白裁剪")]
    public void TestTrim()
    {
        var r = _engine.Execute("SELECT TRIM('  hi  '), LTRIM('  hi'), RTRIM('hi  ')");
        Assert.Equal("hi", r.Rows[0][0]);
        Assert.Equal("hi", r.Rows[0][1]);
        Assert.Equal("hi", r.Rows[0][2]);
    }

    [Fact(DisplayName = "REPLACE 字符串替换")]
    public void TestReplace()
    {
        var r = _engine.Execute("SELECT REPLACE('Hello World', 'World', 'NovaDb')");
        Assert.Equal("Hello NovaDb", r.Rows[0][0]);
    }

    [Fact(DisplayName = "LEFT/RIGHT 左右截取")]
    public void TestLeftRight()
    {
        var r = _engine.Execute("SELECT LEFT('Hello', 3), RIGHT('Hello', 3)");
        Assert.Equal("Hel", r.Rows[0][0]);
        Assert.Equal("llo", r.Rows[0][1]);
    }

    [Fact(DisplayName = "CHARINDEX 子串查找")]
    public void TestCharIndex()
    {
        var r = _engine.Execute("SELECT CHARINDEX('World', 'Hello World')");
        Assert.Equal(7, r.Rows[0][0]);
    }

    [Fact(DisplayName = "CHARINDEX 未找到返回 0")]
    public void TestCharIndexNotFound()
    {
        var r = _engine.Execute("SELECT CHARINDEX('xyz', 'Hello World')");
        Assert.Equal(0, r.Rows[0][0]);
    }

    [Fact(DisplayName = "REVERSE 字符串翻转")]
    public void TestReverse()
    {
        var r = _engine.Execute("SELECT REVERSE('abc')");
        Assert.Equal("cba", r.Rows[0][0]);
    }

    [Fact(DisplayName = "LPAD/RPAD 填充")]
    public void TestPad()
    {
        var r = _engine.Execute("SELECT LPAD('hi', 5, '0'), RPAD('hi', 5, '0')");
        Assert.Equal("000hi", r.Rows[0][0]);
        Assert.Equal("hi000", r.Rows[0][1]);
    }

    #endregion

    #region 数值函数

    [Fact(DisplayName = "ABS 绝对值")]
    public void TestAbs()
    {
        var r = _engine.Execute("SELECT ABS(-5)");
        Assert.Equal(5.0, r.Rows[0][0]);
    }

    [Fact(DisplayName = "ROUND 四舍五入")]
    public void TestRound()
    {
        var r = _engine.Execute("SELECT ROUND(3.567, 2)");
        Assert.Equal(3.57, r.Rows[0][0]);
    }

    [Fact(DisplayName = "CEILING/FLOOR 上取整/下取整")]
    public void TestCeilingFloor()
    {
        var r = _engine.Execute("SELECT CEILING(2.1), FLOOR(2.9)");
        Assert.Equal(3.0, r.Rows[0][0]);
        Assert.Equal(2.0, r.Rows[0][1]);
    }

    [Fact(DisplayName = "MOD 取模")]
    public void TestMod()
    {
        var r = _engine.Execute("SELECT MOD(10, 3)");
        Assert.Equal(1.0, r.Rows[0][0]);
    }

    [Fact(DisplayName = "% 取模运算符")]
    public void TestPercentOperator()
    {
        var r = _engine.Execute("SELECT 10 % 3");
        Assert.Equal(1.0, r.Rows[0][0]);
    }

    [Fact(DisplayName = "POWER/SQRT 幂和平方根")]
    public void TestPowerSqrt()
    {
        var r = _engine.Execute("SELECT POWER(2, 10), SQRT(144)");
        Assert.Equal(1024.0, r.Rows[0][0]);
        Assert.Equal(12.0, r.Rows[0][1]);
    }

    [Fact(DisplayName = "SIGN 符号")]
    public void TestSign()
    {
        var r = _engine.Execute("SELECT SIGN(-5), SIGN(0), SIGN(3)");
        Assert.Equal(-1, r.Rows[0][0]);
        Assert.Equal(0, r.Rows[0][1]);
        Assert.Equal(1, r.Rows[0][2]);
    }

    [Fact(DisplayName = "TRUNCATE 截断")]
    public void TestTruncate()
    {
        var r = _engine.Execute("SELECT TRUNCATE(3.789, 1)");
        Assert.Equal(3.7, r.Rows[0][0]);
    }

    [Fact(DisplayName = "PI 圆周率")]
    public void TestPi()
    {
        var r = _engine.Execute("SELECT PI()");
        Assert.Equal(Math.PI, r.Rows[0][0]);
    }

    [Fact(DisplayName = "EXP/LOG/LOG10 指数对数")]
    public void TestExpLog()
    {
        var r = _engine.Execute("SELECT EXP(0), LOG10(100)");
        Assert.Equal(1.0, r.Rows[0][0]);
        Assert.Equal(2.0, r.Rows[0][1]);
    }

    #endregion

    #region 日期时间函数

    [Fact(DisplayName = "NOW 获取当前时间")]
    public void TestNow()
    {
        var r = _engine.Execute("SELECT NOW()");
        Assert.IsType<DateTime>(r.Rows[0][0]);
    }

    [Fact(DisplayName = "YEAR/MONTH/DAY 日期部分提取")]
    public void TestYearMonthDay()
    {
        CreateUsersTable();
        // 使用字符串表示日期
        var r = _engine.Execute("SELECT YEAR('2025-06-15'), MONTH('2025-06-15'), DAY('2025-06-15')");
        Assert.Equal(2025, r.Rows[0][0]);
        Assert.Equal(6, r.Rows[0][1]);
        Assert.Equal(15, r.Rows[0][2]);
    }

    [Fact(DisplayName = "HOUR/MINUTE/SECOND 时间部分提取")]
    public void TestHourMinuteSecond()
    {
        var r = _engine.Execute("SELECT HOUR('2025-06-15 14:30:45'), MINUTE('2025-06-15 14:30:45'), SECOND('2025-06-15 14:30:45')");
        Assert.Equal(14, r.Rows[0][0]);
        Assert.Equal(30, r.Rows[0][1]);
        Assert.Equal(45, r.Rows[0][2]);
    }

    [Fact(DisplayName = "DATEDIFF 日期差值")]
    public void TestDateDiff()
    {
        var r = _engine.Execute("SELECT DATEDIFF('2025-06-15', '2025-06-10')");
        Assert.Equal(5, r.Rows[0][0]);
    }

    [Fact(DisplayName = "DATEADD 日期加减")]
    public void TestDateAdd()
    {
        var r = _engine.Execute("SELECT DATEADD('DAY', 5, '2025-06-10')");
        Assert.Equal(new DateTime(2025, 6, 15), r.Rows[0][0]);
    }

    [Fact(DisplayName = "LAST_DAY 月末日期")]
    public void TestLastDay()
    {
        var r = _engine.Execute("SELECT LAST_DAY('2025-02-10')");
        Assert.Equal(new DateTime(2025, 2, 28), r.Rows[0][0]);
    }

    [Fact(DisplayName = "CURRENT_TIMESTAMP 无括号")]
    public void TestCurrentTimestamp()
    {
        var r = _engine.Execute("SELECT CURRENT_TIMESTAMP");
        Assert.IsType<DateTime>(r.Rows[0][0]);
    }

    [Fact(DisplayName = "DATE_FORMAT 日期格式化")]
    public void TestDateFormat()
    {
        var r = _engine.Execute("SELECT DATE_FORMAT('2025-06-15 14:30:45', '%Y-%m-%d')");
        Assert.Equal("2025-06-15", r.Rows[0][0]);
    }

    [Fact(DisplayName = "DATE_FORMAT 时间格式化")]
    public void TestDateFormatTime()
    {
        var r = _engine.Execute("SELECT DATE_FORMAT('2025-06-15 14:30:45', '%H:%i:%s')");
        Assert.Equal("14:30:45", r.Rows[0][0]);
    }

    [Fact(DisplayName = "TIMESTAMPDIFF 日期差值按天")]
    public void TestTimestampDiffDays()
    {
        var r = _engine.Execute("SELECT TIMESTAMPDIFF('DAY', '2025-06-10', '2025-06-15')");
        Assert.Equal(5, Convert.ToInt32(r.Rows[0][0]));
    }

    [Fact(DisplayName = "TIMESTAMPDIFF 日期差值按月")]
    public void TestTimestampDiffMonths()
    {
        var r = _engine.Execute("SELECT TIMESTAMPDIFF('MONTH', '2025-01-15', '2025-06-15')");
        Assert.Equal(5, Convert.ToInt32(r.Rows[0][0]));
    }

    [Fact(DisplayName = "TIMESTAMPDIFF 日期差值按小时")]
    public void TestTimestampDiffHours()
    {
        var r = _engine.Execute("SELECT TIMESTAMPDIFF('HOUR', '2025-06-15 10:00:00', '2025-06-15 14:00:00')");
        Assert.Equal(4, Convert.ToInt32(r.Rows[0][0]));
    }

    #endregion

    #region 类型转换

    [Fact(DisplayName = "CAST 整型转换")]
    public void TestCastInt()
    {
        var r = _engine.Execute("SELECT CAST(3.14 AS INT)");
        Assert.Equal(3, r.Rows[0][0]);
    }

    [Fact(DisplayName = "CAST 字符串转换")]
    public void TestCastVarchar()
    {
        var r = _engine.Execute("SELECT CAST(42 AS VARCHAR)");
        Assert.Equal("42", r.Rows[0][0]);
    }

    [Fact(DisplayName = "CONVERT 类型转换")]
    public void TestConvert()
    {
        var r = _engine.Execute("SELECT CONVERT('INT', '123')");
        Assert.Equal(123, r.Rows[0][0]);
    }

    [Fact(DisplayName = "COALESCE 第一个非空值")]
    public void TestCoalesce()
    {
        var r = _engine.Execute("SELECT COALESCE(NULL, NULL, 'hello', 'world')");
        Assert.Equal("hello", r.Rows[0][0]);
    }

    [Fact(DisplayName = "ISNULL 空值替换")]
    public void TestIsNull()
    {
        var r = _engine.Execute("SELECT ISNULL(NULL, 'default')");
        Assert.Equal("default", r.Rows[0][0]);
    }

    [Fact(DisplayName = "NULLIF 相等返回 NULL")]
    public void TestNullIf()
    {
        var r = _engine.Execute("SELECT NULLIF(1, 1), NULLIF(1, 2)");
        Assert.Null(r.Rows[0][0]);
        Assert.NotNull(r.Rows[0][1]);
    }

    #endregion

    #region 条件函数

    [Fact(DisplayName = "CASE WHEN 条件表达式")]
    public void TestCaseWhen()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT name, CASE WHEN age > 30 THEN 'senior' WHEN age > 20 THEN 'mid' ELSE 'young' END AS category FROM users ORDER BY id");
        Assert.Equal("mid", r.Rows[0][1]);      // Alice 30
        Assert.Equal("mid", r.Rows[1][1]);      // Bob 25
        Assert.Equal("senior", r.Rows[2][1]);    // Charlie 35
    }

    [Fact(DisplayName = "IF 三元函数")]
    public void TestIfFunction()
    {
        var r = _engine.Execute("SELECT IF(1 > 0, 'yes', 'no')");
        Assert.Equal("yes", r.Rows[0][0]);
    }

    [Fact(DisplayName = "IIF 三元函数")]
    public void TestIifFunction()
    {
        var r = _engine.Execute("SELECT IIF(1 < 0, 'yes', 'no')");
        Assert.Equal("no", r.Rows[0][0]);
    }

    #endregion

    #region 系统函数

    [Fact(DisplayName = "VERSION 版本")]
    public void TestVersion()
    {
        var r = _engine.Execute("SELECT VERSION()");
        Assert.Equal("NovaDb 1.0", r.Rows[0][0]);
    }

    [Fact(DisplayName = "USER 当前用户")]
    public void TestUser()
    {
        var r = _engine.Execute("SELECT USER()");
        Assert.Equal("nova", r.Rows[0][0]);
    }

    [Fact(DisplayName = "CONNECTION_ID 连接 ID")]
    public void TestConnectionId()
    {
        var r = _engine.Execute("SELECT CONNECTION_ID()");
        Assert.Equal(0, r.Rows[0][0]);
    }

    [Fact(DisplayName = "ROW_COUNT 上条语句影响行数")]
    public void TestRowCount()
    {
        CreateUsersTable();
        // 上次插入影响 1 行
        var r = _engine.Execute("SELECT ROW_COUNT()");
        Assert.Equal(1, r.Rows[0][0]);
    }

    [Fact(DisplayName = "LAST_INSERT_ID 返回 0")]
    public void TestLastInsertId()
    {
        var r = _engine.Execute("SELECT LAST_INSERT_ID()");
        Assert.Equal(0, r.Rows[0][0]);
    }

    #endregion

    #region 哈希函数

    [Fact(DisplayName = "MD5 哈希")]
    public void TestMd5()
    {
        var r = _engine.Execute("SELECT MD5('hello')");
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", r.Rows[0][0]);
    }

    [Fact(DisplayName = "SHA1 哈希")]
    public void TestSha1()
    {
        var r = _engine.Execute("SELECT SHA1('hello')");
        Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", r.Rows[0][0]);
    }

    [Fact(DisplayName = "SHA2 哈希（256 位）")]
    public void TestSha2()
    {
        var r = _engine.Execute("SELECT SHA2('hello', 256)");
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", r.Rows[0][0]);
    }

    #endregion

    #region 聚合扩展

    [Fact(DisplayName = "STRING_AGG 聚合拼接")]
    public void TestStringAgg()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT STRING_AGG(name, '; ') FROM users");
        var result = Convert.ToString(r.Rows[0][0])!;
        Assert.Contains("Alice", result);
        Assert.Contains("Bob", result);
        Assert.Contains("Charlie", result);
    }

    [Fact(DisplayName = "GROUP_CONCAT 逗号拼接")]
    public void TestGroupConcat()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT GROUP_CONCAT(name) FROM users");
        var result = Convert.ToString(r.Rows[0][0])!;
        Assert.Contains("Alice", result);
        Assert.Contains(",", result);
    }

    [Fact(DisplayName = "STDDEV 标准差")]
    public void TestStddev()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT STDDEV(age) FROM users");
        var stddev = Convert.ToDouble(r.Rows[0][0]);
        Assert.True(stddev > 0);
    }

    [Fact(DisplayName = "VARIANCE 方差")]
    public void TestVariance()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT VARIANCE(age) FROM users");
        var variance = Convert.ToDouble(r.Rows[0][0]);
        Assert.True(variance > 0);
    }

    #endregion

    #region 表行级标量函数

    [Fact(DisplayName = "SELECT 中使用标量函数处理列")]
    public void TestScalarFunctionOnColumn()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT UPPER(name) FROM users WHERE id = 1");
        Assert.Equal("ALICE", r.Rows[0][0]);
    }

    [Fact(DisplayName = "WHERE 中使用标量函数")]
    public void TestScalarFunctionInWhere()
    {
        CreateUsersTable();
        var r = _engine.Execute("SELECT name FROM users WHERE LENGTH(name) > 4");
        Assert.Equal(2, r.Rows.Count); // Alice and Charlie
    }

    [Fact(DisplayName = "嵌套标量函数")]
    public void TestNestedScalarFunctions()
    {
        var r = _engine.Execute("SELECT UPPER(CONCAT('hello', ' ', 'world'))");
        Assert.Equal("HELLO WORLD", r.Rows[0][0]);
    }

    [Fact(DisplayName = "CASE WHEN 无表查询")]
    public void TestCaseWhenNoTable()
    {
        var r = _engine.Execute("SELECT CASE WHEN 1 > 0 THEN 'positive' ELSE 'negative' END");
        Assert.Equal("positive", r.Rows[0][0]);
    }

    [Fact(DisplayName = "RAND 返回随机数")]
    public void TestRand()
    {
        var r = _engine.Execute("SELECT RAND()");
        var val = Convert.ToDouble(r.Rows[0][0]);
        Assert.InRange(val, 0.0, 1.0);
    }

    [Fact(DisplayName = "DATEPART 提取日期部分")]
    public void TestDatePart()
    {
        var r = _engine.Execute("SELECT DATEPART('YEAR', '2025-06-15')");
        Assert.Equal(2025, r.Rows[0][0]);
    }

    [Fact(DisplayName = "WEEKDAY 获取星期几")]
    public void TestWeekday()
    {
        // 2025-06-15 is Sunday = DayOfWeek.Sunday = 0 + 1 = 1
        var r = _engine.Execute("SELECT WEEKDAY('2025-06-15')");
        var expected = (Int32)new DateTime(2025, 6, 15).DayOfWeek + 1;
        Assert.Equal(expected, r.Rows[0][0]);
    }

    [Fact(DisplayName = "QUARTER 获取季度")]
    public void TestQuarter()
    {
        var r1 = _engine.Execute("SELECT QUARTER('2025-01-15')");
        Assert.Equal(1, r1.Rows[0][0]);

        var r2 = _engine.Execute("SELECT QUARTER('2025-06-15')");
        Assert.Equal(2, r2.Rows[0][0]);

        var r3 = _engine.Execute("SELECT QUARTER('2025-09-15')");
        Assert.Equal(3, r3.Rows[0][0]);

        var r4 = _engine.Execute("SELECT QUARTER('2025-12-15')");
        Assert.Equal(4, r4.Rows[0][0]);
    }

    [Fact(DisplayName = "WEEK 获取周数")]
    public void TestWeek()
    {
        var r = _engine.Execute("SELECT WEEK('2025-01-01')");
        Assert.NotNull(r.Rows[0][0]);
        var weekNum = Convert.ToInt32(r.Rows[0][0]);
        Assert.InRange(weekNum, 0, 53);
    }

    [Fact(DisplayName = "ASCII 获取字符ASCII码")]
    public void TestAscii()
    {
        var r = _engine.Execute("SELECT ASCII('A')");
        Assert.Equal(65, r.Rows[0][0]);

        var r2 = _engine.Execute("SELECT ASCII('abc')");
        Assert.Equal(97, r2.Rows[0][0]);
    }

    [Fact(DisplayName = "CHAR 码值转字符")]
    public void TestChar()
    {
        var r = _engine.Execute("SELECT CHAR(65)");
        Assert.Equal("A", r.Rows[0][0]);

        var r2 = _engine.Execute("SELECT CHAR(97)");
        Assert.Equal("a", r2.Rows[0][0]);
    }

    [Fact(DisplayName = "DATEPART支持QUARTER和WEEK")]
    public void TestDatePartQuarterWeek()
    {
        var r1 = _engine.Execute("SELECT DATEPART('QUARTER', '2025-06-15')");
        Assert.Equal(2, r1.Rows[0][0]);

        var r2 = _engine.Execute("SELECT DATEPART('WEEK', '2025-01-01')");
        Assert.NotNull(r2.Rows[0][0]);
    }

    #endregion

    #region GeoPoint 函数

    [Fact(DisplayName = "GEOPOINT 创建地理坐标")]
    public void TestGeoPointFunction()
    {
        var r = _engine.Execute("SELECT GEOPOINT(39.9042, 116.4074)");
        var point = (GeoPoint)r.Rows[0][0]!;
        Assert.Equal(39.9042, point.Latitude);
        Assert.Equal(116.4074, point.Longitude);
    }

    [Fact(DisplayName = "DISTANCE 计算两点距离")]
    public void TestDistanceFunction()
    {
        var r = _engine.Execute("SELECT DISTANCE(GEOPOINT(39.9042, 116.4074), GEOPOINT(31.2304, 121.4737))");
        var distance = Convert.ToDouble(r.Rows[0][0]);
        // 北京到上海约 1068 km
        Assert.InRange(distance, 1_050_000, 1_090_000);
    }

    [Fact(DisplayName = "DISTANCE NULL 参数返回 NULL")]
    public void TestDistanceNullArgs()
    {
        var r = _engine.Execute("SELECT DISTANCE(NULL, GEOPOINT(31.2304, 121.4737))");
        Assert.Null(r.Rows[0][0]);
    }

    [Fact(DisplayName = "WITHIN_RADIUS 在范围内")]
    public void TestWithinRadiusTrue()
    {
        var r = _engine.Execute("SELECT WITHIN_RADIUS(GEOPOINT(39.91, 116.41), GEOPOINT(39.9042, 116.4074), 5000)");
        Assert.Equal(true, r.Rows[0][0]);
    }

    [Fact(DisplayName = "WITHIN_RADIUS 超出范围")]
    public void TestWithinRadiusFalse()
    {
        var r = _engine.Execute("SELECT WITHIN_RADIUS(GEOPOINT(39.9042, 116.4074), GEOPOINT(31.2304, 121.4737), 100000)");
        Assert.Equal(false, r.Rows[0][0]);
    }

    #endregion

    #region Vector 函数

    [Fact(DisplayName = "VECTOR 创建向量")]
    public void TestVectorFunction()
    {
        var r = _engine.Execute("SELECT VECTOR(1.0, 2.0, 3.0)");
        var vec = (Single[])r.Rows[0][0]!;
        Assert.Equal(3, vec.Length);
        Assert.Equal(1.0f, vec[0]);
        Assert.Equal(2.0f, vec[1]);
        Assert.Equal(3.0f, vec[2]);
    }

    [Fact(DisplayName = "COSINE_SIMILARITY 余弦相似度")]
    public void TestCosineSimilarity()
    {
        var r = _engine.Execute("SELECT COSINE_SIMILARITY(VECTOR(1, 0, 0), VECTOR(1, 0, 0))");
        var similarity = Convert.ToDouble(r.Rows[0][0]);
        Assert.Equal(1.0, similarity, 6);
    }

    [Fact(DisplayName = "COSINE_SIMILARITY 正交向量")]
    public void TestCosineSimilarityOrthogonal()
    {
        var r = _engine.Execute("SELECT COSINE_SIMILARITY(VECTOR(1, 0, 0), VECTOR(0, 1, 0))");
        var similarity = Convert.ToDouble(r.Rows[0][0]);
        Assert.Equal(0.0, similarity, 6);
    }

    [Fact(DisplayName = "COSINE_SIMILARITY NULL 参数返回 NULL")]
    public void TestCosineSimilarityNull()
    {
        var r = _engine.Execute("SELECT COSINE_SIMILARITY(NULL, VECTOR(1, 0, 0))");
        Assert.Null(r.Rows[0][0]);
    }

    [Fact(DisplayName = "EUCLIDEAN_DISTANCE 欧氏距离")]
    public void TestEuclideanDistance()
    {
        var r = _engine.Execute("SELECT EUCLIDEAN_DISTANCE(VECTOR(0, 0, 0), VECTOR(3, 4, 0))");
        var distance = Convert.ToDouble(r.Rows[0][0]);
        Assert.Equal(5.0, distance, 6);
    }

    [Fact(DisplayName = "DOT_PRODUCT 点积")]
    public void TestDotProduct()
    {
        var r = _engine.Execute("SELECT DOT_PRODUCT(VECTOR(1, 2, 3), VECTOR(4, 5, 6))");
        var result = Convert.ToDouble(r.Rows[0][0]);
        Assert.Equal(32.0, result, 6); // 1*4 + 2*5 + 3*6 = 32
    }

    [Fact(DisplayName = "DOT_PRODUCT NULL 参数返回 NULL")]
    public void TestDotProductNull()
    {
        var r = _engine.Execute("SELECT DOT_PRODUCT(NULL, VECTOR(1, 2, 3))");
        Assert.Null(r.Rows[0][0]);
    }

    #endregion

    #region 新增函数测试

    [Fact(DisplayName = "TIMESTAMPADD 按天加减")]
    public void TestTimestampAddDay()
    {
        var r = _engine.Execute("SELECT TIMESTAMPADD('DAY', 5, '2025-06-10')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 6, 15), result);
    }

    [Fact(DisplayName = "TIMESTAMPADD 按月加减")]
    public void TestTimestampAddMonth()
    {
        var r = _engine.Execute("SELECT TIMESTAMPADD('MONTH', 3, '2025-01-15')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 4, 15), result);
    }

    [Fact(DisplayName = "TIMESTAMPADD 按小时加减")]
    public void TestTimestampAddHour()
    {
        var r = _engine.Execute("SELECT TIMESTAMPADD('HOUR', 4, '2025-06-15 10:00:00')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 6, 15, 14, 0, 0), result);
    }

    [Fact(DisplayName = "TIMESTAMPADD NULL 参数返回 NULL")]
    public void TestTimestampAddNull()
    {
        var r = _engine.Execute("SELECT TIMESTAMPADD('DAY', NULL, '2025-06-10')");
        Assert.Null(r.Rows[0][0]);
    }

    [Fact(DisplayName = "TIME_BUCKET 按小时分桶")]
    public void TestTimeBucketHour()
    {
        var r = _engine.Execute("SELECT TIME_BUCKET('1 hour', '2025-06-15 14:35:22')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 6, 15, 14, 0, 0), result);
    }

    [Fact(DisplayName = "TIME_BUCKET 按5分钟分桶")]
    public void TestTimeBucket5Minutes()
    {
        var r = _engine.Execute("SELECT TIME_BUCKET('5 minutes', '2025-06-15 14:37:22')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 6, 15, 14, 35, 0), result);
    }

    [Fact(DisplayName = "TIME_BUCKET 按天分桶")]
    public void TestTimeBucketDay()
    {
        var r = _engine.Execute("SELECT TIME_BUCKET('1 day', '2025-06-15 14:35:22')");
        var result = Convert.ToDateTime(r.Rows[0][0]);
        Assert.Equal(new DateTime(2025, 6, 15), result);
    }

    [Fact(DisplayName = "TIME_BUCKET NULL 参数返回 NULL")]
    public void TestTimeBucketNull()
    {
        var r = _engine.Execute("SELECT TIME_BUCKET(NULL, '2025-06-15')");
        Assert.Null(r.Rows[0][0]);
    }

    [Fact(DisplayName = "DISTANCE_KM 计算两点距离（公里）")]
    public void TestDistanceKm()
    {
        var r = _engine.Execute("SELECT DISTANCE_KM(GEOPOINT(39.9042, 116.4074), GEOPOINT(31.2304, 121.4737))");
        var distance = Convert.ToDouble(r.Rows[0][0]);
        // 北京到上海约 1068 km
        Assert.InRange(distance, 1050, 1090);
    }

    [Fact(DisplayName = "DISTANCE_KM NULL 参数返回 NULL")]
    public void TestDistanceKmNull()
    {
        var r = _engine.Execute("SELECT DISTANCE_KM(NULL, GEOPOINT(31.2304, 121.4737))");
        Assert.Null(r.Rows[0][0]);
    }

    #endregion
}

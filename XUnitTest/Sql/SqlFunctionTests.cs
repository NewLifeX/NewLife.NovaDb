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

    #endregion
}

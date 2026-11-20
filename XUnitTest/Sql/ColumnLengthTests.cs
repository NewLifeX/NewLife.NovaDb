using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

#nullable enable

namespace XUnitTest.Sql;

/// <summary>列长度约束单元测试。字符串按字符数、二进制按字节数、向量按维度</summary>
public class ColumnLengthTests : IDisposable
{
    private readonly String _testDir;
    private readonly SqlEngine _engine;

    public ColumnLengthTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ColumnLengthTests_{Guid.NewGuid():N}");
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

    private static String Scalar(SqlResult result) => Convert.ToString(result.Rows[0][0])!;

    [Fact(DisplayName = "STRING(n) 按字符数拒绝超长写入")]
    public void StringLength_RejectsOverLength()
    {
        _engine.Execute("CREATE TABLE t_str (id INT PRIMARY KEY, name STRING(3))");

        // 3 个字符（含中文）都算刚好达标
        _engine.Execute("INSERT INTO t_str VALUES (1, 'abc')");
        _engine.Execute("INSERT INTO t_str VALUES (2, '中文测')");

        var ex = Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO t_str VALUES (3, 'abcd')"));
        Assert.Equal(ErrorCode.ConstraintViolation, ex.Code);

        var ex2 = Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO t_str VALUES (4, '中文测试')"));
        Assert.Equal(ErrorCode.ConstraintViolation, ex2.Code);

        Assert.Equal(2L, Convert.ToInt64(Scalar(_engine.Execute("SELECT COUNT(*) FROM t_str"))));
    }

    [Fact(DisplayName = "UPDATE 超长同样拒绝且不落库")]
    public void StringLength_RejectsOverLengthOnUpdate()
    {
        _engine.Execute("CREATE TABLE t_upd (id INT PRIMARY KEY, name STRING(3))");
        _engine.Execute("INSERT INTO t_upd VALUES (1, 'abc')");

        Assert.Throws<NovaException>(() => _engine.Execute("UPDATE t_upd SET name = 'abcdef' WHERE id = 1"));

        Assert.Equal("abc", Scalar(_engine.Execute("SELECT name FROM t_upd WHERE id = 1")));
    }

    [Fact(DisplayName = "BINARY(n) 按字节数拒绝超长写入")]
    public void BinaryLength_RejectsOverLength()
    {
        _engine.Execute("CREATE TABLE t_bin (id INT PRIMARY KEY, data BINARY(4))");

        _engine.Execute("INSERT INTO t_bin VALUES (@id, @data)", new Dictionary<String, Object?>
        {
            ["@id"] = 1,
            ["@data"] = new Byte[] { 1, 2, 3, 4 },
        });

        var ex = Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO t_bin VALUES (@id, @data)", new Dictionary<String, Object?>
        {
            ["@id"] = 2,
            ["@data"] = new Byte[] { 1, 2, 3, 4, 5 },
        }));
        Assert.Equal(ErrorCode.ConstraintViolation, ex.Code);
    }

    [Fact(DisplayName = "VECTOR(n) 维度不符拒绝写入")]
    public void VectorLength_RejectsDimensionMismatch()
    {
        _engine.Execute("CREATE TABLE t_vec (id INT PRIMARY KEY, v VECTOR(3))");

        _engine.Execute("INSERT INTO t_vec VALUES (1, VECTOR(1, 0, 0))");

        var ex = Assert.Throws<NovaException>(() => _engine.Execute("INSERT INTO t_vec VALUES (2, VECTOR(1, 0))"));
        Assert.Equal(ErrorCode.ConstraintViolation, ex.Code);
    }

    [Fact(DisplayName = "SHOW COLUMNS 输出规范名与长度")]
    public void ShowColumns_ReturnsCanonicalTypeWithLength()
    {
        _engine.Execute("CREATE TABLE t_show (id INT PRIMARY KEY, name VARCHAR(64), data BLOB, flag BOOLEAN)");

        var rows = _engine.Execute("SHOW COLUMNS FROM t_show").Rows;
        String TypeOf(String column) => Convert.ToString(rows.First(r => Convert.ToString(r[0]) == column)[1])!;

        Assert.Equal("int", TypeOf("id"));
        Assert.Equal("string(64)", TypeOf("name"));
        Assert.Equal("binary", TypeOf("data"));
        Assert.Equal("bool", TypeOf("flag"));
    }

    [Fact(DisplayName = "information_schema 与 SHOW COLUMNS 类型名一致")]
    public void InformationSchema_MatchesShowColumns()
    {
        _engine.Execute("CREATE TABLE t_info (id BIGINT PRIMARY KEY, name STRING(20))");

        var type = Scalar(_engine.Execute("SELECT column_type FROM information_schema.columns WHERE table_name = 't_info' AND column_name = 'name'"));
        var dataType = Scalar(_engine.Execute("SELECT data_type FROM information_schema.columns WHERE table_name = 't_info' AND column_name = 'name'"));
        var maxLength = _engine.Execute("SELECT character_maximum_length FROM information_schema.columns WHERE table_name = 't_info' AND column_name = 'name'").Rows[0][0];

        Assert.Equal("string(20)", type);
        Assert.Equal("string", dataType);
        Assert.Equal(20L, Convert.ToInt64(maxLength));

        Assert.Equal("bigint", Scalar(_engine.Execute("SELECT column_type FROM information_schema.columns WHERE table_name = 't_info' AND column_name = 'id'")));
    }

    [Fact(DisplayName = "ALTER TABLE 修改长度后元数据生效")]
    public void AlterTable_ModifyLength()
    {
        _engine.Execute("CREATE TABLE t_alt (id INT PRIMARY KEY, name STRING(5))");
        _engine.Execute("INSERT INTO t_alt VALUES (1, 'abc')");

        _engine.Execute("ALTER TABLE t_alt MODIFY COLUMN name STRING(10)");

        var type = Convert.ToString(_engine.Execute("SHOW COLUMNS FROM t_alt").Rows.First(r => Convert.ToString(r[0]) == "name")[1]);
        Assert.Equal("string(10)", type);

        // 旧数据仍可读，新长度放开到 10 个字符
        Assert.Equal("abc", Scalar(_engine.Execute("SELECT name FROM t_alt WHERE id = 1")));
        _engine.Execute("INSERT INTO t_alt VALUES (2, 'abcdefghij')");
    }

    [Fact(DisplayName = "未声明长度的列不受限制")]
    public void StringWithoutLength_IsUnlimited()
    {
        _engine.Execute("CREATE TABLE t_unlimited (id INT PRIMARY KEY, name STRING)");

        var longText = new String('x', 5000);
        _engine.Execute("INSERT INTO t_unlimited VALUES (@id, @name)", new Dictionary<String, Object?>
        {
            ["@id"] = 1,
            ["@name"] = longText,
        });

        Assert.Equal(longText, Scalar(_engine.Execute("SELECT name FROM t_unlimited WHERE id = 1")));
    }

    [Fact(DisplayName = "长度随 schema 持久化并在重启后恢复")]
    public void Length_SurvivesReopen()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ColumnLengthReopen_{Guid.NewGuid():N}");
        try
        {
            using (var engine = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None }))
            {
                engine.Execute("CREATE TABLE t_persist (id INT PRIMARY KEY, name STRING(7))");
            }

            using var reopened = new SqlEngine(dir, new DbOptions { Path = dir, WalMode = WalMode.None });
            var type = Convert.ToString(reopened.Execute("SHOW COLUMNS FROM t_persist").Rows.First(r => Convert.ToString(r[0]) == "name")[1]);
            Assert.Equal("string(7)", type);

            Assert.Throws<NovaException>(() => reopened.Execute("INSERT INTO t_persist VALUES (1, 'abcdefgh')"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 忽略清理错误
                }
            }
        }
    }
}

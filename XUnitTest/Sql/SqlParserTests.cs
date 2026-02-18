using System;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;
using Xunit;

namespace XUnitTest.Sql;

/// <summary>SQL 解析器单元测试</summary>
public class SqlParserTests
{
    [Fact(DisplayName = "测试 CREATE TABLE 解析")]
    public void TestParseCreateTable()
    {
        var parser = new SqlParser("CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)");
        var stmt = parser.Parse();

        Assert.IsType<CreateTableStatement>(stmt);
        var create = (CreateTableStatement)stmt;
        Assert.Equal("users", create.TableName);
        Assert.Equal(3, create.Columns.Count);
        Assert.Equal("id", create.Columns[0].Name);
        Assert.Equal("INT", create.Columns[0].DataTypeName);
        Assert.True(create.Columns[0].IsPrimaryKey);
        Assert.Equal("name", create.Columns[1].Name);
        Assert.True(create.Columns[1].NotNull);
        Assert.Equal("age", create.Columns[2].Name);
    }

    [Fact(DisplayName = "测试 CREATE TABLE IF NOT EXISTS")]
    public void TestParseCreateTableIfNotExists()
    {
        var parser = new SqlParser("CREATE TABLE IF NOT EXISTS logs (id INT PRIMARY KEY, message TEXT)");
        var stmt = parser.Parse();

        Assert.IsType<CreateTableStatement>(stmt);
        var create = (CreateTableStatement)stmt;
        Assert.True(create.IfNotExists);
        Assert.Equal("logs", create.TableName);
    }

    [Fact(DisplayName = "测试 DROP TABLE 解析")]
    public void TestParseDropTable()
    {
        var parser = new SqlParser("DROP TABLE users");
        var stmt = parser.Parse();

        Assert.IsType<DropTableStatement>(stmt);
        var drop = (DropTableStatement)stmt;
        Assert.Equal("users", drop.TableName);
        Assert.False(drop.IfExists);
    }

    [Fact(DisplayName = "测试 DROP TABLE IF EXISTS")]
    public void TestParseDropTableIfExists()
    {
        var parser = new SqlParser("DROP TABLE IF EXISTS users");
        var stmt = parser.Parse();

        Assert.IsType<DropTableStatement>(stmt);
        var drop = (DropTableStatement)stmt;
        Assert.True(drop.IfExists);
    }

    [Fact(DisplayName = "测试 CREATE INDEX 解析")]
    public void TestParseCreateIndex()
    {
        var parser = new SqlParser("CREATE INDEX idx_name ON users (name)");
        var stmt = parser.Parse();

        Assert.IsType<CreateIndexStatement>(stmt);
        var create = (CreateIndexStatement)stmt;
        Assert.Equal("idx_name", create.IndexName);
        Assert.Equal("users", create.TableName);
        Assert.Single(create.Columns);
        Assert.Equal("name", create.Columns[0]);
        Assert.False(create.IsUnique);
    }

    [Fact(DisplayName = "测试 CREATE UNIQUE INDEX")]
    public void TestParseCreateUniqueIndex()
    {
        var parser = new SqlParser("CREATE UNIQUE INDEX idx_email ON users (email)");
        var stmt = parser.Parse();

        Assert.IsType<CreateIndexStatement>(stmt);
        var create = (CreateIndexStatement)stmt;
        Assert.True(create.IsUnique);
    }

    [Fact(DisplayName = "测试 INSERT 解析")]
    public void TestParseInsert()
    {
        var parser = new SqlParser("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25)");
        var stmt = parser.Parse();

        Assert.IsType<InsertStatement>(stmt);
        var insert = (InsertStatement)stmt;
        Assert.Equal("users", insert.TableName);
        Assert.NotNull(insert.Columns);
        Assert.Equal(3, insert.Columns!.Count);
        Assert.Single(insert.ValuesList);
        Assert.Equal(3, insert.ValuesList[0].Count);
    }

    [Fact(DisplayName = "测试 INSERT 无列名")]
    public void TestParseInsertWithoutColumns()
    {
        var parser = new SqlParser("INSERT INTO users VALUES (1, 'Alice', 25)");
        var stmt = parser.Parse();

        Assert.IsType<InsertStatement>(stmt);
        var insert = (InsertStatement)stmt;
        Assert.Null(insert.Columns);
        Assert.Single(insert.ValuesList);
    }

    [Fact(DisplayName = "测试 UPDATE 解析")]
    public void TestParseUpdate()
    {
        var parser = new SqlParser("UPDATE users SET name = 'Bob', age = 30 WHERE id = 1");
        var stmt = parser.Parse();

        Assert.IsType<UpdateStatement>(stmt);
        var update = (UpdateStatement)stmt;
        Assert.Equal("users", update.TableName);
        Assert.Equal(2, update.SetClauses.Count);
        Assert.NotNull(update.Where);
    }

    [Fact(DisplayName = "测试 DELETE 解析")]
    public void TestParseDelete()
    {
        var parser = new SqlParser("DELETE FROM users WHERE id = 1");
        var stmt = parser.Parse();

        Assert.IsType<DeleteStatement>(stmt);
        var delete = (DeleteStatement)stmt;
        Assert.Equal("users", delete.TableName);
        Assert.NotNull(delete.Where);
    }

    [Fact(DisplayName = "测试 SELECT * 解析")]
    public void TestParseSelectAll()
    {
        var parser = new SqlParser("SELECT * FROM users");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.True(select.IsSelectAll);
        Assert.Equal("users", select.TableName);
    }

    [Fact(DisplayName = "测试 SELECT 带 WHERE")]
    public void TestParseSelectWithWhere()
    {
        var parser = new SqlParser("SELECT id, name FROM users WHERE age > 18 AND name = 'Alice'");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal(2, select.Columns.Count);
        Assert.NotNull(select.Where);
    }

    [Fact(DisplayName = "测试 SELECT 带 ORDER BY")]
    public void TestParseSelectWithOrderBy()
    {
        var parser = new SqlParser("SELECT * FROM users ORDER BY name ASC, age DESC");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.NotNull(select.OrderBy);
        Assert.Equal(2, select.OrderBy!.Count);
        Assert.False(select.OrderBy[0].Descending);
        Assert.True(select.OrderBy[1].Descending);
    }

    [Fact(DisplayName = "测试 SELECT 带 GROUP BY")]
    public void TestParseSelectWithGroupBy()
    {
        var parser = new SqlParser("SELECT name, COUNT(*) AS cnt FROM users GROUP BY name");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.NotNull(select.GroupBy);
        Assert.Single(select.GroupBy!);
        Assert.Equal("name", select.GroupBy[0]);
    }

    [Fact(DisplayName = "测试 SELECT 带 HAVING")]
    public void TestParseSelectWithHaving()
    {
        var parser = new SqlParser("SELECT name, COUNT(*) AS cnt FROM users GROUP BY name HAVING COUNT(*) > 1");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.NotNull(select.Having);
    }

    [Fact(DisplayName = "测试 SELECT 带 LIMIT")]
    public void TestParseSelectWithLimit()
    {
        var parser = new SqlParser("SELECT * FROM users LIMIT 10 OFFSET 5");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal(10, select.Limit);
        Assert.Equal(5, select.OffsetValue);
    }

    [Fact(DisplayName = "测试聚合函数")]
    public void TestParseAggregateFunctions()
    {
        var parser = new SqlParser("SELECT COUNT(*), SUM(age), AVG(age), MIN(age), MAX(age) FROM users");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal(5, select.Columns.Count);

        for (var i = 0; i < 5; i++)
        {
            Assert.IsType<FunctionExpression>(select.Columns[i].Expression);
            var func = (FunctionExpression)select.Columns[i].Expression;
            Assert.True(func.IsAggregate);
        }
    }

    [Fact(DisplayName = "测试参数占位符")]
    public void TestParseParameter()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE id = @id");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.NotNull(select.Where);
        Assert.IsType<BinaryExpression>(select.Where);

        var binary = (BinaryExpression)select.Where!;
        Assert.IsType<ParameterExpression>(binary.Right);
        Assert.Equal("@id", ((ParameterExpression)binary.Right).ParameterName);
    }

    [Fact(DisplayName = "测试 LIKE 运算符")]
    public void TestParseLike()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE name LIKE 'A%'");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<BinaryExpression>(select.Where);
        Assert.Equal(BinaryOperator.Like, ((BinaryExpression)select.Where!).Operator);
    }

    [Fact(DisplayName = "测试 IS NULL")]
    public void TestParseIsNull()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE age IS NULL");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<IsNullExpression>(select.Where);
        Assert.False(((IsNullExpression)select.Where!).IsNot);
    }

    [Fact(DisplayName = "测试 IS NOT NULL")]
    public void TestParseIsNotNull()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE age IS NOT NULL");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<IsNullExpression>(select.Where);
        Assert.True(((IsNullExpression)select.Where!).IsNot);
    }

    [Fact(DisplayName = "测试语法错误")]
    public void TestSyntaxError()
    {
        Assert.Throws<NovaException>(() =>
        {
            var parser = new SqlParser("INVALID SQL");
            parser.Parse();
        });
    }

    [Fact(DisplayName = "测试 SELECT 无 FROM")]
    public void TestParseSelectNoFrom()
    {
        var parser = new SqlParser("SELECT 1");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Null(select.TableName);
        Assert.Single(select.Columns);
    }

    [Fact(DisplayName = "测试 DROP INDEX")]
    public void TestParseDropIndex()
    {
        var parser = new SqlParser("DROP INDEX idx_name ON users");
        var stmt = parser.Parse();

        Assert.IsType<DropIndexStatement>(stmt);
        var drop = (DropIndexStatement)stmt;
        Assert.Equal("idx_name", drop.IndexName);
        Assert.Equal("users", drop.TableName);
    }

    [Fact(DisplayName = "测试带分号的 SQL")]
    public void TestParseSqlWithSemicolon()
    {
        var parser = new SqlParser("SELECT * FROM users;");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
    }

    [Fact(DisplayName = "测试字符串转义")]
    public void TestParseStringEscape()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE name = 'O''Brien'");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        var binary = (BinaryExpression)select.Where!;
        var literal = (LiteralExpression)binary.Right;
        Assert.Equal("O'Brien", literal.Value);
    }

    [Fact(DisplayName = "测试多行 INSERT")]
    public void TestParseMultiRowInsert()
    {
        var parser = new SqlParser("INSERT INTO users VALUES (1, 'Alice', 25), (2, 'Bob', 30)");
        var stmt = parser.Parse();

        Assert.IsType<InsertStatement>(stmt);
        var insert = (InsertStatement)stmt;
        Assert.Equal(2, insert.ValuesList.Count);
    }

    [Fact(DisplayName = "测试表级 PRIMARY KEY 约束")]
    public void TestParseTableLevelPrimaryKey()
    {
        var parser = new SqlParser("CREATE TABLE users (id INT, name VARCHAR, PRIMARY KEY (id))");
        var stmt = parser.Parse();

        Assert.IsType<CreateTableStatement>(stmt);
        var create = (CreateTableStatement)stmt;
        Assert.True(create.Columns[0].IsPrimaryKey);
    }

    [Fact(DisplayName = "测试 INNER JOIN 解析")]
    public void TestParseInnerJoin()
    {
        var parser = new SqlParser("SELECT e.name, d.name FROM employees e INNER JOIN departments d ON e.dept_id = d.id");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal("employees", select.TableName);
        Assert.Equal("e", select.TableAlias);
        Assert.True(select.HasJoin);
        Assert.Single(select.Joins!);

        var join = select.Joins![0];
        Assert.Equal(JoinType.Inner, join.Type);
        Assert.Equal("departments", join.TableName);
        Assert.Equal("d", join.Alias);
        Assert.NotNull(join.Condition);
    }

    [Fact(DisplayName = "测试 LEFT JOIN 解析")]
    public void TestParseLeftJoin()
    {
        var parser = new SqlParser("SELECT * FROM a LEFT JOIN b ON a.id = b.a_id");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.True(select.HasJoin);
        Assert.Equal(JoinType.Left, select.Joins![0].Type);
    }

    [Fact(DisplayName = "测试多表 JOIN 解析")]
    public void TestParseMultipleJoins()
    {
        var parser = new SqlParser("SELECT * FROM a JOIN b ON a.id = b.a_id JOIN c ON b.id = c.b_id");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal(2, select.Joins!.Count);
        Assert.Equal("b", select.Joins[0].TableName);
        Assert.Equal("c", select.Joins[1].TableName);
    }
}

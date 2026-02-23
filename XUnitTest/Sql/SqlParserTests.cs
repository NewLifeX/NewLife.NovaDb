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

    #region UPSERT 解析

    [Fact(DisplayName = "测试 UPSERT 解析")]
    public void TestParseUpsert()
    {
        var parser = new SqlParser("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25) ON DUPLICATE KEY UPDATE name = 'Alice2', age = 26");
        var stmt = parser.Parse();

        Assert.IsType<UpsertStatement>(stmt);
        var upsert = (UpsertStatement)stmt;
        Assert.Equal("users", upsert.TableName);
        Assert.Equal(SqlStatementType.Upsert, upsert.StatementType);
        Assert.NotNull(upsert.Columns);
        Assert.Equal(3, upsert.Columns!.Count);
        Assert.Single(upsert.ValuesList);
        Assert.Equal(3, upsert.ValuesList[0].Count);
        Assert.Equal(2, upsert.UpdateClauses.Count);
        Assert.Equal("name", upsert.UpdateClauses[0].Column);
        Assert.Equal("age", upsert.UpdateClauses[1].Column);
    }

    [Fact(DisplayName = "测试 UPSERT 无列名解析")]
    public void TestParseUpsertWithoutColumns()
    {
        var parser = new SqlParser("INSERT INTO users VALUES (1, 'Alice', 25) ON DUPLICATE KEY UPDATE name = 'Alice2'");
        var stmt = parser.Parse();

        Assert.IsType<UpsertStatement>(stmt);
        var upsert = (UpsertStatement)stmt;
        Assert.Null(upsert.Columns);
        Assert.Single(upsert.UpdateClauses);
    }

    [Fact(DisplayName = "测试 UPSERT 多行解析")]
    public void TestParseUpsertMultiRow()
    {
        var parser = new SqlParser("INSERT INTO users VALUES (1, 'Alice', 25), (2, 'Bob', 30) ON DUPLICATE KEY UPDATE age = 99");
        var stmt = parser.Parse();

        Assert.IsType<UpsertStatement>(stmt);
        var upsert = (UpsertStatement)stmt;
        Assert.Equal(2, upsert.ValuesList.Count);
        Assert.Single(upsert.UpdateClauses);
    }

    [Fact(DisplayName = "测试普通 INSERT 不触发 UPSERT")]
    public void TestParseInsertNotUpsert()
    {
        var parser = new SqlParser("INSERT INTO users (id, name) VALUES (1, 'Alice')");
        var stmt = parser.Parse();

        Assert.IsType<InsertStatement>(stmt);
        Assert.IsNotType<UpsertStatement>(stmt);
    }

    #endregion

    #region MERGE INTO 解析

    [Fact(DisplayName = "测试 MERGE INTO 解析")]
    public void TestParseMerge()
    {
        var parser = new SqlParser("MERGE INTO users (id, name, age) VALUES (1, 'Alice', 25)");
        var stmt = parser.Parse();

        Assert.IsType<MergeStatement>(stmt);
        var merge = (MergeStatement)stmt;
        Assert.Equal("users", merge.TableName);
        Assert.Equal(SqlStatementType.Merge, merge.StatementType);
        Assert.NotNull(merge.Columns);
        Assert.Equal(3, merge.Columns!.Count);
        Assert.Single(merge.ValuesList);
        Assert.Equal(3, merge.ValuesList[0].Count);
    }

    [Fact(DisplayName = "测试 MERGE INTO 无列名解析")]
    public void TestParseMergeWithoutColumns()
    {
        var parser = new SqlParser("MERGE INTO users VALUES (1, 'Alice', 25)");
        var stmt = parser.Parse();

        Assert.IsType<MergeStatement>(stmt);
        var merge = (MergeStatement)stmt;
        Assert.Null(merge.Columns);
        Assert.Single(merge.ValuesList);
    }

    [Fact(DisplayName = "测试 MERGE INTO 多行解析")]
    public void TestParseMergeMultiRow()
    {
        var parser = new SqlParser("MERGE INTO users VALUES (1, 'Alice', 25), (2, 'Bob', 30)");
        var stmt = parser.Parse();

        Assert.IsType<MergeStatement>(stmt);
        var merge = (MergeStatement)stmt;
        Assert.Equal(2, merge.ValuesList.Count);
    }

    #endregion

    #region TRUNCATE 解析

    [Fact(DisplayName = "测试 TRUNCATE TABLE 解析")]
    public void TestParseTruncateTable()
    {
        var parser = new SqlParser("TRUNCATE TABLE users");
        var stmt = parser.Parse();

        Assert.IsType<TruncateTableStatement>(stmt);
        var truncate = (TruncateTableStatement)stmt;
        Assert.Equal("users", truncate.TableName);
        Assert.Equal(SqlStatementType.TruncateTable, truncate.StatementType);
    }

    #endregion

    #region CASE WHEN / CAST 解析

    [Fact(DisplayName = "测试 CASE WHEN 表达式解析")]
    public void TestParseCaseWhen()
    {
        var parser = new SqlParser("SELECT CASE WHEN age > 30 THEN 'senior' ELSE 'junior' END AS level FROM users");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Single(select.Columns);
        Assert.IsType<CaseExpression>(select.Columns[0].Expression);

        var caseExpr = (CaseExpression)select.Columns[0].Expression;
        Assert.Single(caseExpr.WhenClauses);
        Assert.NotNull(caseExpr.ElseExpression);
        Assert.Equal("level", select.Columns[0].Alias);
    }

    [Fact(DisplayName = "测试 CAST 表达式解析")]
    public void TestParseCast()
    {
        var parser = new SqlParser("SELECT CAST(age AS VARCHAR) FROM users");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<CastExpression>(select.Columns[0].Expression);

        var castExpr = (CastExpression)select.Columns[0].Expression;
        Assert.Equal("VARCHAR", castExpr.TargetTypeName);
    }

    #endregion

    #region CREATE/DROP DATABASE 解析

    [Fact(DisplayName = "测试 CREATE DATABASE 解析")]
    public void TestParseCreateDatabase()
    {
        var parser = new SqlParser("CREATE DATABASE testdb");
        var stmt = parser.Parse();

        Assert.IsType<CreateDatabaseStatement>(stmt);
        var create = (CreateDatabaseStatement)stmt;
        Assert.Equal("testdb", create.DatabaseName);
        Assert.False(create.IfNotExists);
    }

    [Fact(DisplayName = "测试 CREATE DATABASE IF NOT EXISTS")]
    public void TestParseCreateDatabaseIfNotExists()
    {
        var parser = new SqlParser("CREATE DATABASE IF NOT EXISTS testdb");
        var stmt = parser.Parse();

        Assert.IsType<CreateDatabaseStatement>(stmt);
        var create = (CreateDatabaseStatement)stmt;
        Assert.True(create.IfNotExists);
    }

    [Fact(DisplayName = "测试 DROP DATABASE 解析")]
    public void TestParseDropDatabase()
    {
        var parser = new SqlParser("DROP DATABASE testdb");
        var stmt = parser.Parse();

        Assert.IsType<DropDatabaseStatement>(stmt);
        var drop = (DropDatabaseStatement)stmt;
        Assert.Equal("testdb", drop.DatabaseName);
        Assert.False(drop.IfExists);
    }

    [Fact(DisplayName = "测试 DROP DATABASE IF EXISTS")]
    public void TestParseDropDatabaseIfExists()
    {
        var parser = new SqlParser("DROP DATABASE IF EXISTS testdb");
        var stmt = parser.Parse();

        Assert.IsType<DropDatabaseStatement>(stmt);
        var drop = (DropDatabaseStatement)stmt;
        Assert.True(drop.IfExists);
    }

    #endregion

    #region ALTER TABLE 解析

    [Fact(DisplayName = "测试 ALTER TABLE ADD COLUMN")]
    public void TestParseAlterTableAddColumn()
    {
        var parser = new SqlParser("ALTER TABLE users ADD COLUMN email VARCHAR NOT NULL");
        var stmt = parser.Parse();

        Assert.IsType<AlterTableStatement>(stmt);
        var alter = (AlterTableStatement)stmt;
        Assert.Equal("users", alter.TableName);
        Assert.Equal(AlterTableAction.AddColumn, alter.Action);
        Assert.NotNull(alter.ColumnDef);
        Assert.Equal("email", alter.ColumnDef!.Name);
        Assert.Equal("VARCHAR", alter.ColumnDef.DataTypeName);
        Assert.True(alter.ColumnDef.NotNull);
    }

    [Fact(DisplayName = "测试 ALTER TABLE MODIFY COLUMN")]
    public void TestParseAlterTableModifyColumn()
    {
        var parser = new SqlParser("ALTER TABLE users MODIFY COLUMN name TEXT");
        var stmt = parser.Parse();

        Assert.IsType<AlterTableStatement>(stmt);
        var alter = (AlterTableStatement)stmt;
        Assert.Equal(AlterTableAction.ModifyColumn, alter.Action);
        Assert.Equal("name", alter.ColumnDef!.Name);
        Assert.Equal("TEXT", alter.ColumnDef.DataTypeName);
    }

    [Fact(DisplayName = "测试 ALTER TABLE DROP COLUMN")]
    public void TestParseAlterTableDropColumn()
    {
        var parser = new SqlParser("ALTER TABLE users DROP COLUMN age");
        var stmt = parser.Parse();

        Assert.IsType<AlterTableStatement>(stmt);
        var alter = (AlterTableStatement)stmt;
        Assert.Equal(AlterTableAction.DropColumn, alter.Action);
        Assert.Equal("age", alter.ColumnName);
    }

    [Fact(DisplayName = "测试 ALTER TABLE COMMENT")]
    public void TestParseAlterTableComment()
    {
        var parser = new SqlParser("ALTER TABLE users COMMENT = '用户表'");
        var stmt = parser.Parse();

        Assert.IsType<AlterTableStatement>(stmt);
        var alter = (AlterTableStatement)stmt;
        Assert.Equal(AlterTableAction.AddTableComment, alter.Action);
        Assert.Equal("用户表", alter.Comment);
    }

    #endregion

    #region 表达式与运算符解析

    [Fact(DisplayName = "测试一元 NOT 表达式")]
    public void TestParseUnaryNot()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE NOT (age > 30)");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<UnaryExpression>(select.Where);
        var unary = (UnaryExpression)select.Where!;
        Assert.Equal("NOT", unary.Operator);
    }

    [Fact(DisplayName = "测试负数字面量")]
    public void TestParseNegativeNumber()
    {
        var parser = new SqlParser("SELECT -1");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<UnaryExpression>(select.Columns[0].Expression);
    }

    [Fact(DisplayName = "测试 Boolean 字面量")]
    public void TestParseBooleanLiteral()
    {
        var parser = new SqlParser("SELECT TRUE, FALSE");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal(2, select.Columns.Count);
        var trueExpr = (LiteralExpression)select.Columns[0].Expression;
        var falseExpr = (LiteralExpression)select.Columns[1].Expression;
        Assert.Equal(true, trueExpr.Value);
        Assert.Equal(false, falseExpr.Value);
    }

    [Fact(DisplayName = "测试 NULL 字面量")]
    public void TestParseNullLiteral()
    {
        var parser = new SqlParser("SELECT NULL");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        var lit = (LiteralExpression)select.Columns[0].Expression;
        Assert.Null(lit.Value);
    }

    [Fact(DisplayName = "测试算术表达式")]
    public void TestParseArithmetic()
    {
        var parser = new SqlParser("SELECT 1 + 2 * 3");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<BinaryExpression>(select.Columns[0].Expression);

        // 验证优先级: 1 + (2 * 3)
        var add = (BinaryExpression)select.Columns[0].Expression;
        Assert.Equal(BinaryOperator.Add, add.Operator);
        Assert.IsType<BinaryExpression>(add.Right);
        var mul = (BinaryExpression)add.Right;
        Assert.Equal(BinaryOperator.Multiply, mul.Operator);
    }

    [Fact(DisplayName = "测试括号表达式")]
    public void TestParseParentheses()
    {
        var parser = new SqlParser("SELECT (1 + 2) * 3");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<BinaryExpression>(select.Columns[0].Expression);

        // 验证 (1 + 2) * 3
        var mul = (BinaryExpression)select.Columns[0].Expression;
        Assert.Equal(BinaryOperator.Multiply, mul.Operator);
        Assert.IsType<BinaryExpression>(mul.Left);
        var add = (BinaryExpression)mul.Left;
        Assert.Equal(BinaryOperator.Add, add.Operator);
    }

    [Fact(DisplayName = "测试 OR 优先级低于 AND")]
    public void TestParseAndOrPrecedence()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE a = 1 OR b = 2 AND c = 3");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        // OR 优先级低于 AND: a = 1 OR (b = 2 AND c = 3)
        Assert.IsType<BinaryExpression>(select.Where);
        var or = (BinaryExpression)select.Where!;
        Assert.Equal(BinaryOperator.Or, or.Operator);
        Assert.IsType<BinaryExpression>(or.Right);
        var and = (BinaryExpression)or.Right;
        Assert.Equal(BinaryOperator.And, and.Operator);
    }

    [Fact(DisplayName = "测试比较运算符解析")]
    public void TestParseComparisonOperators()
    {
        // 测试 !=
        var parser = new SqlParser("SELECT * FROM users WHERE id != 1");
        var stmt = (SelectStatement)parser.Parse();
        Assert.Equal(BinaryOperator.NotEqual, ((BinaryExpression)stmt.Where!).Operator);

        // 测试 <>
        parser = new SqlParser("SELECT * FROM users WHERE id <> 1");
        stmt = (SelectStatement)parser.Parse();
        Assert.Equal(BinaryOperator.NotEqual, ((BinaryExpression)stmt.Where!).Operator);

        // 测试 <=
        parser = new SqlParser("SELECT * FROM users WHERE id <= 1");
        stmt = (SelectStatement)parser.Parse();
        Assert.Equal(BinaryOperator.LessOrEqual, ((BinaryExpression)stmt.Where!).Operator);

        // 测试 >=
        parser = new SqlParser("SELECT * FROM users WHERE id >= 1");
        stmt = (SelectStatement)parser.Parse();
        Assert.Equal(BinaryOperator.GreaterOrEqual, ((BinaryExpression)stmt.Where!).Operator);
    }

    [Fact(DisplayName = "测试标量函数调用解析")]
    public void TestParseScalarFunction()
    {
        var parser = new SqlParser("SELECT UPPER('hello')");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.IsType<FunctionExpression>(select.Columns[0].Expression);
        var func = (FunctionExpression)select.Columns[0].Expression;
        Assert.Equal("UPPER", func.FunctionName);
        Assert.False(func.IsAggregate);
        Assert.Single(func.Arguments);
    }

    [Fact(DisplayName = "测试表名带点号解析（系统表）")]
    public void TestParseDottedTableName()
    {
        var parser = new SqlParser("SELECT * FROM _sys.tables");
        var stmt = parser.Parse();

        Assert.IsType<SelectStatement>(stmt);
        var select = (SelectStatement)stmt;
        Assert.Equal("_sys.tables", select.TableName);
    }

    [Fact(DisplayName = "测试 CREATE TABLE 带 ENGINE 和 COMMENT")]
    public void TestParseCreateTableWithEngineAndComment()
    {
        var parser = new SqlParser("CREATE TABLE users (id INT PRIMARY KEY) ENGINE = Nova COMMENT '用户表'");
        var stmt = parser.Parse();

        Assert.IsType<CreateTableStatement>(stmt);
        var create = (CreateTableStatement)stmt;
        Assert.Equal("Nova", create.EngineName);
        Assert.Equal("用户表", create.Comment);
    }

    [Fact(DisplayName = "测试列带 COMMENT")]
    public void TestParseColumnComment()
    {
        var parser = new SqlParser("CREATE TABLE users (id INT PRIMARY KEY COMMENT '主键', name VARCHAR COMMENT '用户名')");
        var stmt = parser.Parse();

        Assert.IsType<CreateTableStatement>(stmt);
        var create = (CreateTableStatement)stmt;
        Assert.Equal("主键", create.Columns[0].Comment);
        Assert.Equal("用户名", create.Columns[1].Comment);
    }

    #endregion
}

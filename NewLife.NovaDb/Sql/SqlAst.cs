using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 语句类型</summary>
public enum SqlStatementType
{
    /// <summary>CREATE TABLE</summary>
    CreateTable,
    /// <summary>DROP TABLE</summary>
    DropTable,
    /// <summary>CREATE INDEX</summary>
    CreateIndex,
    /// <summary>DROP INDEX</summary>
    DropIndex,
    /// <summary>CREATE DATABASE</summary>
    CreateDatabase,
    /// <summary>DROP DATABASE</summary>
    DropDatabase,
    /// <summary>ALTER TABLE</summary>
    AlterTable,
    /// <summary>INSERT</summary>
    Insert,
    /// <summary>UPDATE</summary>
    Update,
    /// <summary>DELETE</summary>
    Delete,
    /// <summary>SELECT</summary>
    Select,
    /// <summary>TRUNCATE TABLE</summary>
    TruncateTable,
    /// <summary>UPSERT（INSERT ... ON DUPLICATE KEY UPDATE）</summary>
    Upsert,
    /// <summary>EXPLAIN 查询计划</summary>
    Explain
}

/// <summary>SQL 语句基类</summary>
public abstract class SqlStatement
{
    /// <summary>语句类型</summary>
    public abstract SqlStatementType StatementType { get; }
}

/// <summary>CREATE TABLE 语句</summary>
public class CreateTableStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.CreateTable;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>列定义列表</summary>
    public List<SqlColumnDef> Columns { get; set; } = [];

    /// <summary>是否包含 IF NOT EXISTS</summary>
    public Boolean IfNotExists { get; set; }

    /// <summary>存储引擎名称（默认 Nova）</summary>
    public String? EngineName { get; set; }

    /// <summary>表注释</summary>
    public String? Comment { get; set; }
}

/// <summary>SQL 列定义</summary>
public class SqlColumnDef
{
    /// <summary>列名</summary>
    public String Name { get; set; } = String.Empty;

    /// <summary>数据类型名称</summary>
    public String DataTypeName { get; set; } = String.Empty;

    /// <summary>是否为主键</summary>
    public Boolean IsPrimaryKey { get; set; }

    /// <summary>是否不允许为空</summary>
    public Boolean NotNull { get; set; }

    /// <summary>列注释</summary>
    public String? Comment { get; set; }
}

/// <summary>DROP TABLE 语句</summary>
public class DropTableStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.DropTable;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>是否包含 IF EXISTS</summary>
    public Boolean IfExists { get; set; }
}

/// <summary>TRUNCATE TABLE 语句</summary>
public class TruncateTableStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.TruncateTable;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;
}

/// <summary>EXPLAIN 语句（查询计划解释）</summary>
public class ExplainStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Explain;

    /// <summary>被解释的内部语句</summary>
    public SqlStatement InnerStatement { get; set; } = null!;
}

/// <summary>CREATE INDEX 语句</summary>
public class CreateIndexStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.CreateIndex;

    /// <summary>索引名</summary>
    public String IndexName { get; set; } = String.Empty;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>索引列</summary>
    public List<String> Columns { get; set; } = [];

    /// <summary>是否唯一索引</summary>
    public Boolean IsUnique { get; set; }
}

/// <summary>DROP INDEX 语句</summary>
public class DropIndexStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.DropIndex;

    /// <summary>索引名</summary>
    public String IndexName { get; set; } = String.Empty;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;
}

/// <summary>CREATE DATABASE 语句</summary>
public class CreateDatabaseStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.CreateDatabase;

    /// <summary>数据库名</summary>
    public String DatabaseName { get; set; } = String.Empty;

    /// <summary>是否包含 IF NOT EXISTS</summary>
    public Boolean IfNotExists { get; set; }
}

/// <summary>DROP DATABASE 语句</summary>
public class DropDatabaseStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.DropDatabase;

    /// <summary>数据库名</summary>
    public String DatabaseName { get; set; } = String.Empty;

    /// <summary>是否包含 IF EXISTS</summary>
    public Boolean IfExists { get; set; }
}

/// <summary>ALTER TABLE 操作类型</summary>
public enum AlterTableAction
{
    /// <summary>添加列</summary>
    AddColumn,
    /// <summary>修改列</summary>
    ModifyColumn,
    /// <summary>删除列</summary>
    DropColumn,
    /// <summary>添加表注释</summary>
    AddTableComment,
    /// <summary>添加列注释</summary>
    AddColumnComment
}

/// <summary>ALTER TABLE 语句</summary>
public class AlterTableStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.AlterTable;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>操作类型</summary>
    public AlterTableAction Action { get; set; }

    /// <summary>列定义（ADD COLUMN / MODIFY COLUMN 时使用）</summary>
    public SqlColumnDef? ColumnDef { get; set; }

    /// <summary>列名（DROP COLUMN 时使用）</summary>
    public String? ColumnName { get; set; }

    /// <summary>注释内容</summary>
    public String? Comment { get; set; }
}

/// <summary>INSERT 语句</summary>
public class InsertStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Insert;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>列名列表（可选）</summary>
    public List<String>? Columns { get; set; }

    /// <summary>值列表（多行插入）</summary>
    public List<List<SqlExpression>> ValuesList { get; set; } = [];
}

/// <summary>UPSERT 语句（INSERT ... ON DUPLICATE KEY UPDATE）</summary>
public class UpsertStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Upsert;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>列名列表（可选）</summary>
    public List<String>? Columns { get; set; }

    /// <summary>值列表（多行插入）</summary>
    public List<List<SqlExpression>> ValuesList { get; set; } = [];

    /// <summary>ON DUPLICATE KEY UPDATE 的 SET 子句</summary>
    public List<(String Column, SqlExpression Value)> UpdateClauses { get; set; } = [];
}

/// <summary>UPDATE 语句</summary>
public class UpdateStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Update;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>SET 子句（列名 -> 表达式）</summary>
    public List<(String Column, SqlExpression Value)> SetClauses { get; set; } = [];

    /// <summary>WHERE 条件</summary>
    public SqlExpression? Where { get; set; }
}

/// <summary>DELETE 语句</summary>
public class DeleteStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Delete;

    /// <summary>表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>WHERE 条件</summary>
    public SqlExpression? Where { get; set; }
}

/// <summary>SELECT 语句</summary>
public class SelectStatement : SqlStatement
{
    /// <summary>语句类型</summary>
    public override SqlStatementType StatementType => SqlStatementType.Select;

    /// <summary>投影列</summary>
    public List<SelectColumn> Columns { get; set; } = [];

    /// <summary>FROM 表名</summary>
    public String? TableName { get; set; }

    /// <summary>FROM 表别名</summary>
    public String? TableAlias { get; set; }

    /// <summary>JOIN 子句列表</summary>
    public List<JoinClause>? Joins { get; set; }

    /// <summary>WHERE 条件</summary>
    public SqlExpression? Where { get; set; }

    /// <summary>ORDER BY 子句</summary>
    public List<OrderByClause>? OrderBy { get; set; }

    /// <summary>GROUP BY 列</summary>
    public List<String>? GroupBy { get; set; }

    /// <summary>HAVING 条件</summary>
    public SqlExpression? Having { get; set; }

    /// <summary>LIMIT</summary>
    public Int32? Limit { get; set; }

    /// <summary>OFFSET</summary>
    public Int32? OffsetValue { get; set; }

    /// <summary>是否为 SELECT *</summary>
    public Boolean IsSelectAll => Columns.Count == 1 && Columns[0].IsWildcard;

    /// <summary>是否包含 JOIN</summary>
    public Boolean HasJoin => Joins != null && Joins.Count > 0;
}

/// <summary>SELECT 列</summary>
public class SelectColumn
{
    /// <summary>表达式</summary>
    public SqlExpression Expression { get; set; } = null!;

    /// <summary>别名</summary>
    public String? Alias { get; set; }

    /// <summary>是否为通配符 *</summary>
    public Boolean IsWildcard { get; set; }
}

/// <summary>JOIN 类型</summary>
public enum JoinType
{
    /// <summary>INNER JOIN</summary>
    Inner,
    /// <summary>LEFT JOIN</summary>
    Left,
    /// <summary>RIGHT JOIN</summary>
    Right
}

/// <summary>JOIN 子句</summary>
public class JoinClause
{
    /// <summary>JOIN 类型</summary>
    public JoinType Type { get; set; }

    /// <summary>右表名</summary>
    public String TableName { get; set; } = String.Empty;

    /// <summary>表别名</summary>
    public String? Alias { get; set; }

    /// <summary>ON 条件</summary>
    public SqlExpression Condition { get; set; } = null!;
}

/// <summary>ORDER BY 子句</summary>
public class OrderByClause
{
    /// <summary>列名</summary>
    public String ColumnName { get; set; } = String.Empty;

    /// <summary>是否降序</summary>
    public Boolean Descending { get; set; }
}

#region 表达式

/// <summary>SQL 表达式类型</summary>
public enum SqlExpressionType
{
    /// <summary>字面量</summary>
    Literal,
    /// <summary>列引用</summary>
    ColumnRef,
    /// <summary>二元运算</summary>
    Binary,
    /// <summary>一元运算</summary>
    Unary,
    /// <summary>函数调用</summary>
    Function,
    /// <summary>参数引用</summary>
    Parameter,
    /// <summary>IS NULL / IS NOT NULL</summary>
    IsNull,
    /// <summary>CASE WHEN 条件表达式</summary>
    CaseWhen,
    /// <summary>CAST 类型转换</summary>
    CastExpr
}

/// <summary>SQL 表达式基类</summary>
public abstract class SqlExpression
{
    /// <summary>表达式类型</summary>
    public abstract SqlExpressionType ExprType { get; }
}

/// <summary>字面量表达式</summary>
public class LiteralExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.Literal;

    /// <summary>值</summary>
    public Object? Value { get; set; }

    /// <summary>数据类型</summary>
    public DataType DataType { get; set; }
}

/// <summary>列引用表达式</summary>
public class ColumnRefExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.ColumnRef;

    /// <summary>列名</summary>
    public String ColumnName { get; set; } = String.Empty;

    /// <summary>表名前缀（可选）</summary>
    public String? TablePrefix { get; set; }
}

/// <summary>二元运算类型</summary>
public enum BinaryOperator
{
    /// <summary>等于</summary>
    Equal,
    /// <summary>不等于</summary>
    NotEqual,
    /// <summary>小于</summary>
    LessThan,
    /// <summary>大于</summary>
    GreaterThan,
    /// <summary>小于等于</summary>
    LessOrEqual,
    /// <summary>大于等于</summary>
    GreaterOrEqual,
    /// <summary>逻辑与</summary>
    And,
    /// <summary>逻辑或</summary>
    Or,
    /// <summary>加</summary>
    Add,
    /// <summary>减</summary>
    Subtract,
    /// <summary>乘</summary>
    Multiply,
    /// <summary>除</summary>
    Divide,
    /// <summary>取模</summary>
    Modulo,
    /// <summary>LIKE</summary>
    Like
}

/// <summary>二元运算表达式</summary>
public class BinaryExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.Binary;

    /// <summary>左操作数</summary>
    public SqlExpression Left { get; set; } = null!;

    /// <summary>运算符</summary>
    public BinaryOperator Operator { get; set; }

    /// <summary>右操作数</summary>
    public SqlExpression Right { get; set; } = null!;
}

/// <summary>一元运算表达式</summary>
public class UnaryExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.Unary;

    /// <summary>运算符（如 NOT, -）</summary>
    public String Operator { get; set; } = String.Empty;

    /// <summary>操作数</summary>
    public SqlExpression Operand { get; set; } = null!;
}

/// <summary>聚合函数类型</summary>
public enum AggregateFunctionType
{
    /// <summary>COUNT</summary>
    Count,
    /// <summary>SUM</summary>
    Sum,
    /// <summary>AVG</summary>
    Avg,
    /// <summary>MIN</summary>
    Min,
    /// <summary>MAX</summary>
    Max
}

/// <summary>函数调用表达式</summary>
public class FunctionExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.Function;

    /// <summary>函数名</summary>
    public String FunctionName { get; set; } = String.Empty;

    /// <summary>参数列表</summary>
    public List<SqlExpression> Arguments { get; set; } = [];

    /// <summary>是否为聚合函数</summary>
    public Boolean IsAggregate { get; set; }
}

/// <summary>参数引用表达式</summary>
public class ParameterExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.Parameter;

    /// <summary>参数名（包含 @ 前缀）</summary>
    public String ParameterName { get; set; } = String.Empty;
}

/// <summary>IS NULL / IS NOT NULL 表达式</summary>
public class IsNullExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.IsNull;

    /// <summary>操作数</summary>
    public SqlExpression Operand { get; set; } = null!;

    /// <summary>是否为 IS NOT NULL</summary>
    public Boolean IsNot { get; set; }
}

/// <summary>CASE WHEN ... THEN ... ELSE ... END 表达式</summary>
public class CaseExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.CaseWhen;

    /// <summary>WHEN/THEN 子句列表</summary>
    public List<(SqlExpression When, SqlExpression Then)> WhenClauses { get; set; } = [];

    /// <summary>ELSE 表达式</summary>
    public SqlExpression? ElseExpression { get; set; }
}

/// <summary>CAST(expr AS type) 表达式</summary>
public class CastExpression : SqlExpression
{
    /// <summary>表达式类型</summary>
    public override SqlExpressionType ExprType => SqlExpressionType.CastExpr;

    /// <summary>操作数</summary>
    public SqlExpression Operand { get; set; } = null!;

    /// <summary>目标类型名称</summary>
    public String TargetTypeName { get; set; } = String.Empty;
}

#endregion

using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 解析器，将 SQL 文本解析为 AST</summary>
public class SqlParser
{
    private readonly List<SqlToken> _tokens;
    private Int32 _pos;

    /// <summary>创建 SQL 解析器</summary>
    /// <param name="sql">SQL 文本</param>
    public SqlParser(String sql)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));

        var lexer = new SqlLexer(sql);
        _tokens = lexer.Tokenize();
        _pos = 0;
    }

    /// <summary>解析 SQL 语句</summary>
    /// <returns>SQL AST</returns>
    public SqlStatement Parse()
    {
        var token = Peek();
        var stmt = token.Type switch
        {
            SqlTokenType.Select => (SqlStatement)ParseSelect(),
            SqlTokenType.Insert => ParseInsert(),
            SqlTokenType.Update => ParseUpdate(),
            SqlTokenType.Delete => ParseDelete(),
            SqlTokenType.Create => ParseCreate(),
            SqlTokenType.Drop => ParseDrop(),
            _ => throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}")
        };

        // 跳过可选的分号
        if (Peek().Type == SqlTokenType.Semicolon)
            Advance();

        return stmt;
    }

    #region DDL

    private SqlStatement ParseCreate()
    {
        Expect(SqlTokenType.Create);

        var next = Peek();
        if (next.Type == SqlTokenType.Table)
            return ParseCreateTable();
        if (next.Type == SqlTokenType.Unique || next.Type == SqlTokenType.Index)
            return ParseCreateIndex();

        throw SyntaxError($"Expected TABLE or INDEX after CREATE, got '{next.Value}'");
    }

    private CreateTableStatement ParseCreateTable()
    {
        Expect(SqlTokenType.Table);

        var stmt = new CreateTableStatement();

        // IF NOT EXISTS
        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Not);
            Expect(SqlTokenType.Exists);
            stmt.IfNotExists = true;
        }

        stmt.TableName = ExpectIdentifier();
        Expect(SqlTokenType.LeftParen);

        // 解析列定义
        String? primaryKeyColumn = null;
        do
        {
            // 检查是否为 PRIMARY KEY 约束
            if (Peek().Type == SqlTokenType.Primary)
            {
                Advance();
                Expect(SqlTokenType.Key);
                Expect(SqlTokenType.LeftParen);
                primaryKeyColumn = ExpectIdentifier();
                Expect(SqlTokenType.RightParen);
                continue;
            }

            var colDef = ParseColumnDef();
            stmt.Columns.Add(colDef);
        }
        while (TryConsume(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);

        // 应用表级 PRIMARY KEY 约束
        if (primaryKeyColumn != null)
        {
            foreach (var col in stmt.Columns)
            {
                if (String.Equals(col.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                {
                    col.IsPrimaryKey = true;
                    break;
                }
            }
        }

        return stmt;
    }

    private SqlColumnDef ParseColumnDef()
    {
        var col = new SqlColumnDef
        {
            Name = ExpectIdentifier(),
            DataTypeName = ExpectIdentifier()
        };

        // 解析列约束
        while (Peek().Type != SqlTokenType.Comma && Peek().Type != SqlTokenType.RightParen && Peek().Type != SqlTokenType.Eof)
        {
            var next = Peek();
            if (next.Type == SqlTokenType.Primary)
            {
                Advance();
                Expect(SqlTokenType.Key);
                col.IsPrimaryKey = true;
            }
            else if (next.Type == SqlTokenType.Not)
            {
                Advance();
                Expect(SqlTokenType.Null);
                col.NotNull = true;
            }
            else if (next.Type == SqlTokenType.Null)
            {
                Advance();
                col.NotNull = false;
            }
            else
            {
                break;
            }
        }

        return col;
    }

    private CreateIndexStatement ParseCreateIndex()
    {
        var stmt = new CreateIndexStatement();

        if (Peek().Type == SqlTokenType.Unique)
        {
            Advance();
            stmt.IsUnique = true;
        }

        Expect(SqlTokenType.Index);
        stmt.IndexName = ExpectIdentifier();
        Expect(SqlTokenType.On);
        stmt.TableName = ExpectIdentifier();
        Expect(SqlTokenType.LeftParen);

        do
        {
            stmt.Columns.Add(ExpectIdentifier());
        }
        while (TryConsume(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);
        return stmt;
    }

    private SqlStatement ParseDrop()
    {
        Expect(SqlTokenType.Drop);

        var next = Peek();
        if (next.Type == SqlTokenType.Table)
            return ParseDropTable();
        if (next.Type == SqlTokenType.Index)
            return ParseDropIndex();

        throw SyntaxError($"Expected TABLE or INDEX after DROP, got '{next.Value}'");
    }

    private DropTableStatement ParseDropTable()
    {
        Expect(SqlTokenType.Table);

        var stmt = new DropTableStatement();

        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Exists);
            stmt.IfExists = true;
        }

        stmt.TableName = ExpectIdentifier();
        return stmt;
    }

    private DropIndexStatement ParseDropIndex()
    {
        Expect(SqlTokenType.Index);

        var stmt = new DropIndexStatement
        {
            IndexName = ExpectIdentifier()
        };

        Expect(SqlTokenType.On);
        stmt.TableName = ExpectIdentifier();
        return stmt;
    }

    #endregion

    #region DML

    private InsertStatement ParseInsert()
    {
        Expect(SqlTokenType.Insert);
        Expect(SqlTokenType.Into);

        var stmt = new InsertStatement
        {
            TableName = ExpectIdentifier()
        };

        // 可选的列名列表
        if (Peek().Type == SqlTokenType.LeftParen)
        {
            Advance();
            stmt.Columns = [];

            do
            {
                stmt.Columns.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
        }

        Expect(SqlTokenType.Values);

        // 解析一组或多组值
        do
        {
            Expect(SqlTokenType.LeftParen);
            var values = new List<SqlExpression>();

            do
            {
                values.Add(ParseExpression());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
            stmt.ValuesList.Add(values);
        }
        while (TryConsume(SqlTokenType.Comma));

        return stmt;
    }

    private UpdateStatement ParseUpdate()
    {
        Expect(SqlTokenType.Update);

        var stmt = new UpdateStatement
        {
            TableName = ExpectIdentifier()
        };

        Expect(SqlTokenType.Set);

        do
        {
            var column = ExpectIdentifier();
            Expect(SqlTokenType.Equals);
            var value = ParseExpression();
            stmt.SetClauses.Add((column, value));
        }
        while (TryConsume(SqlTokenType.Comma));

        // WHERE 子句
        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        return stmt;
    }

    private DeleteStatement ParseDelete()
    {
        Expect(SqlTokenType.Delete);
        Expect(SqlTokenType.From);

        var stmt = new DeleteStatement
        {
            TableName = ExpectIdentifier()
        };

        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        return stmt;
    }

    #endregion

    #region SELECT

    private SelectStatement ParseSelect()
    {
        Expect(SqlTokenType.Select);

        var stmt = new SelectStatement();

        // 投影列
        if (Peek().Type == SqlTokenType.Star)
        {
            Advance();
            stmt.Columns.Add(new SelectColumn { IsWildcard = true });
        }
        else
        {
            do
            {
                stmt.Columns.Add(ParseSelectColumn());
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        // FROM
        if (Peek().Type == SqlTokenType.From)
        {
            Advance();
            stmt.TableName = ExpectIdentifier();
        }

        // WHERE
        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        // GROUP BY
        if (Peek().Type == SqlTokenType.Group)
        {
            Advance();
            Expect(SqlTokenType.By);
            stmt.GroupBy = [];

            do
            {
                stmt.GroupBy.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        // HAVING
        if (Peek().Type == SqlTokenType.Having)
        {
            Advance();
            stmt.Having = ParseExpression();
        }

        // ORDER BY
        if (Peek().Type == SqlTokenType.Order)
        {
            Advance();
            Expect(SqlTokenType.By);
            stmt.OrderBy = [];

            do
            {
                var clause = new OrderByClause { ColumnName = ExpectIdentifier() };
                if (Peek().Type == SqlTokenType.Desc)
                {
                    Advance();
                    clause.Descending = true;
                }
                else if (Peek().Type == SqlTokenType.Asc)
                {
                    Advance();
                }
                stmt.OrderBy.Add(clause);
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        // LIMIT
        if (Peek().Type == SqlTokenType.Limit)
        {
            Advance();
            stmt.Limit = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value);
        }

        // OFFSET
        if (Peek().Type == SqlTokenType.Offset)
        {
            Advance();
            stmt.OffsetValue = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value);
        }

        return stmt;
    }

    private SelectColumn ParseSelectColumn()
    {
        var col = new SelectColumn();

        // 检查聚合函数
        var funcType = Peek().Type;
        if (funcType is SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg or SqlTokenType.Min or SqlTokenType.Max)
        {
            col.Expression = ParseFunction();
        }
        else
        {
            col.Expression = ParseExpression();
        }

        // 别名
        if (Peek().Type == SqlTokenType.As)
        {
            Advance();
            col.Alias = ExpectIdentifier();
        }
        else if (Peek().Type == SqlTokenType.Identifier)
        {
            // 无 AS 关键字的别名
            col.Alias = ExpectIdentifier();
        }

        return col;
    }

    #endregion

    #region 表达式解析

    private SqlExpression ParseExpression() => ParseOr();

    private SqlExpression ParseOr()
    {
        var left = ParseAnd();

        while (Peek().Type == SqlTokenType.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpression { Left = left, Operator = BinaryOperator.Or, Right = right };
        }

        return left;
    }

    private SqlExpression ParseAnd()
    {
        var left = ParseComparison();

        while (Peek().Type == SqlTokenType.And)
        {
            Advance();
            var right = ParseComparison();
            left = new BinaryExpression { Left = left, Operator = BinaryOperator.And, Right = right };
        }

        return left;
    }

    private SqlExpression ParseComparison()
    {
        var left = ParseAddSub();

        var next = Peek();

        // IS NULL / IS NOT NULL
        if (next.Type == SqlTokenType.Is)
        {
            Advance();
            var isNot = false;
            if (Peek().Type == SqlTokenType.Not)
            {
                Advance();
                isNot = true;
            }
            Expect(SqlTokenType.Null);
            return new IsNullExpression { Operand = left, IsNot = isNot };
        }

        // LIKE
        if (next.Type == SqlTokenType.Like)
        {
            Advance();
            var right = ParseAddSub();
            return new BinaryExpression { Left = left, Operator = BinaryOperator.Like, Right = right };
        }

        // 比较运算符
        var op = next.Type switch
        {
            SqlTokenType.Equals => (BinaryOperator?)BinaryOperator.Equal,
            SqlTokenType.NotEquals => BinaryOperator.NotEqual,
            SqlTokenType.LessThan => BinaryOperator.LessThan,
            SqlTokenType.GreaterThan => BinaryOperator.GreaterThan,
            SqlTokenType.LessThanOrEqual => BinaryOperator.LessOrEqual,
            SqlTokenType.GreaterThanOrEqual => BinaryOperator.GreaterOrEqual,
            _ => null
        };

        if (op.HasValue)
        {
            Advance();
            var right = ParseAddSub();
            return new BinaryExpression { Left = left, Operator = op.Value, Right = right };
        }

        return left;
    }

    private SqlExpression ParseAddSub()
    {
        var left = ParseMulDiv();

        while (Peek().Type is SqlTokenType.Plus or SqlTokenType.Minus)
        {
            var op = Advance().Type == SqlTokenType.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            var right = ParseMulDiv();
            left = new BinaryExpression { Left = left, Operator = op, Right = right };
        }

        return left;
    }

    private SqlExpression ParseMulDiv()
    {
        var left = ParseUnary();

        while (Peek().Type is SqlTokenType.Star or SqlTokenType.Slash)
        {
            var op = Advance().Type == SqlTokenType.Star ? BinaryOperator.Multiply : BinaryOperator.Divide;
            var right = ParseUnary();
            left = new BinaryExpression { Left = left, Operator = op, Right = right };
        }

        return left;
    }

    private SqlExpression ParseUnary()
    {
        if (Peek().Type == SqlTokenType.Not)
        {
            Advance();
            var operand = ParseUnary();
            return new UnaryExpression { Operator = "NOT", Operand = operand };
        }

        if (Peek().Type == SqlTokenType.Minus)
        {
            Advance();
            var operand = ParsePrimary();
            return new UnaryExpression { Operator = "-", Operand = operand };
        }

        return ParsePrimary();
    }

    private SqlExpression ParsePrimary()
    {
        var token = Peek();

        switch (token.Type)
        {
            case SqlTokenType.IntegerLiteral:
                Advance();
                return new LiteralExpression
                {
                    Value = Int64.Parse(token.Value),
                    DataType = Int64.Parse(token.Value) is >= Int32.MinValue and <= Int32.MaxValue ? DataType.Int32 : DataType.Int64
                };

            case SqlTokenType.FloatLiteral:
                Advance();
                return new LiteralExpression { Value = Double.Parse(token.Value), DataType = DataType.Double };

            case SqlTokenType.StringLiteral:
                Advance();
                return new LiteralExpression { Value = token.Value, DataType = DataType.String };

            case SqlTokenType.True:
                Advance();
                return new LiteralExpression { Value = true, DataType = DataType.Boolean };

            case SqlTokenType.False:
                Advance();
                return new LiteralExpression { Value = false, DataType = DataType.Boolean };

            case SqlTokenType.Null:
                Advance();
                return new LiteralExpression { Value = null, DataType = DataType.String };

            case SqlTokenType.Parameter:
                Advance();
                return new ParameterExpression { ParameterName = token.Value };

            case SqlTokenType.LeftParen:
                Advance();
                var expr = ParseExpression();
                Expect(SqlTokenType.RightParen);
                return expr;

            // 聚合函数
            case SqlTokenType.Count:
            case SqlTokenType.Sum:
            case SqlTokenType.Avg:
            case SqlTokenType.Min:
            case SqlTokenType.Max:
                return ParseFunction();

            case SqlTokenType.Star:
                Advance();
                return new ColumnRefExpression { ColumnName = "*" };

            case SqlTokenType.Identifier:
                Advance();
                // 检查表名前缀 table.column
                if (Peek().Type == SqlTokenType.Dot)
                {
                    Advance();
                    var colName = ExpectIdentifier();
                    return new ColumnRefExpression { TablePrefix = token.Value, ColumnName = colName };
                }
                return new ColumnRefExpression { ColumnName = token.Value };

            default:
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");
        }
    }

    private FunctionExpression ParseFunction()
    {
        var funcToken = Advance();
        Expect(SqlTokenType.LeftParen);

        var func = new FunctionExpression
        {
            FunctionName = funcToken.Value.ToUpper(),
            IsAggregate = funcToken.Type is SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg or SqlTokenType.Min or SqlTokenType.Max
        };

        // 解析参数
        if (Peek().Type != SqlTokenType.RightParen)
        {
            // COUNT(*) 特殊处理
            if (Peek().Type == SqlTokenType.Star)
            {
                Advance();
                func.Arguments.Add(new ColumnRefExpression { ColumnName = "*" });
            }
            else
            {
                do
                {
                    func.Arguments.Add(ParseExpression());
                }
                while (TryConsume(SqlTokenType.Comma));
            }
        }

        Expect(SqlTokenType.RightParen);
        return func;
    }

    #endregion

    #region 辅助

    private SqlToken Peek() => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private SqlToken Advance()
    {
        var token = _tokens[_pos];
        _pos++;
        return token;
    }

    private SqlToken Expect(SqlTokenType type)
    {
        var token = Peek();
        if (token.Type != type)
            throw SyntaxError($"Expected {type}, got '{token.Value}' ({token.Type}) at position {token.Position}");

        return Advance();
    }

    private String ExpectIdentifier()
    {
        var token = Peek();
        if (token.Type != SqlTokenType.Identifier)
            throw SyntaxError($"Expected identifier, got '{token.Value}' ({token.Type}) at position {token.Position}");

        Advance();
        return token.Value;
    }

    private Boolean TryConsume(SqlTokenType type)
    {
        if (Peek().Type == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    private static NovaDbException SyntaxError(String message) => new(ErrorCode.SyntaxError, message);

    #endregion
}

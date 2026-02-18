using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 词法分析器</summary>
public class SqlLexer
{
    private readonly String _sql;
    private Int32 _pos;

    private static readonly Dictionary<String, SqlTokenType> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = SqlTokenType.Select,
        ["FROM"] = SqlTokenType.From,
        ["WHERE"] = SqlTokenType.Where,
        ["INSERT"] = SqlTokenType.Insert,
        ["INTO"] = SqlTokenType.Into,
        ["VALUES"] = SqlTokenType.Values,
        ["UPDATE"] = SqlTokenType.Update,
        ["SET"] = SqlTokenType.Set,
        ["DELETE"] = SqlTokenType.Delete,
        ["CREATE"] = SqlTokenType.Create,
        ["DROP"] = SqlTokenType.Drop,
        ["TABLE"] = SqlTokenType.Table,
        ["INDEX"] = SqlTokenType.Index,
        ["ON"] = SqlTokenType.On,
        ["PRIMARY"] = SqlTokenType.Primary,
        ["KEY"] = SqlTokenType.Key,
        ["NOT"] = SqlTokenType.Not,
        ["NULL"] = SqlTokenType.Null,
        ["AND"] = SqlTokenType.And,
        ["OR"] = SqlTokenType.Or,
        ["ORDER"] = SqlTokenType.Order,
        ["BY"] = SqlTokenType.By,
        ["ASC"] = SqlTokenType.Asc,
        ["DESC"] = SqlTokenType.Desc,
        ["GROUP"] = SqlTokenType.Group,
        ["HAVING"] = SqlTokenType.Having,
        ["AS"] = SqlTokenType.As,
        ["COUNT"] = SqlTokenType.Count,
        ["SUM"] = SqlTokenType.Sum,
        ["AVG"] = SqlTokenType.Avg,
        ["MIN"] = SqlTokenType.Min,
        ["MAX"] = SqlTokenType.Max,
        ["IF"] = SqlTokenType.If,
        ["EXISTS"] = SqlTokenType.Exists,
        ["UNIQUE"] = SqlTokenType.Unique,
        ["TRUE"] = SqlTokenType.True,
        ["FALSE"] = SqlTokenType.False,
        ["LIKE"] = SqlTokenType.Like,
        ["IN"] = SqlTokenType.In,
        ["BETWEEN"] = SqlTokenType.Between,
        ["IS"] = SqlTokenType.Is,
        ["LIMIT"] = SqlTokenType.Limit,
        ["OFFSET"] = SqlTokenType.Offset,
        ["JOIN"] = SqlTokenType.Join,
        ["LEFT"] = SqlTokenType.Left,
        ["RIGHT"] = SqlTokenType.Right,
        ["INNER"] = SqlTokenType.Inner,
        ["CASE"] = SqlTokenType.Case,
        ["WHEN"] = SqlTokenType.When,
        ["THEN"] = SqlTokenType.Then,
        ["ELSE"] = SqlTokenType.Else,
        ["END"] = SqlTokenType.End,
        ["CAST"] = SqlTokenType.Cast,
        ["STRING_AGG"] = SqlTokenType.StringAgg,
        ["GROUP_CONCAT"] = SqlTokenType.GroupConcat,
        ["STDDEV"] = SqlTokenType.Stddev,
        ["VARIANCE"] = SqlTokenType.Variance,
    };

    /// <summary>创建词法分析器</summary>
    /// <param name="sql">SQL 字符串</param>
    public SqlLexer(String sql)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _pos = 0;
    }

    /// <summary>将 SQL 文本分词为词法单元列表</summary>
    /// <returns>词法单元列表</returns>
    public List<SqlToken> Tokenize()
    {
        var tokens = new List<SqlToken>();

        while (_pos < _sql.Length)
        {
            SkipWhitespace();
            if (_pos >= _sql.Length) break;

            var ch = _sql[_pos];

            // 跳过行注释
            if (ch == '-' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '-')
            {
                while (_pos < _sql.Length && _sql[_pos] != '\n')
                    _pos++;
                continue;
            }

            // 标识符或关键字
            if (Char.IsLetter(ch) || ch == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            // 数字字面量
            if (Char.IsDigit(ch))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // 字符串字面量
            if (ch == '\'')
            {
                tokens.Add(ReadString());
                continue;
            }

            // 参数
            if (ch == '@')
            {
                tokens.Add(ReadParameter());
                continue;
            }

            // 运算符和标点
            tokens.Add(ReadOperatorOrPunctuation());
        }

        tokens.Add(new SqlToken(SqlTokenType.Eof, String.Empty, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < _sql.Length && Char.IsWhiteSpace(_sql[_pos]))
            _pos++;
    }

    private SqlToken ReadIdentifierOrKeyword()
    {
        var start = _pos;
        while (_pos < _sql.Length && (Char.IsLetterOrDigit(_sql[_pos]) || _sql[_pos] == '_'))
            _pos++;

        var value = _sql[start.._pos];

        if (_keywords.TryGetValue(value, out var keywordType))
            return new SqlToken(keywordType, value, start);

        return new SqlToken(SqlTokenType.Identifier, value, start);
    }

    private SqlToken ReadNumber()
    {
        var start = _pos;
        var isFloat = false;

        while (_pos < _sql.Length && (Char.IsDigit(_sql[_pos]) || _sql[_pos] == '.'))
        {
            if (_sql[_pos] == '.')
            {
                if (isFloat) break;
                isFloat = true;
            }
            _pos++;
        }

        var value = _sql[start.._pos];
        return new SqlToken(isFloat ? SqlTokenType.FloatLiteral : SqlTokenType.IntegerLiteral, value, start);
    }

    private SqlToken ReadString()
    {
        var start = _pos;
        _pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();

        while (_pos < _sql.Length)
        {
            if (_sql[_pos] == '\'')
            {
                // 处理转义的单引号
                if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '\'')
                {
                    sb.Append('\'');
                    _pos += 2;
                    continue;
                }

                _pos++; // skip closing quote
                return new SqlToken(SqlTokenType.StringLiteral, sb.ToString(), start);
            }
            sb.Append(_sql[_pos]);
            _pos++;
        }

        throw new NovaException(ErrorCode.SyntaxError, $"Unterminated string literal at position {start}");
    }

    private SqlToken ReadParameter()
    {
        var start = _pos;
        _pos++; // skip @

        while (_pos < _sql.Length && (Char.IsLetterOrDigit(_sql[_pos]) || _sql[_pos] == '_'))
            _pos++;

        var value = _sql[start.._pos];
        return new SqlToken(SqlTokenType.Parameter, value, start);
    }

    private SqlToken ReadOperatorOrPunctuation()
    {
        var start = _pos;
        var ch = _sql[_pos];
        _pos++;

        switch (ch)
        {
            case '(':
                return new SqlToken(SqlTokenType.LeftParen, "(", start);
            case ')':
                return new SqlToken(SqlTokenType.RightParen, ")", start);
            case ',':
                return new SqlToken(SqlTokenType.Comma, ",", start);
            case ';':
                return new SqlToken(SqlTokenType.Semicolon, ";", start);
            case '.':
                return new SqlToken(SqlTokenType.Dot, ".", start);
            case '+':
                return new SqlToken(SqlTokenType.Plus, "+", start);
            case '-':
                return new SqlToken(SqlTokenType.Minus, "-", start);
            case '*':
                return new SqlToken(SqlTokenType.Star, "*", start);
            case '/':
                return new SqlToken(SqlTokenType.Slash, "/", start);
            case '%':
                return new SqlToken(SqlTokenType.Percent, "%", start);
            case '=':
                return new SqlToken(SqlTokenType.Equals, "=", start);
            case '<':
                if (_pos < _sql.Length && _sql[_pos] == '=')
                {
                    _pos++;
                    return new SqlToken(SqlTokenType.LessThanOrEqual, "<=", start);
                }
                if (_pos < _sql.Length && _sql[_pos] == '>')
                {
                    _pos++;
                    return new SqlToken(SqlTokenType.NotEquals, "<>", start);
                }
                return new SqlToken(SqlTokenType.LessThan, "<", start);
            case '>':
                if (_pos < _sql.Length && _sql[_pos] == '=')
                {
                    _pos++;
                    return new SqlToken(SqlTokenType.GreaterThanOrEqual, ">=", start);
                }
                return new SqlToken(SqlTokenType.GreaterThan, ">", start);
            case '!':
                if (_pos < _sql.Length && _sql[_pos] == '=')
                {
                    _pos++;
                    return new SqlToken(SqlTokenType.NotEquals, "!=", start);
                }
                break;
        }

        throw new NovaException(ErrorCode.SyntaxError, $"Unexpected character '{ch}' at position {start}");
    }
}

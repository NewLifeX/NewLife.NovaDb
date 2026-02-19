namespace NewLife.NovaDb.Sql;

/// <summary>SQL 词法单元类型</summary>
public enum SqlTokenType
{
    // 关键字
    /// <summary>SELECT</summary>
    Select,
    /// <summary>FROM</summary>
    From,
    /// <summary>WHERE</summary>
    Where,
    /// <summary>INSERT</summary>
    Insert,
    /// <summary>INTO</summary>
    Into,
    /// <summary>VALUES</summary>
    Values,
    /// <summary>UPDATE</summary>
    Update,
    /// <summary>SET</summary>
    Set,
    /// <summary>DELETE</summary>
    Delete,
    /// <summary>CREATE</summary>
    Create,
    /// <summary>DROP</summary>
    Drop,
    /// <summary>TABLE</summary>
    Table,
    /// <summary>INDEX</summary>
    Index,
    /// <summary>ON</summary>
    On,
    /// <summary>PRIMARY</summary>
    Primary,
    /// <summary>KEY</summary>
    Key,
    /// <summary>NOT</summary>
    Not,
    /// <summary>NULL</summary>
    Null,
    /// <summary>AND</summary>
    And,
    /// <summary>OR</summary>
    Or,
    /// <summary>ORDER</summary>
    Order,
    /// <summary>BY</summary>
    By,
    /// <summary>ASC</summary>
    Asc,
    /// <summary>DESC</summary>
    Desc,
    /// <summary>GROUP</summary>
    Group,
    /// <summary>HAVING</summary>
    Having,
    /// <summary>AS</summary>
    As,
    /// <summary>COUNT</summary>
    Count,
    /// <summary>SUM</summary>
    Sum,
    /// <summary>AVG</summary>
    Avg,
    /// <summary>MIN</summary>
    Min,
    /// <summary>MAX</summary>
    Max,
    /// <summary>IF</summary>
    If,
    /// <summary>EXISTS</summary>
    Exists,
    /// <summary>UNIQUE</summary>
    Unique,
    /// <summary>LIKE</summary>
    Like,
    /// <summary>IN</summary>
    In,
    /// <summary>BETWEEN</summary>
    Between,
    /// <summary>IS</summary>
    Is,
    /// <summary>LIMIT</summary>
    Limit,
    /// <summary>OFFSET</summary>
    Offset,
    /// <summary>JOIN</summary>
    Join,
    /// <summary>LEFT</summary>
    Left,
    /// <summary>RIGHT</summary>
    Right,
    /// <summary>INNER</summary>
    Inner,
    /// <summary>CASE</summary>
    Case,
    /// <summary>WHEN</summary>
    When,
    /// <summary>THEN</summary>
    Then,
    /// <summary>ELSE</summary>
    Else,
    /// <summary>END</summary>
    End,
    /// <summary>CAST</summary>
    Cast,
    /// <summary>STRING_AGG</summary>
    StringAgg,
    /// <summary>GROUP_CONCAT</summary>
    GroupConcat,
    /// <summary>STDDEV</summary>
    Stddev,
    /// <summary>VARIANCE</summary>
    Variance,
    /// <summary>TRUNCATE</summary>
    Truncate,

    // 标识符与字面量
    /// <summary>标识符</summary>
    Identifier,
    /// <summary>整数字面量</summary>
    IntegerLiteral,
    /// <summary>浮点字面量</summary>
    FloatLiteral,
    /// <summary>字符串字面量</summary>
    StringLiteral,
    /// <summary>布尔字面量 TRUE</summary>
    True,
    /// <summary>布尔字面量 FALSE</summary>
    False,

    // 运算符与标点
    /// <summary>等号 =</summary>
    Equals,
    /// <summary>不等号 !=</summary>
    NotEquals,
    /// <summary>小于 &lt;</summary>
    LessThan,
    /// <summary>大于 &gt;</summary>
    GreaterThan,
    /// <summary>小于等于 &lt;=</summary>
    LessThanOrEqual,
    /// <summary>大于等于 &gt;=</summary>
    GreaterThanOrEqual,
    /// <summary>加号 +</summary>
    Plus,
    /// <summary>减号 -</summary>
    Minus,
    /// <summary>乘号 *</summary>
    Star,
    /// <summary>除号 /</summary>
    Slash,
    /// <summary>取模 %</summary>
    Percent,
    /// <summary>左括号 (</summary>
    LeftParen,
    /// <summary>右括号 )</summary>
    RightParen,
    /// <summary>逗号 ,</summary>
    Comma,
    /// <summary>分号 ;</summary>
    Semicolon,
    /// <summary>句点 .</summary>
    Dot,
    /// <summary>参数占位 @</summary>
    Parameter,

    // 结束
    /// <summary>文件结束</summary>
    Eof
}

/// <summary>SQL 词法单元</summary>
public class SqlToken
{
    /// <summary>词法单元类型</summary>
    public SqlTokenType Type { get; }

    /// <summary>词法单元值</summary>
    public String Value { get; }

    /// <summary>位置</summary>
    public Int32 Position { get; }

    /// <summary>创建词法单元</summary>
    /// <param name="type">类型</param>
    /// <param name="value">值</param>
    /// <param name="position">位置</param>
    public SqlToken(SqlTokenType type, String value, Int32 position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    /// <summary>字符串表示</summary>
    public override String ToString() => $"[{Type}] {Value}";
}

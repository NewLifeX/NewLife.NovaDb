using System.Data.Common;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb 连接字符串构建器。支持嵌入模式和服务器模式</summary>
/// <remarks>
/// 嵌入模式连接字符串：Data Source=../data/member
/// 服务器模式连接字符串：Server=localhost;Port=3306;Database=member;UserId=root;Password=root
/// </remarks>
public class NovaConnectionStringBuilder : DbConnectionStringBuilder
{
    #region 属性
    /// <summary>数据源路径（嵌入模式）</summary>
    public String? DataSource { get => this[nameof(DataSource)] as String; set => this[nameof(DataSource)] = value; }

    /// <summary>服务器地址（服务器模式）</summary>
    public String? Server { get => this[nameof(Server)] as String; set => this[nameof(Server)] = value; }

    /// <summary>端口</summary>
    public Int32 Port
    {
        get => Int32.TryParse(this[nameof(Port)]?.ToString(), out var p) ? p : 3306;
        set => this[nameof(Port)] = value;
    }

    /// <summary>数据库名称</summary>
    public String? Database { get => this[nameof(Database)] as String; set => this[nameof(Database)] = value; }

    /// <summary>连接超时（秒）</summary>
    public Int32 ConnectionTimeout
    {
        get => Int32.TryParse(this[nameof(ConnectionTimeout)]?.ToString(), out var v) ? v : 15;
        set => this[nameof(ConnectionTimeout)] = value;
    }

    /// <summary>命令超时（秒）</summary>
    public Int32 CommandTimeout
    {
        get => Int32.TryParse(this[nameof(CommandTimeout)]?.ToString(), out var v) ? v : 30;
        set => this[nameof(CommandTimeout)] = value;
    }

    /// <summary>用户名（服务器模式）</summary>
    public String? UserId { get => this[nameof(UserId)] as String; set => this[nameof(UserId)] = value; }

    /// <summary>密码（服务器模式）</summary>
    public String? Password { get => this[nameof(Password)] as String; set => this[nameof(Password)] = value; }

    /// <summary>是否为嵌入模式</summary>
    public Boolean IsEmbedded => !String.IsNullOrEmpty(DataSource);
    #endregion

    #region 构造
    private static readonly IDictionary<String, String[]> _options;

    static NovaConnectionStringBuilder()
    {
        var dic = new Dictionary<String, String[]>
        {
            [nameof(DataSource)] = ["data source", "datasource"],
            [nameof(Server)] = ["server"],
            [nameof(Port)] = ["port"],
            [nameof(Database)] = ["database"],
            [nameof(ConnectionTimeout)] = ["connectiontimeout", "connection timeout"],
            [nameof(CommandTimeout)] = ["commandtimeout", "command timeout", "default command timeout"],
            [nameof(UserId)] = ["userid", "user id", "uid"],
            [nameof(Password)] = ["password", "pwd"],
        };

        _options = dic;
    }

    /// <summary>实例化</summary>
    public NovaConnectionStringBuilder()
    {
        Port = 3306;
        ConnectionTimeout = 15;
        CommandTimeout = 30;
    }

    /// <summary>使用连接字符串实例化</summary>
    /// <param name="connStr">连接字符串</param>
    public NovaConnectionStringBuilder(String connStr) : this() => ConnectionString = connStr;
    #endregion

    #region 方法
    /// <summary>索引器。自动将别名映射为标准键名</summary>
    /// <param name="keyword">键名或别名</param>
    /// <returns>键值</returns>
    public override Object? this[String keyword]
    {
        get => TryGetValue(keyword, out var value) ? value : null;
        set
        {
            // 替换为标准键名
            var kw = keyword.ToLower();
            foreach (var kv in _options)
            {
                if (kv.Value.Contains(kw))
                {
                    keyword = kv.Key;
                    break;
                }
            }

            base[keyword] = value;
        }
    }
    #endregion
}

using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 连接</summary>
public class NovaConnection : DbConnection
{
    private String _connectionString = String.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private String _database = String.Empty;
    private NovaClient? _client;
    private SqlEngine? _sqlEngine;

    /// <summary>连接字符串。格式：嵌入模式 "Data Source=path"，服务器模式 "Server=host;Port=3306"</summary>
    public override String ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? String.Empty;
    }

    /// <summary>数据库名称</summary>
    public override String Database => _database;

    /// <summary>数据源</summary>
    public override String DataSource
    {
        get
        {
            if (IsEmbedded)
                return ParseValue("Data Source");

            return ParseValue("Server");
        }
    }

    /// <summary>服务器版本</summary>
    public override String ServerVersion => "1.0";

    /// <summary>连接状态</summary>
    public override ConnectionState State => _state;

    /// <summary>是否为嵌入模式</summary>
    public Boolean IsEmbedded => _connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);

    /// <summary>远程客户端（服务器模式）</summary>
    public NovaClient? Client => _client;

    /// <summary>SQL 执行引擎（嵌入模式）</summary>
    public SqlEngine? SqlEngine => _sqlEngine;

    /// <summary>打开连接</summary>
    public override void Open()
    {
        if (IsEmbedded)
        {
            var dataSource = ParseValue("Data Source");
            if (!String.IsNullOrEmpty(dataSource))
            {
                var options = new DbOptions { Path = dataSource, WalMode = WalMode.None };
                _sqlEngine = new SqlEngine(dataSource, options);
            }
        }
        else
        {
            var server = ParseValue("Server");
            var portStr = ParseValue("Port");
            var port = Int32.TryParse(portStr, out var p) ? p : 3306;
            _client = new NovaClient($"tcp://{server}:{port}");
            _client.Open();
        }

        _state = ConnectionState.Open;
    }

    /// <summary>关闭连接</summary>
    public override void Close()
    {
        _client?.Close("Connection.Close");
        _client = null;

        _sqlEngine?.Dispose();
        _sqlEngine = null;

        _state = ConnectionState.Closed;
    }

    /// <summary>切换数据库</summary>
    /// <param name="databaseName">数据库名称</param>
    public override void ChangeDatabase(String databaseName) => _database = databaseName;

    /// <summary>开始事务</summary>
    /// <param name="isolationLevel">隔离级别</param>
    /// <returns>事务实例</returns>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new NovaTransaction(this);

    /// <summary>创建命令</summary>
    /// <returns>命令实例</returns>
    protected override DbCommand CreateDbCommand() => new NovaCommand { Connection = this };

    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _client?.Close(disposing ? "Dispose" : "GC");
        _client?.Dispose();
        _client = null;

        _sqlEngine?.Dispose();
        _sqlEngine = null;
    }

    #region 辅助

    /// <summary>从连接字符串中解析指定键的值</summary>
    private String ParseValue(String key)
    {
        if (String.IsNullOrEmpty(_connectionString)) return String.Empty;

        foreach (var part in _connectionString.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }

        return String.Empty;
    }

    #endregion
}

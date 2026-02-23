using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Client;

#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member

/// <summary>NovaDb ADO.NET 连接。支持嵌入模式和服务器模式</summary>
public class NovaConnection : DbConnection
{
    #region 属性
    private ConnectionState _state = ConnectionState.Closed;
    private String _database = String.Empty;
    private NovaClient? _client;
    private SqlEngine? _sqlEngine;
    private Boolean _fromPool;

    /// <summary>连接字符串设置</summary>
    public NovaConnectionStringBuilder Setting { get; } = [];

    /// <summary>连接字符串。格式：嵌入模式 "Data Source=path"，服务器模式 "Server=host;Port=3306"</summary>
    public override String ConnectionString
    {
        get => Setting.ConnectionString;
        set => Setting.ConnectionString = value ?? String.Empty;
    }

    /// <summary>数据库名称</summary>
    public override String Database => !String.IsNullOrEmpty(_database) ? _database : Setting.Database ?? String.Empty;

    /// <summary>数据源</summary>
    public override String DataSource => IsEmbedded ? Setting.DataSource ?? String.Empty : Setting.Server ?? String.Empty;

    /// <summary>连接超时</summary>
    public override Int32 ConnectionTimeout => Setting.ConnectionTimeout;

    /// <summary>服务器版本</summary>
    public override String ServerVersion => "1.0";

    /// <summary>连接状态</summary>
    public override ConnectionState State => _state;

    /// <summary>是否为嵌入模式</summary>
    public Boolean IsEmbedded => Setting.IsEmbedded;

    /// <summary>远程客户端（服务器模式）</summary>
    public NovaClient? Client => _client;

    /// <summary>SQL 执行引擎（嵌入模式）</summary>
    public SqlEngine? SqlEngine => _sqlEngine;

    /// <summary>客户端工厂</summary>
    public NovaClientFactory Factory { get; set; } = NovaClientFactory.Instance;

    /// <summary>提供者工厂</summary>
    protected override DbProviderFactory DbProviderFactory => Factory;
    #endregion

    #region 构造
    /// <summary>实例化</summary>
    public NovaConnection() { }

    /// <summary>使用连接字符串实例化</summary>
    /// <param name="connectionString">连接字符串</param>
    public NovaConnection(String connectionString) => ConnectionString = connectionString;
    #endregion

    #region 打开关闭
    /// <summary>打开连接</summary>
    public override void Open()
    {
        if (_state == ConnectionState.Open) return;

        _state = ConnectionState.Connecting;

        if (IsEmbedded)
        {
            var dataSource = Setting.DataSource;
            if (!dataSource.IsNullOrEmpty())
            {
                // 从连接字符串解析 WAL 模式，默认 Normal
                var walMode = WalMode.Normal;
                var walModeStr = Setting.WalMode;
                if (!walModeStr.IsNullOrEmpty() && Enum.TryParse<WalMode>(walModeStr, true, out var wm))
                    walMode = wm;

                var options = new DbOptions
                {
                    Path = dataSource,
                    WalMode = walMode,
                    ReadOnly = Setting.ReadOnly
                };
                _sqlEngine = new SqlEngine(dataSource, options);
            }
        }
        else
        {
            // 从连接池获取客户端
            var pool = Factory.PoolManager.GetPool(Setting);
            _client = pool.Get();
            _fromPool = true;
        }

        _state = ConnectionState.Open;
    }

    /// <summary>关闭连接</summary>
    public override void Close()
    {
        if (_state == ConnectionState.Closed) return;

        // 如果客户端来自连接池，归还而非关闭
        if (_fromPool && _client != null)
        {
            var pool = Factory.PoolManager.GetPool(Setting);
            pool.Return(_client);
            _client = null;
            _fromPool = false;
        }
        else
        {
            _client?.Close("Connection.Close");
            _client = null;
        }

        _sqlEngine?.Dispose();
        _sqlEngine = null;

        _state = ConnectionState.Closed;
    }
    #endregion

    #region 方法
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

    /// <summary>执行 SQL 语句</summary>
    /// <param name="sql">SQL 语句</param>
    /// <returns>受影响行数</returns>
    public Int32 ExecuteNonQuery(String sql)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
    #endregion

    #region 释放
    /// <summary>释放资源</summary>
    /// <param name="disposing">是否由 Dispose 调用</param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        // 如果客户端来自连接池，归还而非销毁
        if (_fromPool && _client != null)
        {
            var pool = Factory.PoolManager.GetPool(Setting);
            pool.Return(_client);
            _client = null;
            _fromPool = false;
        }
        else
        {
            _client?.Close(disposing ? "Dispose" : "GC");
            _client?.Dispose();
            _client = null;
        }

        _sqlEngine?.Dispose();
        _sqlEngine = null;
    }
    #endregion
}

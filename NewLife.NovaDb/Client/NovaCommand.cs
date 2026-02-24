using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Client;

#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member

/// <summary>NovaDb ADO.NET 命令</summary>
public class NovaCommand : DbCommand
{
    #region 属性
    private String _commandText = String.Empty;
    private readonly NovaParameterCollection _parameters = [];
    private CancellationTokenSource? _cts;

    /// <summary>SQL 命令文本</summary>
    public override String CommandText
    {
        get => _commandText;
        set => _commandText = value ?? String.Empty;
    }

    /// <summary>命令超时（秒）。0 表示不超时</summary>
    public override Int32 CommandTimeout { get; set; } = 30;

    /// <summary>命令类型</summary>
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <summary>是否在设计时可见</summary>
    public override Boolean DesignTimeVisible { get; set; }

    /// <summary>更新行来源</summary>
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <summary>关联连接</summary>
    protected override DbConnection? DbConnection { get; set; }

    /// <summary>参数集合</summary>
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>关联事务</summary>
    protected override DbTransaction? DbTransaction { get; set; }
    #endregion

    #region 方法

    /// <summary>取消正在执行的命令</summary>
    public override void Cancel() => _cts?.Cancel();

    /// <summary>执行非查询命令</summary>
    /// <returns>受影响行数</returns>
    public override Int32 ExecuteNonQuery()
    {
        if (DbConnection is not NovaConnection conn) return 0;

        // 嵌入模式：直接使用 SQL 引擎
        if (conn.SqlEngine != null)
        {
            var result = ExecuteWithTimeout(() => conn.SqlEngine.Execute(_commandText, BuildParameters()));
            return result.AffectedRows;
        }

        // 网络模式：通过 RPC 客户端执行
        if (conn.Client != null)
            return conn.Client.ExecuteAsync(_commandText).ConfigureAwait(false).GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>执行查询并返回第一行第一列</summary>
    /// <returns>标量值</returns>
    public override Object? ExecuteScalar()
    {
        if (DbConnection is not NovaConnection conn) return null;

        // 嵌入模式：直接使用 SQL 引擎
        if (conn.SqlEngine != null)
        {
            var result = ExecuteWithTimeout(() => conn.SqlEngine.Execute(_commandText, BuildParameters()));
            return result.GetScalar();
        }

        // 网络模式：通过查询获取标量值
        if (conn.Client != null)
        {
            var reader = ExecuteDbDataReader(CommandBehavior.Default);
            if (reader.Read() && reader.FieldCount > 0)
                return reader.GetValue(0);
        }

        return null;
    }

    /// <summary>预编译命令</summary>
    public override void Prepare() { }

    /// <summary>创建参数</summary>
    /// <returns>参数实例</returns>
    protected override DbParameter CreateDbParameter() => new NovaParameter();

    /// <summary>执行查询并返回数据读取器</summary>
    /// <param name="behavior">命令行为</param>
    /// <returns>数据读取器</returns>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var reader = new NovaDataReader();
        if (DbConnection is not NovaConnection conn) return reader;

        // 嵌入模式：直接使用 SQL 引擎
        if (conn.SqlEngine != null)
        {
            var result = ExecuteWithTimeout(() => conn.SqlEngine.Execute(_commandText, BuildParameters()));
            if (result.IsQuery && result.ColumnNames != null)
            {
                reader.SetColumns(result.ColumnNames);
                foreach (var row in result.Rows)
                {
                    reader.AddRow(row);
                }
            }
            return reader;
        }

        // 网络模式：通过 RPC 客户端查询
        if (conn.Client != null)
        {
            var queryResult = conn.Client.QueryAsync<IDictionary<String, Object?>>(_commandText)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            if (queryResult != null)
            {
                FillReaderFromQueryResult(reader, queryResult);
            }
        }

        return reader;
    }

    #endregion

    #region 辅助

    /// <summary>带超时和取消支持的执行包装</summary>
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="action">执行动作</param>
    /// <returns>执行结果</returns>
    private T ExecuteWithTimeout<T>(Func<T> action)
    {
        // CommandTimeout 为 0 表示不超时
        if (CommandTimeout <= 0) return action();

        _cts = new CancellationTokenSource();
        try
        {
            var task = Task.Run(action, _cts.Token);
            if (task.Wait(TimeSpan.FromSeconds(CommandTimeout)))
                return task.Result;

            _cts.Cancel();
            throw new NovaException(ErrorCode.Timeout, $"Command execution timed out after {CommandTimeout} seconds");
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            throw new NovaException(ErrorCode.Timeout, "Command execution was cancelled");
        }
        catch (OperationCanceledException)
        {
            throw new NovaException(ErrorCode.Timeout, "Command execution was cancelled");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>将参数集合转换为字典</summary>
    private Dictionary<String, Object?>? BuildParameters()
    {
        if (_parameters.Count == 0) return null;

        var dict = new Dictionary<String, Object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = (NovaParameter)_parameters[i];
            dict[p.ParameterName] = p.Value;
        }

        return dict;
    }

    /// <summary>从 RPC 查询结果填充数据读取器</summary>
    /// <param name="reader">数据读取器</param>
    /// <param name="queryResult">查询结果字典</param>
    private static void FillReaderFromQueryResult(NovaDataReader reader, IDictionary<String, Object?> queryResult)
    {
        // 解析列名（Remoting 反序列化后为 List<Object>）
        if (queryResult.TryGetValue("ColumnNames", out var colObj) || queryResult.TryGetValue("columnNames", out colObj))
        {
            if (colObj is IList<Object> colList)
            {
                var columns = colList.Select(c => c?.ToString() ?? String.Empty).ToArray();
                reader.SetColumns(columns);
            }
            else if (colObj is Object[] colArr)
            {
                var columns = colArr.Select(c => c?.ToString() ?? String.Empty).ToArray();
                reader.SetColumns(columns);
            }
            else if (colObj is String[] colStrArr)
            {
                reader.SetColumns(colStrArr);
            }
        }

        // 解析行数据（Remoting 反序列化后每行为 List<Object>）
        if (queryResult.TryGetValue("Rows", out var rowsObj) || queryResult.TryGetValue("rows", out rowsObj))
        {
            if (rowsObj is IList<Object> rows)
            {
                foreach (var row in rows)
                {
                    if (row is IList<Object> rowList)
                        reader.AddRow(rowList.ToArray());
                    else if (row is Object[] rowArr)
                        reader.AddRow(rowArr);
                }
            }
        }
    }

    #endregion
}

using System.Data;
using System.Data.Common;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 命令</summary>
public class NovaDbCommand : DbCommand
{
    private String _commandText = String.Empty;
    private readonly NovaDbParameterCollection _parameters = new();

    /// <summary>SQL 命令文本</summary>
    public override String CommandText
    {
        get => _commandText;
        set => _commandText = value ?? String.Empty;
    }

    /// <summary>命令超时（秒）</summary>
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

    /// <summary>取消命令</summary>
    public override void Cancel() { }

    /// <summary>执行非查询命令</summary>
    /// <returns>受影响行数</returns>
    public override Int32 ExecuteNonQuery()
    {
        var engine = GetSqlEngine();
        if (engine == null) return 0;

        var result = engine.Execute(_commandText, BuildParameters());
        return result.AffectedRows;
    }

    /// <summary>执行查询并返回第一行第一列</summary>
    /// <returns>标量值</returns>
    public override Object? ExecuteScalar()
    {
        var engine = GetSqlEngine();
        if (engine == null) return null;

        var result = engine.Execute(_commandText, BuildParameters());
        return result.GetScalar();
    }

    /// <summary>预编译命令</summary>
    public override void Prepare() { }

    /// <summary>创建参数</summary>
    /// <returns>参数实例</returns>
    protected override DbParameter CreateDbParameter() => new NovaDbParameter();

    /// <summary>执行查询并返回数据读取器</summary>
    /// <param name="behavior">命令行为</param>
    /// <returns>数据读取器</returns>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var reader = new NovaDbDataReader();
        var engine = GetSqlEngine();
        if (engine == null) return reader;

        var result = engine.Execute(_commandText, BuildParameters());
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

    #region 辅助

    /// <summary>获取 SQL 引擎实例</summary>
    private SqlEngine? GetSqlEngine()
    {
        if (DbConnection is NovaDbConnection conn)
            return conn.SqlEngine;

        return null;
    }

    /// <summary>将参数集合转换为字典</summary>
    private Dictionary<String, Object?>? BuildParameters()
    {
        if (_parameters.Count == 0) return null;

        var dict = new Dictionary<String, Object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = (NovaDbParameter)_parameters[i];
            dict[p.ParameterName] = p.Value;
        }

        return dict;
    }

    #endregion
}

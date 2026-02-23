using System.ComponentModel;
using System.Data;
using System.Data.Common;

namespace NewLife.NovaDb.Client;

#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member

/// <summary>行更新前事件参数</summary>
public sealed class NovaRowUpdatingEventArgs(DataRow row, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) : RowUpdatingEventArgs(row, command, statementType, tableMapping)
{
    /// <summary>获取或设置要执行的 NovaCommand</summary>
    public new NovaCommand Command { get => (NovaCommand)base.Command!; set => base.Command = value; }
}

/// <summary>行更新后事件参数</summary>
public sealed class NovaRowUpdatedEventArgs(DataRow row, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) : RowUpdatedEventArgs(row, command, statementType, tableMapping)
{
    /// <summary>获取执行的 NovaCommand</summary>
    public new NovaCommand Command => (NovaCommand)base.Command!;
}

/// <summary>行更新前事件处理程序</summary>
/// <param name="sender">事件源</param>
/// <param name="e">事件参数</param>
public delegate void NovaRowUpdatingEventHandler(Object sender, NovaRowUpdatingEventArgs e);

/// <summary>行更新后事件处理程序</summary>
/// <param name="sender">事件源</param>
/// <param name="e">事件参数</param>
public delegate void NovaRowUpdatedEventHandler(Object sender, NovaRowUpdatedEventArgs e);

/// <summary>NovaDb 数据适配器，用于在 DataSet 和数据库之间传输数据</summary>
[DesignerCategory("Code")]
public sealed class NovaDataAdapter : DbDataAdapter, IDbDataAdapter, IDataAdapter
{
    #region 属性
    /// <summary>行更新前事件</summary>
    public event NovaRowUpdatingEventHandler? RowUpdating;

    /// <summary>行更新后事件</summary>
    public event NovaRowUpdatedEventHandler? RowUpdated;
    #endregion

    #region 构造
    /// <summary>实例化</summary>
    public NovaDataAdapter() { }

    /// <summary>使用指定的查询命令实例化</summary>
    /// <param name="selectCommand">SELECT 命令</param>
    public NovaDataAdapter(NovaCommand selectCommand) => SelectCommand = selectCommand;

    /// <summary>使用查询文本和连接实例化</summary>
    /// <param name="selectCommandText">SELECT 命令文本</param>
    /// <param name="connection">数据库连接</param>
    public NovaDataAdapter(String selectCommandText, NovaConnection connection) => SelectCommand = new NovaCommand { CommandText = selectCommandText, Connection = connection };

    /// <summary>使用查询文本和连接字符串实例化</summary>
    /// <param name="selectCommandText">SELECT 命令文本</param>
    /// <param name="connectionString">数据库连接字符串</param>
    public NovaDataAdapter(String selectCommandText, String connectionString) => SelectCommand = new NovaCommand { CommandText = selectCommandText, Connection = new NovaConnection { ConnectionString = connectionString } };
    #endregion

    #region 方法
    /// <summary>创建行更新后事件参数</summary>
    /// <param name="dataRow">数据行</param>
    /// <param name="command">命令</param>
    /// <param name="statementType">语句类型</param>
    /// <param name="tableMapping">表映射</param>
    /// <returns>行更新后事件参数</returns>
    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) => new NovaRowUpdatedEventArgs(dataRow, command, statementType, tableMapping);

    /// <summary>创建行更新前事件参数</summary>
    /// <param name="dataRow">数据行</param>
    /// <param name="command">命令</param>
    /// <param name="statementType">语句类型</param>
    /// <param name="tableMapping">表映射</param>
    /// <returns>行更新前事件参数</returns>
    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) => new NovaRowUpdatingEventArgs(dataRow, command, statementType, tableMapping);

    /// <summary>触发行更新前事件</summary>
    /// <param name="value">事件参数</param>
    protected override void OnRowUpdating(RowUpdatingEventArgs value) => RowUpdating?.Invoke(this, (NovaRowUpdatingEventArgs)value);

    /// <summary>触发行更新后事件</summary>
    /// <param name="value">事件参数</param>
    protected override void OnRowUpdated(RowUpdatedEventArgs value) => RowUpdated?.Invoke(this, (NovaRowUpdatedEventArgs)value);
    #endregion
}

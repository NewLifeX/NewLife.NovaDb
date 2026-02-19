using System;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaDataAdapter 单元测试</summary>
public class NovaDataAdapterTests
{
    #region 构造函数

    [Fact(DisplayName = "DataAdapter-默认构造")]
    public void DefaultConstructor()
    {
        var adapter = new NovaDataAdapter();
        Assert.NotNull(adapter);
    }

    [Fact(DisplayName = "DataAdapter-使用命令构造")]
    public void ConstructorWithCommand()
    {
        var cmd = new NovaCommand { CommandText = "SELECT 1" };
        var adapter = new NovaDataAdapter(cmd);

        Assert.NotNull(adapter);
        Assert.Same(cmd, adapter.SelectCommand);
    }

    [Fact(DisplayName = "DataAdapter-使用SQL和连接构造")]
    public void ConstructorWithSqlAndConnection()
    {
        using var conn = new NovaConnection("Data Source=./test.db");
        var adapter = new NovaDataAdapter("SELECT 1", conn);

        Assert.NotNull(adapter);
        Assert.NotNull(adapter.SelectCommand);
        Assert.Equal("SELECT 1", adapter.SelectCommand!.CommandText);
    }

    [Fact(DisplayName = "DataAdapter-使用SQL和连接字符串构造")]
    public void ConstructorWithSqlAndConnStr()
    {
        var adapter = new NovaDataAdapter("SELECT 1", "Data Source=./test.db");

        Assert.NotNull(adapter);
        Assert.NotNull(adapter.SelectCommand);
        Assert.Equal("SELECT 1", adapter.SelectCommand!.CommandText);
    }

    #endregion

    #region 工厂创建

    [Fact(DisplayName = "工厂-CreateDataAdapter返回NovaDataAdapter")]
    public void Factory_CreateDataAdapter()
    {
        var adapter = NovaClientFactory.Instance.CreateDataAdapter();
        Assert.NotNull(adapter);
        Assert.IsType<NovaDataAdapter>(adapter);
    }

    #endregion

    #region 命令属性读写

    [Fact(DisplayName = "DataAdapter-SelectCommand读写")]
    public void SelectCommand_GetSet()
    {
        var adapter = new NovaDataAdapter();
        var cmd = new NovaCommand { CommandText = "SELECT * FROM t" };

        adapter.SelectCommand = cmd;
        Assert.Same(cmd, adapter.SelectCommand);
    }

    [Fact(DisplayName = "DataAdapter-InsertCommand读写")]
    public void InsertCommand_GetSet()
    {
        var adapter = new NovaDataAdapter();
        var cmd = new NovaCommand { CommandText = "INSERT INTO t VALUES (1)" };

        adapter.InsertCommand = cmd;
        Assert.Same(cmd, adapter.InsertCommand);
    }

    [Fact(DisplayName = "DataAdapter-UpdateCommand读写")]
    public void UpdateCommand_GetSet()
    {
        var adapter = new NovaDataAdapter();
        var cmd = new NovaCommand { CommandText = "UPDATE t SET x=1" };

        adapter.UpdateCommand = cmd;
        Assert.Same(cmd, adapter.UpdateCommand);
    }

    [Fact(DisplayName = "DataAdapter-DeleteCommand读写")]
    public void DeleteCommand_GetSet()
    {
        var adapter = new NovaDataAdapter();
        var cmd = new NovaCommand { CommandText = "DELETE FROM t" };

        adapter.DeleteCommand = cmd;
        Assert.Same(cmd, adapter.DeleteCommand);
    }

    #endregion
}

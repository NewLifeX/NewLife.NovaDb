#if NET6_0_OR_GREATER
using System;
using System.Data;
using System.IO;
using NewLife.NovaDb.Client;
using Xunit;

namespace XUnitTest.Client;

/// <summary>NovaBatch 和 NovaBatchCommand 单元测试</summary>
public class NovaBatchTests : IDisposable
{
    private readonly String _dbPath;

    public NovaBatchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"NovaBatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);
    }

    public void Dispose()
    {
        if (!String.IsNullOrEmpty(_dbPath) && Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); }
            catch { }
        }
    }

    /// <summary>创建并打开嵌入式连接</summary>
    private NovaConnection CreateConnection()
    {
        var conn = new NovaConnection { ConnectionString = $"Data Source={_dbPath}" };
        conn.Open();
        return conn;
    }

    #region NovaBatchCommand 属性

    [Fact(DisplayName = "BatchCommand-默认CommandText为空")]
    public void BatchCommand_DefaultCommandText()
    {
        var cmd = new NovaBatchCommand();
        Assert.Equal(String.Empty, cmd.CommandText);
    }

    [Fact(DisplayName = "BatchCommand-CommandText读写")]
    public void BatchCommand_CommandText_GetSet()
    {
        var cmd = new NovaBatchCommand { CommandText = "INSERT INTO t VALUES (1)" };
        Assert.Equal("INSERT INTO t VALUES (1)", cmd.CommandText);
    }

    [Fact(DisplayName = "BatchCommand-默认CommandType为Text")]
    public void BatchCommand_DefaultCommandType()
    {
        var cmd = new NovaBatchCommand();
        Assert.Equal(CommandType.Text, cmd.CommandType);
    }

    [Fact(DisplayName = "BatchCommand-RecordsAffected为0")]
    public void BatchCommand_RecordsAffected()
    {
        var cmd = new NovaBatchCommand();
        Assert.Equal(0, cmd.RecordsAffected);
    }

    #endregion

    #region NovaBatch 属性

    [Fact(DisplayName = "Batch-默认构造")]
    public void Batch_DefaultConstructor()
    {
        var batch = new NovaBatch();
        Assert.NotNull(batch);
        Assert.Equal(30, batch.Timeout);
    }

    [Fact(DisplayName = "Batch-使用连接构造")]
    public void Batch_ConstructorWithConnection()
    {
        using var conn = CreateConnection();
        var batch = new NovaBatch(conn);
        Assert.NotNull(batch);
    }

    [Fact(DisplayName = "Batch-Timeout读写")]
    public void Batch_Timeout_GetSet()
    {
        var batch = new NovaBatch { Timeout = 120 };
        Assert.Equal(120, batch.Timeout);
    }

    [Fact(DisplayName = "Batch-BatchCommands初始为空")]
    public void Batch_CommandsInitiallyEmpty()
    {
        var batch = new NovaBatch();
        Assert.Equal(0, batch.BatchCommands.Count);
    }

    #endregion

    #region NovaBatch 命令集合

    [Fact(DisplayName = "Batch-添加命令到集合")]
    public void Batch_AddCommands()
    {
        var batch = new NovaBatch();
        var cmd1 = new NovaBatchCommand { CommandText = "SELECT 1" };
        var cmd2 = new NovaBatchCommand { CommandText = "SELECT 2" };

        batch.BatchCommands.Add(cmd1);
        batch.BatchCommands.Add(cmd2);

        Assert.Equal(2, batch.BatchCommands.Count);
    }

    [Fact(DisplayName = "Batch-清空命令集合")]
    public void Batch_ClearCommands()
    {
        var batch = new NovaBatch();
        batch.BatchCommands.Add(new NovaBatchCommand { CommandText = "SELECT 1" });
        batch.BatchCommands.Add(new NovaBatchCommand { CommandText = "SELECT 2" });

        batch.BatchCommands.Clear();
        Assert.Equal(0, batch.BatchCommands.Count);
    }

    #endregion

    #region NovaBatch 执行

    [Fact(DisplayName = "Batch-无连接ExecuteNonQuery抛异常")]
    public void Batch_ExecuteNonQuery_NoConnection_Throws()
    {
        var batch = new NovaBatch();
        batch.BatchCommands.Add(new NovaBatchCommand { CommandText = "SELECT 1" });

        Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
    }

    [Fact(DisplayName = "Batch-无命令ExecuteNonQuery抛异常")]
    public void Batch_ExecuteNonQuery_NoCommands_Throws()
    {
        using var conn = CreateConnection();
        var batch = new NovaBatch(conn);

        Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
    }

    [Fact(DisplayName = "Batch-Prepare不抛异常")]
    public void Batch_Prepare_DoesNotThrow()
    {
        var batch = new NovaBatch();
        batch.Prepare();
    }

    [Fact(DisplayName = "Batch-Cancel不抛异常")]
    public void Batch_Cancel_DoesNotThrow()
    {
        var batch = new NovaBatch();
        batch.Cancel();
    }

    [Fact(DisplayName = "Batch-Dispose不抛异常")]
    public void Batch_Dispose_DoesNotThrow()
    {
        var batch = new NovaBatch();
        batch.Dispose();
    }

    #endregion

    #region 工厂创建

    [Fact(DisplayName = "工厂-CreateBatch返回NovaBatch")]
    public void Factory_CreateBatch()
    {
        var batch = NovaClientFactory.Instance.CreateBatch();
        Assert.NotNull(batch);
        Assert.IsType<NovaBatch>(batch);
    }

    [Fact(DisplayName = "工厂-CreateBatchCommand返回NovaBatchCommand")]
    public void Factory_CreateBatchCommand()
    {
        var cmd = NovaClientFactory.Instance.CreateBatchCommand();
        Assert.NotNull(cmd);
        Assert.IsType<NovaBatchCommand>(cmd);
    }

    [Fact(DisplayName = "工厂-CanCreateBatch为true")]
    public void Factory_CanCreateBatch()
    {
        Assert.True(NovaClientFactory.Instance.CanCreateBatch);
    }

    [Fact(DisplayName = "工厂-CreateBatch每次返回新实例")]
    public void Factory_CreateBatch_NewInstance()
    {
        var a = NovaClientFactory.Instance.CreateBatch();
        var b = NovaClientFactory.Instance.CreateBatch();
        Assert.NotSame(a, b);
    }

    #endregion
}
#endif

using System.Data.Common;
using NewLife.Log;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 客户端工厂</summary>
public sealed class NovaClientFactory : DbProviderFactory
{
    /// <summary>默认实例</summary>
    public static NovaClientFactory Instance = new();

    static NovaClientFactory()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
        DbProviderFactories.RegisterFactory("NewLife.NovaDb.Client", Instance);
#endif
    }

    #region 属性
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
    /// <summary>是否支持创建命令构建器。不支持</summary>
    public override Boolean CanCreateCommandBuilder => false;

    /// <summary>是否支持创建数据适配器</summary>
    public override Boolean CanCreateDataAdapter => true;
#endif

#if NET6_0_OR_GREATER
    /// <summary>是否支持创建批量命令</summary>
    public override Boolean CanCreateBatch => true;
#endif

    /// <summary>不支持创建数据源枚举</summary>
    public override Boolean CanCreateDataSourceEnumerator => false;

    /// <summary>性能跟踪器</summary>
    public ITracer? Tracer { get; set; }
    #endregion

    #region 方法
    /// <summary>创建命令</summary>
    /// <returns>命令实例</returns>
    public override DbCommand CreateCommand() => new NovaCommand();

    /// <summary>创建连接</summary>
    /// <returns>连接实例</returns>
    public override DbConnection CreateConnection() => new NovaConnection { Factory = this };

    /// <summary>创建参数</summary>
    /// <returns>参数实例</returns>
    public override DbParameter CreateParameter() => new NovaParameter();

    /// <summary>创建连接字符串构建器</summary>
    /// <returns>连接字符串构建器实例</returns>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new NovaConnectionStringBuilder();

    /// <summary>创建数据适配器</summary>
    /// <returns>数据适配器实例</returns>
    public override DbDataAdapter CreateDataAdapter() => new NovaDataAdapter();

#if NET6_0_OR_GREATER
    /// <summary>创建批量命令</summary>
    /// <returns>批量命令实例</returns>
    public override DbBatch CreateBatch() => new NovaBatch();

    /// <summary>创建批量命令项</summary>
    /// <returns>批量命令项实例</returns>
    public override DbBatchCommand CreateBatchCommand() => new NovaBatchCommand();
#endif
    #endregion
}

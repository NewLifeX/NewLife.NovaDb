using System.Text;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 管理工具</summary>
public static class NovaDbTool
{
    /// <summary>检查数据库目录完整性</summary>
    /// <param name="dbPath">数据库路径</param>
    /// <returns>目录是否存在且可访问</returns>
    public static Boolean CheckIntegrity(String dbPath)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        if (!Directory.Exists(dbPath)) return false;

        try
        {
            // 验证目录可读
            Directory.GetFiles(dbPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取数据库状态摘要</summary>
    /// <param name="engine">SQL 引擎实例</param>
    /// <returns>状态摘要文本</returns>
    public static String GetStatus(SqlEngine engine)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));

        var metrics = engine.Metrics;
        var sb = new StringBuilder();
        sb.AppendLine($"NovaDb v{GetVersion()}");
        sb.AppendLine($"Path: {engine.DbPath}");
        sb.AppendLine($"Tables: {engine.TableNames.Count}");
        sb.AppendLine($"Uptime: {(Int32)metrics.Uptime.TotalHours:D2}:{metrics.Uptime.Minutes:D2}:{metrics.Uptime.Seconds:D2}");
        sb.AppendLine($"Executes: {metrics.ExecuteCount}");
        sb.AppendLine($"Queries: {metrics.QueryCount}");
        sb.AppendLine($"Inserts: {metrics.InsertCount}");
        sb.AppendLine($"Updates: {metrics.UpdateCount}");
        sb.AppendLine($"Deletes: {metrics.DeleteCount}");

        return sb.ToString();
    }

    /// <summary>获取数据库版本</summary>
    /// <returns>版本号字符串</returns>
    public static String GetVersion()
    {
        var asm = typeof(SqlEngine).Assembly;
        var ver = asm.GetName().Version;
        return ver?.ToString() ?? "1.0.0";
    }
}

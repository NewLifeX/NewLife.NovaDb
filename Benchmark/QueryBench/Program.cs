using System.Text;
using NewLife.NovaDb.Client;
using NewLife.QueryBench;

internal static class Program
{
    private static async Task<Int32> Main(String[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 重定向输出时可能不支持设置编码
        }

        if (!BenchOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"参数错误：{error}");
            BenchOptions.PrintUsage();
            return 1;
        }

        if (options.Help)
        {
            BenchOptions.PrintUsage();
            return 0;
        }

        try
        {
            if (!options.ReportOnly)
            {
                var runner = new BenchRunner(options);
                await runner.RunAsync();
            }

            if (options.Report || options.ReportOnly) ReportWriter.Write(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"基准执行失败：{BenchLog.Summarize(ex)}");
            if (ex.InnerException != null) Console.Error.WriteLine($"内层错误：{BenchLog.Summarize(ex.InnerException)}");
            return 2;
        }
        finally
        {
            try
            {
                NovaConnection.DisposeSharedEngines();
            }
            catch
            {
                // 退出阶段忽略引擎释放异常
            }
        }

        return 0;
    }
}

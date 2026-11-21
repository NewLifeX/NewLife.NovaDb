namespace NewLife.QueryBench;

/// <summary>控制台日志</summary>
public static class BenchLog
{
    /// <summary>输出一行时间戳日志</summary>
    /// <param name="message"></param>
    public static void Write(String message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>异常摘要，最多 120 字符</summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static String Summarize(Exception exception)
    {
        var text = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 120 ? text : text[..120];
    }

    /// <summary>是否为超时异常</summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static Boolean IsTimeout(Exception exception)
    {
        if (exception is OperationCanceledException or TaskCanceledException) return true;
        if (exception is NewLife.NovaDb.Core.NovaException nova && nova.Code == NewLife.NovaDb.Core.ErrorCode.Timeout) return true;

        // 部分驱动以命令超时异常或内部异常形式抛出
        var message = exception.Message;
        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }
}

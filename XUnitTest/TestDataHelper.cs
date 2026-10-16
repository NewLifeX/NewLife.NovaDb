using System;
using System.IO;

namespace XUnitTest;

/// <summary>测试数据目录助手。目录位于测试输出目录下的 TestData/，并按目标框架隔离，
/// 避免 net8.0/net10.0 多框架并行执行时争抢同一批数据库文件导致文件锁失败</summary>
internal static class TestDataHelper
{
    /// <summary>获取指定测试子目录的完整路径</summary>
    /// <param name="name">子目录名，如 RelationalDb、KvDb、QueueDb、FluxDb</param>
    /// <returns>数据目录完整路径（位于测试输出目录 TestData/net{主版本}/{name}）</returns>
    public static String GetPath(String name) => Path.Combine(AppContext.BaseDirectory, "TestData", $"net{Environment.Version.Major}", name);
}

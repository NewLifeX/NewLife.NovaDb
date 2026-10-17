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

    /// <summary>是否跳过性能类断言。仿真环境（如 LA64 QEMU 用户态仿真）指令吞吐不具备参考意义</summary>
    /// <remarks>由 CI 的 linux-la64（QEMU）作业设置环境变量 NOVADB_SKIP_PERF=1 启用；真机作业不受影响</remarks>
    public static Boolean PerfTestsDisabled => Environment.GetEnvironmentVariable("NOVADB_SKIP_PERF") == "1";
}

using BenchmarkDotNet.Running;

namespace Benchmark;

class Program
{
    static void Main(String[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

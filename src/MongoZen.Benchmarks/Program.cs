using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;

namespace MongoZen.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

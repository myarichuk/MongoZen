using BenchmarkDotNet.Running;

namespace MongoZen.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<ComparisonBenchmarks>(args: args);
    }
}

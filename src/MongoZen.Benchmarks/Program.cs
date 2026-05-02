using BenchmarkDotNet.Running;
using MongoZen.Benchmarks;

namespace MongoZen.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<BsonBenchmarks>();
        // BenchmarkRunner.Run<ChangeTrackingBenchmarks>();
    }
}

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MongoZen.Collections;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class PooledDictionaryBenchmarks
{
    private PooledDictionary<int, string> _dict = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dict = new PooledDictionary<int, string>(1000);
        for (int i = 0; i < 1000; i++)
        {
            _dict.AddOrUpdate(i, $"Value-{i}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dict.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int IterateValues()
    {
        int count = 0;
        foreach (var val in _dict.Values)
        {
            if (val != null) count++;
        }
        return count;
    }
}

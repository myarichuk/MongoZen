using System.Reflection;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class ReflectionBenchmarks
{
    private static readonly MethodInfo EnumerableAnyMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Static | BindingFlags.Public)
        .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2);

    [Benchmark(Baseline = true)]
    public MethodInfo UncachedReflection()
    {
        return typeof(Enumerable)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
    }

    [Benchmark]
    public MethodInfo CachedMethodReflection()
    {
        return EnumerableAnyMethod.MakeGenericMethod(typeof(int));
    }
}

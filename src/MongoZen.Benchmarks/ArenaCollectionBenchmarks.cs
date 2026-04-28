using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MongoZen.Collections;
using SharpArena.Allocators;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ArenaCollectionBenchmarks
{
    private const int ItemCount = 1000;
    private readonly ArenaAllocator _arena = new();
    private int[] _data = null!;
    private string[] _stringData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(0, ItemCount).ToArray();
        _stringData = Enumerable.Range(0, ItemCount).Select(i => $"Key-{i}").ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => _arena.Dispose();

    [IterationCleanup]
    public void IterationCleanup() => _arena.Reset();

    // -------------------------------------------------------------------------
    // HashSet Benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("HashSet"), Benchmark(Baseline = true, Description = "Managed: HashSet<int>")]
    public int ManagedHashSet_Int()
    {
        var set = new HashSet<int>();
        foreach (var i in _data) set.Add(i);
        int count = 0;
        foreach (var i in _data) if (set.Contains(i)) count++;
        return count;
    }

    [BenchmarkCategory("HashSet"), Benchmark(Description = "Zen: ArenaHashSet<int> (Zero-GC)")]
    public int ArenaHashSet_Int()
    {
        var set = new ArenaHashSet<int>(_arena, ItemCount);
        foreach (var i in _data) set.Add(i);
        int count = 0;
        foreach (var i in _data) if (set.Contains(i)) count++;
        return count;
    }

    // -------------------------------------------------------------------------
    // Dictionary Benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("Dictionary"), Benchmark(Baseline = true, Description = "Managed: Dictionary<int, int>")]
    public int ManagedDictionary_Int()
    {
        var dict = new Dictionary<int, int>();
        foreach (var i in _data) dict[i] = i;
        int sum = 0;
        foreach (var i in _data) if (dict.TryGetValue(i, out var val)) sum += val;
        return sum;
    }

    [BenchmarkCategory("Dictionary"), Benchmark(Description = "Zen: ArenaDictionary<int, int> (Zero-GC)")]
    public int ArenaDictionary_Int()
    {
        var dict = new ArenaDictionary<int, int>(_arena, ItemCount);
        foreach (var i in _data) dict.AddOrUpdate(i, i);
        int sum = 0;
        foreach (var i in _data) if (dict.TryGetValue(i, out var val)) sum += val;
        return sum;
    }

    // -------------------------------------------------------------------------
    // ArenaString Benchmarks
    // -------------------------------------------------------------------------

    [BenchmarkCategory("ArenaString"), Benchmark(Description = "Zen: ArenaDictionary<ArenaString, int>")]
    public int ArenaDictionary_ArenaString()
    {
        var dict = new ArenaDictionary<ArenaString, int>(_arena, ItemCount);
        var arenaStrings = new ArenaString[_stringData.Length];
        for (int i = 0; i < _stringData.Length; i++)
        {
            arenaStrings[i] = ArenaString.Clone(_stringData[i], _arena);
            dict.AddOrUpdate(arenaStrings[i], i);
        }

        int sum = 0;
        for (int i = 0; i < arenaStrings.Length; i++)
        {
            if (dict.TryGetValue(arenaStrings[i], out var val)) sum += val;
        }
        return sum;
    }
}

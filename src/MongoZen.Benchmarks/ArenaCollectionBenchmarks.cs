using BenchmarkDotNet.Attributes;
using MongoZen.Collections;
using SharpArena.Allocators;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
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

    [Benchmark]
    public int ManagedHashSet_Int()
    {
        var set = new HashSet<int>();
        foreach (var i in _data) set.Add(i);
        int count = 0;
        foreach (var i in _data) if (set.Contains(i)) count++;
        return count;
    }

    [Benchmark]
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

    [Benchmark]
    public int ManagedDictionary_Int()
    {
        var dict = new Dictionary<int, int>();
        foreach (var i in _data) dict[i] = i;
        int sum = 0;
        foreach (var i in _data) if (dict.TryGetValue(i, out var val)) sum += val;
        return sum;
    }

    [Benchmark]
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

    [Benchmark]
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

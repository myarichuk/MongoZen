using BenchmarkDotNet.Attributes;
using MongoZen.Collections;
using SharpArena.Allocators;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class ArenaCollectionBenchmarks
{
    private const int Size = 1000;
    private readonly int[] _data = new int[Size];
    private readonly DocId[] _docIds = new DocId[Size];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        for (int i = 0; i < Size; i++)
        {
            _data[i] = random.Next();
            _docIds[i] = DocId.From(Guid.NewGuid());
        }
    }

    [Benchmark(Baseline = true)]
    public int HashSet_Standard()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < Size; i++)
        {
            set.Add(_data[i]);
        }
        int count = 0;
        for (int i = 0; i < Size; i++)
        {
            if (set.Contains(_data[i])) count++;
        }
        return count;
    }

    [Benchmark]
    public int HashSet_Arena()
    {
        using var arena = new ArenaAllocator();
        var set = new ArenaHashSet<int>(arena, Size);
        for (int i = 0; i < Size; i++)
        {
            set.Add(_data[i]);
        }
        int count = 0;
        for (int i = 0; i < Size; i++)
        {
            if (set.Contains(_data[i])) count++;
        }
        return count;
    }

    [Benchmark]
    public int Dictionary_Standard()
    {
        var dict = new Dictionary<int, int>();
        for (int i = 0; i < Size; i++)
        {
            dict[_data[i]] = i;
        }
        int sum = 0;
        for (int i = 0; i < Size; i++)
        {
            if (dict.TryGetValue(_data[i], out var val)) sum += val;
        }
        return sum;
    }

    [Benchmark]
    public int Dictionary_Arena()
    {
        using var arena = new ArenaAllocator();
        var dict = new ArenaDictionary<int, int>(arena, Size);
        for (int i = 0; i < Size; i++)
        {
            dict.AddOrUpdate(_data[i], i);
        }
        int sum = 0;
        for (int i = 0; i < Size; i++)
        {
            if (dict.TryGetValue(_data[i], out var val)) sum += val;
        }
        return sum;
    }

    [Benchmark]
    public int DocId_HashSet_Standard()
    {
        var set = new HashSet<DocId>();
        for (int i = 0; i < Size; i++)
        {
            set.Add(_docIds[i]);
        }
        int count = 0;
        for (int i = 0; i < Size; i++)
        {
            if (set.Contains(_docIds[i])) count++;
        }
        return count;
    }

    [Benchmark]
    public int DocId_HashSet_Arena()
    {
        using var arena = new ArenaAllocator();
        var set = new ArenaHashSet<DocId>(arena, Size);
        for (int i = 0; i < Size; i++)
        {
            set.Add(_docIds[i]);
        }
        int count = 0;
        for (int i = 0; i < Size; i++)
        {
            if (set.Contains(_docIds[i])) count++;
        }
        return count;
    }
}

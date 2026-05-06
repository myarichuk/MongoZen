using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoZen.Bson;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class BsonBenchmarks
{
    private byte[] _rawBson = null!;
    private LargePoco _poco = null!;
    private ArenaAllocator _arena = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arena = new ArenaAllocator(10 * 1024 * 1024);
        _poco = new LargePoco
        {
            Id = ObjectId.GenerateNewId(),
            Name = "Root Object",
            Timestamp = DateTime.UtcNow,
            Tags = ["high-perf", "no-nonsense", "arena", "mongodb"],
            Items = []
        };

        for (int i = 0; i < 50; i++)
        {
            _poco.Items.Add(new ItemPoco
            {
                Name = $"Item {i}",
                Value = i * 1.5,
                IsActive = i % 2 == 0
            });
        }

        _rawBson = _poco.ToBson();
    }

    [GlobalCleanup]
    public void Cleanup() => _arena.Dispose();

    [Benchmark(Baseline = true, Description = "Driver (Raw BsonDocument)")]
    [BenchmarkCategory("Deserialization")]
    public BsonDocument Driver_Read() => 
        BsonSerializer.Deserialize<BsonDocument>(_rawBson);

    [Benchmark(Description = "MongoZen (BlittableBsonDocument)")]
    [BenchmarkCategory("Deserialization")]
    public BlittableBsonDocument MongoZen_Read()
    {
        _arena.Reset();
        return ArenaBsonReader.Read(_rawBson, _arena);
    }

    [Benchmark(Description = "Driver (POCO)")]
    [BenchmarkCategory("Deserialization")]
    public LargePoco Driver_Deserialize_Typed() => 
        BsonSerializer.Deserialize<LargePoco>(_rawBson);

    [Benchmark(Description = "MongoZen (Dynamic Blittable POCO)")]
    [BenchmarkCategory("Deserialization")]
    public LargePoco MongoZen_Deserialize_Dynamic_Typed()
    {
        _arena.Reset();
        var doc = ArenaBsonReader.Read(_rawBson, _arena);
        return DynamicBlittableSerializer<LargePoco>.DeserializeDelegate(doc, _arena);
    }

    [Benchmark(Baseline = true, Description = "Driver (POCO)")]
    [BenchmarkCategory("Serialization")]
    public byte[] Driver_Serialize_Typed()
    {
        return _poco.ToBson();
    }

    [Benchmark(Description = "MongoZen (Dynamic Blittable POCO)")]
    [BenchmarkCategory("Serialization")]
    public BlittableBsonDocument MongoZen_Serialize_Dynamic_Typed()
    {
        _arena.Reset();
        var writer = new ArenaBsonWriter(_arena);
        DynamicBlittableSerializer<LargePoco>.SerializeDelegate(ref writer, _poco);
        return writer.Commit(_arena);
    }

    public class LargePoco
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public List<string> Tags { get; set; } = new();
        public List<ItemPoco> Items { get; set; } = new();
    }

    public class ItemPoco
    {
        public string Name { get; set; } = "";
        public double Value { get; set; }
        public bool IsActive { get; set; }
    }
}

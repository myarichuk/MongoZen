using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using SharpArena.Allocators;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoZen.Bson;

namespace MongoZen.Benchmarks;

[MemoryDiagnoser]
public class BsonBenchmarks
{
    private byte[] _rawBson = null!;
    private LargePoco _poco = null!;
    private ArenaAllocator _arena = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arena = new ArenaAllocator();
        _poco = new LargePoco
        {
            Id = ObjectId.GenerateNewId(),
            Name = "Root Object",
            Timestamp = DateTime.UtcNow,
            Tags = new List<string> { "high-perf", "no-nonsense", "arena", "mongodb" },
            Items = new List<ItemPoco>()
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

    [Benchmark(Baseline = true)]
    public BsonDocument Driver_Read()
    {
        return BsonSerializer.Deserialize<BsonDocument>(_rawBson);
    }

    [Benchmark]
    public BlittableBsonDocument MongoZen_Read()
    {
        _arena.Reset();
        return ArenaBsonReader.Read(_rawBson, _arena);
    }

    [Benchmark]
    public LargePoco Driver_Deserialize()
    {
        return BsonSerializer.Deserialize<LargePoco>(_rawBson);
    }

    [Benchmark]
    public LargePoco MongoZen_Deserialize_Dynamic()
    {
        _arena.Reset();
        var doc = ArenaBsonReader.Read(_rawBson, _arena);
        return DynamicBlittableSerializer<LargePoco>.DeserializeDelegate(doc, _arena);
    }

    [Benchmark]
    public byte[] Driver_Serialize()
    {
        return _poco.ToBson();
    }

    [Benchmark]
    public BlittableBsonDocument MongoZen_Serialize_Dynamic()
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

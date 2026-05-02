using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;

namespace MongoZen.Benchmarks;

[Document]
public class SimpleEntity
{
    public int Id { get; set; }
    public long Value { get; set; }
    public Guid Guid { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Address
{
    public string City { get; set; } = string.Empty;
}

public class Customer
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

[Document]
public class ComplexEntity
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, int> Metadata { get; set; } = new();
}

[MemoryDiagnoser]
public class ChangeTrackingBenchmarks
{
    private ArenaAllocator _allocator = null!;
    private readonly UpdateDefinitionBuilder<BsonDocument> _builder = Builders<BsonDocument>.Update;

    private SimpleEntity _simpleEntity = null!;
    private SimpleEntityShadow _simpleShadow;

    private ComplexEntity _complexEntity = null!;
    private ComplexEntityShadow _complexShadow;

    [GlobalSetup]
    public void Setup()
    {
        _allocator = new ArenaAllocator(10 * 1024 * 1024); // 10MB to be safe
        
        _simpleEntity = new SimpleEntity { Id = 1, Value = 100, Guid = Guid.NewGuid(), Name = "Simple" };
        _simpleShadow = SimpleEntityShadow.Create(_simpleEntity, _allocator);

        _complexEntity = new ComplexEntity
        {
            Id = 1,
            Customer = new Customer { Name = "Customer", Address = new Address { City = "City" } },
            Tags = ["tag1", "tag2", "tag3"],
            Metadata = new Dictionary<string, int> { { "k1", 1 }, { "k2", 2 } }
        };
        _complexShadow = ComplexEntityShadow.Create(_complexEntity, _allocator);
    }

    [GlobalCleanup]
    public void Teardown()
    {
        _allocator.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Reset the allocator once per iteration to prevent long-term growth
        // though we also reset in the benchmarks themselves.
        _allocator.Reset();
    }

    [Benchmark]
    public void Simple_CreateShadow()
    {
        _allocator.Reset();
        _ = SimpleEntityShadow.Create(_simpleEntity, _allocator);
    }

    [Benchmark]
    public void Simple_BuildUpdate_Clean()
    {
        _ = _simpleShadow.BuildUpdate(_simpleEntity, _builder);
    }

    [Benchmark]
    public void Simple_BuildUpdate_Modified()
    {
        _simpleEntity.Value++;
        _ = _simpleShadow.BuildUpdate(_simpleEntity, _builder);
        _simpleEntity.Value--; 
    }

    [Benchmark]
    public void Complex_CreateShadow()
    {
        _allocator.Reset();
        _ = ComplexEntityShadow.Create(_complexEntity, _allocator);
    }

    [Benchmark]
    public void Complex_BuildUpdate_Clean()
    {
        _ = _complexShadow.BuildUpdate(_complexEntity, _builder);
    }

    [Benchmark]
    public void Complex_BuildUpdate_Modified()
    {
        _complexEntity.Customer.Address.City = "Modified";
        _ = _complexShadow.BuildUpdate(_complexEntity, _builder);
        _complexEntity.Customer.Address.City = "City"; 
    }
}

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<ChangeTrackingBenchmarks>();
    }
}

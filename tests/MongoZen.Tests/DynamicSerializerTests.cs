using System;
using Xunit;
using SharpArena.Allocators;
using MongoZen.Bson;
using MongoDB.Bson;

namespace MongoZen.Tests;

public class DynamicSerializerTests
{
    public class SimplePoco
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public bool IsActive { get; set; }
    }

    public class NestedPoco
    {
        public string Id { get; set; } = "";
        public SimplePoco Child { get; set; } = new();
    }

    [Fact]
    public void Can_Serialize_Simple_POCO_Dynamically()
    {
        using var arena = new ArenaAllocator();
        var writer = new ArenaBsonWriter(arena);
        var poco = new SimplePoco { Name = "Oren", Age = 42, IsActive = true };

        // This will trigger Expression Tree compilation
        DynamicBlittableSerializer<SimplePoco>.SerializeDelegate(ref writer, poco);

        var doc = writer.Commit(arena);

        Assert.Equal("Oren", doc.GetString("Name"));
        Assert.Equal(42, doc.GetInt32("Age"));
        Assert.True(doc.GetBoolean("IsActive"));
    }

    [Fact]
    public void Can_Deserialize_Simple_POCO_Dynamically()
    {
        using var arena = new ArenaAllocator();
        var bsonDoc = new BsonDocument
        {
            { "Name", "Ayende" },
            { "Age", 38 },
            { "IsActive", false }
        };
        var raw = bsonDoc.ToBson();

        var doc = ArenaBsonReader.Read(raw, arena);
        
        // This will trigger Expression Tree compilation for deserialization
        var poco = DynamicBlittableSerializer<SimplePoco>.DeserializeDelegate(doc, arena);

        Assert.Equal("Ayende", poco.Name);
        Assert.Equal(38, poco.Age);
        Assert.False(poco.IsActive);
    }

    [Fact]
    public void Can_Roundtrip_Nested_POCO_Dynamically()
    {
        using var arena = new ArenaAllocator();
        var poco = new NestedPoco 
        { 
            Id = "parent-1", 
            Child = new SimplePoco { Name = "child-1", Age = 5 } 
        };

        // Serialize
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<NestedPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        // Verify BSON structure
        Assert.Equal("parent-1", doc.GetString("Id"));
        var nestedDoc = doc.GetDocument("Child", arena);
        Assert.Equal("child-1", nestedDoc.GetString("Name"));

        // Deserialize
        var result = DynamicBlittableSerializer<NestedPoco>.DeserializeDelegate(doc, arena);

        Assert.Equal("parent-1", result.Id);
        Assert.NotNull(result.Child);
        Assert.Equal("child-1", result.Child.Name);
        Assert.Equal(5, result.Child.Age);
    }

    public class CollectionPoco
    {
        public string Title { get; set; } = "";
        public List<int> Scores { get; set; } = new();
        public SimplePoco[] Children { get; set; } = Array.Empty<SimplePoco>();
    }

    public class DictionaryPoco
    {
        public string Name { get; set; } = "";
        public Dictionary<string, int> Metadata { get; set; } = new();
        public Dictionary<string, SimplePoco> Children { get; set; } = new();
    }

    [Fact]
    public void Can_Roundtrip_Dictionaries_Dynamically()
    {
        using var arena = new ArenaAllocator();
        var poco = new DictionaryPoco
        {
            Name = "Root",
            Metadata = new Dictionary<string, int> { { "key1", 100 }, { "key2", 200 } },
            Children = new Dictionary<string, SimplePoco>
            {
                { "child1", new SimplePoco { Name = "C1", Age = 1 } }
            }
        };

        // Serialize
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<DictionaryPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        // Deserialize
        var result = DynamicBlittableSerializer<DictionaryPoco>.DeserializeDelegate(doc, arena);

        Assert.Equal("Root", result.Name);
        Assert.Equal(2, result.Metadata.Count);
        Assert.Equal(100, result.Metadata["key1"]);
        Assert.Single(result.Children);
        Assert.Equal("C1", result.Children["child1"].Name);
    }
}

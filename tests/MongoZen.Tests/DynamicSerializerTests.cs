using System;
using Xunit;
using SharpArena.Allocators;
using MongoZen.Bson;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace MongoZen.Tests;

public unsafe class DynamicSerializerTests
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

    public class BridgePoco
    {
        [MongoDB.Bson.Serialization.Attributes.BsonRepresentation(BsonType.String)]
        public int IntAsString { get; set; }
    }

    [Fact]
    public void Can_Roundtrip_Via_Driver_Bridge()
    {
        BsonClassMap.LookupClassMap(typeof(BridgePoco)); // Ensure attributes are scanned
        using var arena = new ArenaAllocator();
        var poco = new BridgePoco { IntAsString = 123 };

        var writer = new ArenaBsonWriter(arena);
        writer.WriteStartDocument();
        writer.WriteName("Data", BlittableBsonConstants.BsonType.Document);
        
        // This should hit the BsonSerializerBridge since it's a complex type
        BlittableConverter<BridgePoco>.Instance.Write(ref writer, poco);
        writer.WriteEndDocument();
        
        var doc = writer.Commit(arena);
        var nested = doc.GetDocument("Data", arena);

        // Verify that it was stored as a string (per BsonRepresentation attribute)
        Assert.Equal("123", nested.GetString("IntAsString"));

        // Deserialize back
        var result = BlittableConverter<BridgePoco>.Instance.Read(nested.Pointer, BlittableBsonConstants.BsonType.Document, nested.Length);
        Assert.Equal(123, result.IntAsString);
    }

    public class RichPoco
    {
        public ObjectId Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Data { get; set; } = "";
    }

    [Fact]
    public void Can_Roundtrip_Rich_Poco_Dynamically()
    {
        using var arena = new ArenaAllocator();
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), now.Kind);

        var poco = new RichPoco { Id = id, Timestamp = now, Data = "Some data" };

        // Serialize
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<RichPoco>.SerializeDelegate(ref writer, poco);
        var doc = writer.Commit(arena);

        // Deserialize
        var result = DynamicBlittableSerializer<RichPoco>.DeserializeDelegate(doc, arena);

        Assert.Equal(id, result.Id);
        Assert.Equal(now, result.Timestamp);
        Assert.Equal("Some data", result.Data);
    }

    [Fact]
    public void Deserializer_Skips_Extra_Fields_Gracefully()
    {
        using var arena = new ArenaAllocator();
        // BSON has "Extra" which is not in SimplePoco
        var bsonDoc = new BsonDocument
        {
            { "Name", "ExtraField" },
            { "Age", 30 },
            { "Extra", "I should be ignored" },
            { "IsActive", true }
        }
        .ToBson();

        var doc = ArenaBsonReader.Read(bsonDoc, arena);
        var poco = DynamicBlittableSerializer<SimplePoco>.DeserializeDelegate(doc, arena);

        Assert.Equal("ExtraField", poco.Name);
        Assert.Equal(30, poco.Age);
        Assert.True(poco.IsActive);
    }
}

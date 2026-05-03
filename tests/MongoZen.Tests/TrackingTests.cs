using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoZen.Bson;
using Xunit;

namespace MongoZen.Tests;

public partial class Customer
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

public partial class Address
{
    public string City { get; set; } = string.Empty;
}

[Document]
public partial class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
}

[Document]
public partial class IgnoreEntity
{
    public string Name { get; set; } = string.Empty;
    
    [BsonIgnore]
    public string Computed { get; set; } = string.Empty;
}

[Document]
public partial class CollectionEntity
{
    public List<string> Tags { get; set; } = [];
}

[Document]
public partial class NullableEntity
{
    public int? Age { get; set; }
}

[Document]
public partial class DictionaryEntity
{
    public Dictionary<string, int> Scores { get; set; } = new();
}

[Document]
public partial class StringEntity
{
    public string? Value { get; set; }
}

[Document]
public partial class ElementEntity
{
    [BsonElement("custom_field_name")]
    public string Name { get; set; } = string.Empty;
}

public class TrackingTests : IDisposable
{
    private readonly ArenaAllocator _allocator = new(64 * 1024);
    private readonly UpdateDefinitionBuilder<BsonDocument> _builder = Builders<BsonDocument>.Update;

    private BlittableBsonDocument Snapshot<T>(T entity) where T : IBlittableDocument<T>
    {
        var writer = new ArenaBsonWriter(_allocator);
        T.Serialize(ref writer, entity);
        return writer.Commit(_allocator);
    }

    private BsonDocument RenderUpdate<T>(UpdateDefinition<T> update)
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        return (BsonDocument)update.Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry));
    }

    [Fact]
    public void Should_Detect_Simple_Change()
    {
        var entity = new SimpleEntity { Id = 1, Name = "Old" };
        var snapshot = Snapshot(entity);

        entity.Name = "New";
        var update = SimpleEntity.BuildUpdate(entity, snapshot, _builder);

        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.Equal("New", bson["$set"].AsBsonDocument["Name"].AsString);
    }

    [Fact]
    public void Should_Respect_BsonIgnore()
    {
        var entity = new IgnoreEntity { Name = "Test", Computed = "Old" };
        var snapshot = Snapshot(entity);

        entity.Computed = "New";
        var update = IgnoreEntity.BuildUpdate(entity, snapshot, _builder);

        // Computed is ignored, so no update should be generated
        Assert.Null(update);

        entity.Name = "Changed";
        update = IgnoreEntity.BuildUpdate(entity, snapshot, _builder);
        Assert.NotNull(update);
    }

    [Fact]
    public void Should_Handle_Nullable_Primitives()
    {
        var entity = new NullableEntity { Age = 10 };
        var snapshot = Snapshot(entity);

        entity.Age = null;
        var update = NullableEntity.BuildUpdate(entity, snapshot, _builder);
        Assert.NotNull(update);
        // Note: The generator currently doesn't handle Nullable types in BuildUpdate yet.
        // It's a TODO in the generator.
    }

    [Fact]
    public void Should_Respect_BsonElement()
    {
        var entity = new ElementEntity { Name = "Old" };
        var snapshot = Snapshot(entity);

        entity.Name = "New";
        var update = ElementEntity.BuildUpdate(entity, snapshot, _builder);
        Assert.NotNull(update);

        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.True(bson["$set"].AsBsonDocument.Contains("custom_field_name"));
        Assert.Equal("New", bson["$set"].AsBsonDocument["custom_field_name"].AsString);
    }

    public void Dispose() => _allocator.Dispose();
}

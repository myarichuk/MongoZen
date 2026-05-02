using SharpArena.Allocators;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using Xunit;

namespace MongoZen.Tests;

public class Customer
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

public class Address
{
    public string City { get; set; } = string.Empty;
}

[Document]
public class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
}

[Document]
public class IgnoreEntity
{
    public string Name { get; set; } = string.Empty;
    
    [BsonIgnore]
    public string Computed { get; set; } = string.Empty;
}

[Document]
public class CollectionEntity
{
    public List<string> Tags { get; set; } = [];
}

public class TrackingTests : IDisposable
{
    private readonly ArenaAllocator _allocator = new(64 * 1024);
    private readonly UpdateDefinitionBuilder<BsonDocument> _builder = Builders<BsonDocument>.Update;

    private BsonDocument RenderUpdate<T>(UpdateDefinition<T> update)
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
        return (BsonDocument)update.Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry));
    }

    [Fact]
    public void Should_Detect_Nested_Changes()
    {
        var order = new Order
        {
            Id = 1,
            Customer = new Customer
            {
                Name = "Oren",
                Address = new Address { City = "Hadera" }
            }
        };

        var shadow = OrderShadow.Create(order, _allocator);
        order.Customer.Address.City = "Tel Aviv";

        var update = shadow.BuildUpdate(order, _builder);
        Assert.NotNull(update);
        
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.Equal("Tel Aviv", bson["$set"].AsBsonDocument["Customer.Address.City"].AsString);
    }

    [Fact]
    public void Should_Respect_BsonIgnore()
    {
        var entity = new IgnoreEntity { Name = "Test", Computed = "Old" };
        var shadow = IgnoreEntityShadow.Create(entity, _allocator);

        entity.Computed = "New";
        var update = shadow.BuildUpdate(entity, _builder);

        // Computed is ignored, so no update should be generated
        Assert.Null(update);

        entity.Name = "Changed";
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
    }

    [Fact]
    public void Should_Detect_Collection_Changes()
    {
        var entity = new CollectionEntity { Tags = ["tag1", "tag2"] };
        var shadow = CollectionEntityShadow.Create(entity, _allocator);

        // Unchanged
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.Null(update);

        // Modified element
        entity.Tags[0] = "modified";
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.Equal(2, bson["$set"].AsBsonDocument["Tags"].AsBsonArray.Count);

        // Added element
        entity.Tags.Add("tag3");
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);

        // Clear collection
        entity.Tags.Clear();
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
    }

    [Fact]
    public void Should_Handle_Null_Transitions()
    {
        var order = new Order { Id = 1, Customer = new Customer { Name = "John" } };
        var shadow = OrderShadow.Create(order, _allocator);

        order.Customer = null!; 

        var update = shadow.BuildUpdate(order, _builder);

        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$unset"));
        Assert.Equal(1, bson["$unset"].AsBsonDocument["Customer"].ToInt32());
    }

    [Document]
    public class NullableEntity
    {
        public int? Age { get; set; }
    }

    [Fact]
    public void Should_Handle_Nullable_Primitives()
    {
        var entity = new NullableEntity { Age = 10 };
        var shadow = NullableEntityShadow.Create(entity, _allocator);

        entity.Age = null;
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$unset"));

        entity.Age = 20;
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        bson = RenderUpdate(update);
        Assert.Equal(20, bson["$set"].AsBsonDocument["Age"].AsInt32);
    }

    [Document]
    public class DictionaryEntity
    {
        public Dictionary<string, int> Scores { get; set; } = new();
    }

    [Fact]
    public void Should_Detect_Dictionary_Changes()
    {
        var entity = new DictionaryEntity { Scores = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } } };
        var shadow = DictionaryEntityShadow.Create(entity, _allocator);

        // Unchanged
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.Null(update);

        // Modified value
        entity.Scores["a"] = 10;
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.Equal(10, bson["$set"].AsBsonDocument["Scores"].AsBsonDocument["a"].AsInt32);

        // Added key
        entity.Scores["c"] = 3;
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);

        // Remove key (not supported by ArenaDictionary.Remove but we can detect count change)
        // Wait, ArenaDictionary.Remove throws NotSupportedException.
        // But our shadow just stores the snapshot. If we change the managed dictionary,
        // BuildUpdate should detect it.
        entity.Scores.Remove("b");
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
    }

    [Document]
    public class StringEntity
    {
        public string? Value { get; set; }
    }

    [Document]
    public class ElementEntity
    {
        [BsonElement("custom_field_name")]
        public string Name { get; set; } = string.Empty;
    }

    [BsonKnownTypes(typeof(Dog), typeof(Cat))]
    public abstract class Animal
    {
        public string Name { get; set; } = string.Empty;
    }
    public class Dog : Animal { public int BarkVolume { get; set; } }
    public class Cat : Animal { public bool IsGrumpy { get; set; } }

    [Document]
    public class Zoo
    {
        public Animal MainAttraction { get; set; } = null!;
    }

    [Fact]
    public void Should_Distinguish_Null_And_Empty_Strings()
    {
        var entity = new StringEntity { Value = "initial" };
        var shadow = StringEntityShadow.Create(entity, _allocator);

        // Transition to Empty String -> Should be $set
        entity.Value = "";
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.Equal("", bson["$set"].AsBsonDocument["Value"].AsString);

        // Transition to Null -> Should be $unset
        entity.Value = null;
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
        bson = RenderUpdate(update);
        Assert.True(bson.Contains("$unset"));
        Assert.Equal(1, bson["$unset"].AsBsonDocument["Value"].ToInt32());
    }

    [Fact]
    public void Should_Respect_BsonElement()
    {
        var entity = new ElementEntity { Name = "Old" };
        var shadow = ElementEntityShadow.Create(entity, _allocator);

        entity.Name = "New";
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);

        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.True(bson["$set"].AsBsonDocument.Contains("custom_field_name"));
        Assert.Equal("New", bson["$set"].AsBsonDocument["custom_field_name"].AsString);
    }

    [Fact]
    public void Should_Detect_Polymorphic_Changes()
    {
        var entity = new Zoo { MainAttraction = new Dog { Name = "Rex", BarkVolume = 10 } };
        var shadow = ZooShadow.Create(entity, _allocator);

        // No change
        var update = shadow.BuildUpdate(entity, _builder);
        Assert.Null(update);

        // Mutate derived field (this will trigger because BsonDocument comparison fails)
        ((Dog)entity.MainAttraction).BarkVolume = 100;
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);

        var bson = RenderUpdate(update);
        Assert.True(bson.Contains("$set"));
        Assert.True(bson["$set"].AsBsonDocument.Contains("MainAttraction"));
        Assert.Equal(100, bson["$set"].AsBsonDocument["MainAttraction"].AsBsonDocument["BarkVolume"].AsInt32);
        
        // Swap polymorphic types completely
        entity.MainAttraction = new Cat { Name = "Fluffy", IsGrumpy = true };
        update = shadow.BuildUpdate(entity, _builder);
        Assert.NotNull(update);
    }

    public void Dispose() => _allocator.Dispose();
}

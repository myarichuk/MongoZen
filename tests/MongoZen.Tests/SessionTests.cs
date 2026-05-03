using SharpArena.Allocators;
using MongoZen;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoZen.Bson;
using Xunit;

namespace MongoZen.Tests;

public class SessionTests : IntegrationTestBase
{
    [Fact]
    public void EntityIdAccessor_Should_Get_Id()
    {
        var entity = new SimpleEntity { Id = 42 };
        var id = EntityIdAccessor.GetId(entity);
        Assert.Equal(42, id);
    }

    [Fact]
    public void BuildUpdate_Should_Detect_Changes_Correct()
    {
        using var arena = new ArenaAllocator();
        var original = new SimpleEntity { Id = 1, Name = "Original", Age = 20 };
        
        // Create snapshot
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<SimpleEntity>.SerializeDelegate(ref writer, original);
        var snapshot = writer.Commit(arena);

        // Mutate
        var modified = new SimpleEntity { Id = 1, Name = "Changed", Age = 20 };

        // Diff
        var builder = Builders<BsonDocument>.Update;
        var update = DynamicBlittableSerializer<SimpleEntity>.BuildUpdateDelegate(modified, snapshot, builder, arena);

        Assert.NotNull(update);
        var bson = (BsonDocument)update.Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
        Assert.True(bson.Contains("$set"));
        Assert.Equal("Changed", bson["$set"].AsBsonDocument["Name"].AsString);
    }

    [Fact]
    public async Task Session_Should_Track_Changes_And_Save()
    {
        var db = Database;
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);
        
        var entity = new SimpleEntity { Id = 1, Name = "Original", Age = 20 };
        await collection.InsertOneAsync(entity);

        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        using var session = new DocumentSession(store);
        
        // Load
        var loaded = await session.LoadAsync<SimpleEntity>(1);
        Assert.NotNull(loaded);
        Assert.Equal("Original", loaded.Name);

        // Mutate
        loaded.Name = "Changed";
        loaded.Age = 30;

        // Save
        await session.SaveChangesAsync();

        // Verify
        var updated = await collection.Find(Builders<SimpleEntity>.Filter.Eq("_id", 1)).FirstOrDefaultAsync();
        Assert.Equal("Changed", updated.Name);
        Assert.Equal(30, updated.Age);
    }

    [Fact]
    public async Task Session_Should_Use_IdentityMap()
    {
        var entity = new SimpleEntity { Id = 2, Name = "Identity", Age = 25 };
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(SimpleEntity));
        await Database.GetCollection<SimpleEntity>(collectionName).InsertOneAsync(entity);

        var store = new DocumentStore(Database.Client, Database.DatabaseNamespace.DatabaseName);
        using var session = new DocumentSession(store);
        
        var load1 = await session.LoadAsync<SimpleEntity>(2);
        var load2 = await session.LoadAsync<SimpleEntity>(2);

        Assert.Same(load1, load2);
    }

    [Fact]
    public async Task DocumentStore_Should_Create_Functional_Session()
    {
        var db = Database;
        var client = db.Client;
        var store = new DocumentStore(client, db.DatabaseNamespace.DatabaseName);

        using var session = store.OpenSession();
        var entity = new SimpleEntity { Id = 3, Name = "StoreTest", Age = 40 };
        session.Store(entity);
        await session.SaveChangesAsync();

        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(SimpleEntity));
        var saved = await db.GetCollection<SimpleEntity>(collectionName)
            .Find(Builders<SimpleEntity>.Filter.Eq("_id", 3))
            .FirstOrDefaultAsync();

        Assert.NotNull(saved);
        Assert.Equal("StoreTest", saved.Name);
    }

    [Fact]
    public void BuildUpdate_Should_Detect_Changes_On_All_Types()
    {
        using var arena = new ArenaAllocator();
        var oid = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), now.Kind);

        var original = new ComprehensiveEntity
        {
            Id = 1,
            BigInt = 100L,
            Precision = 1.23,
            Flag = true,
            Oid = oid,
            Timestamp = now,
            Text = "Original",
            Child = new SimpleEntity { Name = "ChildOriginal", Age = 10 },
            Numbers = new List<int> { 1, 2, 3 }
        };

        // Create snapshot
        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<ComprehensiveEntity>.SerializeDelegate(ref writer, original);
        var snapshot = writer.Commit(arena);

        // Mutate everything
        var newOid = ObjectId.GenerateNewId();
        var later = now.AddHours(1);
        var modified = new ComprehensiveEntity
        {
            Id = 1,
            BigInt = 200L,
            Precision = 4.56,
            Flag = false,
            Oid = newOid,
            Timestamp = later,
            Text = "Changed",
            Child = new SimpleEntity { Name = "ChildChanged", Age = 10 }, // Only Name changed
            Numbers = new List<int> { 4, 5, 6 }
        };

        // Diff
        var builder = Builders<BsonDocument>.Update;
        var update = DynamicBlittableSerializer<ComprehensiveEntity>.BuildUpdateDelegate(modified, snapshot, builder, arena);

        Assert.NotNull(update);
        var bson = (BsonDocument)update.Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
        var set = bson["$set"].AsBsonDocument;

        Assert.Equal(200L, set["BigInt"].AsInt64);
        Assert.Equal(4.56, set["Precision"].AsDouble);
        Assert.False(set["Flag"].AsBoolean);
        Assert.Equal(newOid, set["Oid"].AsObjectId);
        Assert.Equal(later, set["Timestamp"].ToUniversalTime());
        Assert.Equal("Changed", set["Text"].AsString);
        Assert.Equal("ChildChanged", set["Child.Name"].AsString);
        Assert.False(set.Contains("Child.Age")); // Unchanged nested property
        Assert.Equal(new BsonArray { 4, 5, 6 }, set["Numbers"]);
    }
}

[Document]
public partial class SimpleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class ComprehensiveEntity
{
    public int Id { get; set; }
    public long BigInt { get; set; }
    public double Precision { get; set; }
    public bool Flag { get; set; }
    public ObjectId Oid { get; set; }
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
    public SimpleEntity Child { get; set; } = new();
    public List<int> Numbers { get; set; } = new();
}


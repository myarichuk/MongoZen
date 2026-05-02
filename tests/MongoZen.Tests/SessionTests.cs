using SharpArena.Allocators;
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
        var update = DynamicBlittableSerializer<SimpleEntity>.BuildUpdateDelegate(modified, snapshot, builder);

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
}

[Document]
public class SimpleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

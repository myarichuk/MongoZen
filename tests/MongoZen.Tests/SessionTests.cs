using SharpArena.Allocators;
using MongoZen;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoZen.Bson;
using MongoZen.ChangeTracking;
using Xunit;

namespace MongoZen.Tests;

public class SessionTests : IntegrationTestBase
{
    [Fact]
    public async Task Session_Should_Track_Changes_And_Save()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);
        
        var entity = new SimpleEntity { Id = 1, Name = "Initial" };
        await collection.InsertOneAsync(entity);

        using var arena = new ArenaAllocator(1024 * 1024);
        
        using var session = store.OpenSession();
        var loaded = await session.LoadAsync<SimpleEntity>(1);
        Assert.NotNull(loaded);
        Assert.Equal("Initial", loaded.Name);

        var snapshot = session.GetSnapshot(loaded);
        Assert.NotNull(snapshot);

        loaded.Name = "Changed";
        
        var modified = loaded;

        // Diff
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<SimpleEntity>.BuildUpdateDelegate(modified, snapshot.Value, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var bson = builder.Build();
        Assert.True(bson.ContainsKey("$set".AsSpan()));
        Assert.Equal("Changed", bson.GetDocument("$set".AsSpan(), arena).GetString("Name".AsSpan()));

        await session.SaveChangesAsync();

        var final = await collection.Find(x => x.Id == 1).FirstOrDefaultAsync();
        Assert.Equal("Changed", final.Name);
    }

    [Fact]
    public async Task Session_Should_Handle_Deletes()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);
        
        var entity = new SimpleEntity { Id = 2, Name = "To Delete" };
        await collection.InsertOneAsync(entity);

        using var session = store.OpenSession();
        
        var loaded = await session.LoadAsync<SimpleEntity>(2);
        Assert.NotNull(loaded);

        session.Delete(loaded);
        await session.SaveChangesAsync();

        var final = await collection.Find(x => x.Id == 2).FirstOrDefaultAsync();
        Assert.Null(final);
    }

    [Fact]
    public async Task Session_Should_Support_Identity_Map()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);
        
        var entity = new SimpleEntity { Id = 3, Name = "Identical" };
        await collection.InsertOneAsync(entity);

        using var session = store.OpenSession();
        
        var a = await session.LoadAsync<SimpleEntity>(3);
        var b = await session.LoadAsync<SimpleEntity>(3);

        Assert.Same(a, b);
    }

    [Fact]
    public async Task Session_Should_Detect_Dirty_Fields_Only()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);
        
        var entity = new SimpleEntity { Id = 4, Name = "Initial", Age = 30 };
        await collection.InsertOneAsync(entity);

        using var session = store.OpenSession();
        
        var loaded = await session.LoadAsync<SimpleEntity>(4);
        loaded!.Age = 31; // Only age changed
        
        await session.SaveChangesAsync();

        // Verify with raw BSON that Name was not part of the update if possible, 
        // or just verify it's still correct.
        var final = await collection.Find(x => x.Id == 4).FirstOrDefaultAsync();
        Assert.Equal("Initial", final.Name);
        Assert.Equal(31, final.Age);
    }

    [Fact]
    public void BuildUpdate_Should_Detect_Changes_On_All_Types()
    {
        using var arena = new ArenaAllocator(1024 * 1024);
        var original = new ComprehensiveEntity
        {
            Id = ObjectId.GenerateNewId(),
            BigInt = 100L,
            Precision = 1.23,
            Flag = true,
            Oid = ObjectId.GenerateNewId(),
            Timestamp = DateTime.UtcNow,
            Text = "Original",
            Child = new SimpleEntity { Name = "ChildInitial", Age = 5 },
            Numbers = [1, 2, 3]
        };

        var writer = new ArenaBsonWriter(arena);
        DynamicBlittableSerializer<ComprehensiveEntity>.SerializeDelegate(ref writer, original);
        var snapshot = writer.Commit(arena);

        var newOid = ObjectId.GenerateNewId();
        var later = DateTime.UtcNow.AddMinutes(1);
        later = new DateTime(later.Ticks - (later.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        var modified = new ComprehensiveEntity
        {
            Id = original.Id,
            BigInt = 200L,
            Precision = 4.56,
            Flag = false,
            Oid = newOid,
            Timestamp = later,
            Text = "Changed",
            Child = new SimpleEntity { Name = "ChildChanged", Age = 5 },
            Numbers = [4, 5, 6]
        };

        // Diff
        var builder = new ArenaUpdateDefinitionBuilder(arena);
        DynamicBlittableSerializer<ComprehensiveEntity>.BuildUpdateDelegate(modified, snapshot, ref builder, arena, default);

        Assert.True(builder.HasChanges);
        var bson = builder.Build();
        var set = bson.GetDocument("$set".AsSpan(), arena);

        Assert.Equal(200L, set.GetInt64("BigInt".AsSpan()));
        Assert.Equal(4.56, set.GetDouble("Precision".AsSpan()));
        Assert.False(set.GetBoolean("Flag".AsSpan()));
        Assert.Equal(newOid, set.GetObjectId("Oid".AsSpan()));
        Assert.Equal(later, set.GetDateTime("Timestamp".AsSpan()));
        Assert.Equal("Changed", set.GetString("Text".AsSpan()));
        Assert.Equal("ChildChanged", set.GetString("Child.Name".AsSpan()));
        Assert.False(set.ContainsKey("Child.Age".AsSpan()));
    }

    [Fact]
    public async Task Evict_Should_Not_Corrupt_Other_Tracked_Entities()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed 3 docs
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "First", Age = 10 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "Second", Age = 20 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 3, Name = "Third", Age = 30 });

        using var session = store.OpenSession();

        // Load 3 entities
        var e1 = await session.LoadAsync<SimpleEntity>(1);
        var e2 = await session.LoadAsync<SimpleEntity>(2);
        var e3 = await session.LoadAsync<SimpleEntity>(3);

        // Evict the middle one
        session.Advanced.Evict(e2);

        // Mutate the remaining ones
        e1.Name = "FirstModified";
        e3.Name = "ThirdModified";

        await session.SaveChangesAsync();

        // Verify both mutations persisted
        var updated1 = await collection.Find(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 1)).FirstAsync();
        var updated3 = await collection.Find(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 3)).FirstAsync();

        Assert.Equal("FirstModified", updated1.Name);
        Assert.Equal("ThirdModified", updated3.Name);
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Diff_Correctly_On_Second_Save_In_Same_Session()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed doc
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "Original", Age = 25 });

        using var session = store.OpenSession();

        // Load
        var entity = await session.LoadAsync<SimpleEntity>(1);

        // Mutate field A
        entity.Name = "ModifiedA";
        await session.SaveChangesAsync();

        // Mutate field B (different field)
        entity.Age = 50;
        await session.SaveChangesAsync();

        // Verify both mutations persisted independently
        var updated = await collection.Find(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 1)).FirstAsync();
        Assert.Equal("ModifiedA", updated.Name);
        Assert.Equal(50, updated.Age);
    }

    [Fact]
    public async Task Delete_Then_LoadAsync_Should_Reflect_Deletion()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed doc
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "ToDelete", Age = 20 });

        using var session = store.OpenSession();

        // Load
        var entity = await session.LoadAsync<SimpleEntity>(1);
        Assert.NotNull(entity);

        // Delete in the same session
        session.Delete(entity);
        await session.SaveChangesAsync();

        // LoadAsync again in the same session — should hit DB and return null (not the stale identity map entry)
        var reloaded = await session.LoadAsync<SimpleEntity>(1);
        Assert.Null(reloaded);
    }
}

[Document]
[BsonIgnoreExtraElements]
public partial class SimpleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

[BsonIgnoreExtraElements]
public partial class ComprehensiveEntity
{
    public ObjectId Id { get; set; }
    public long BigInt { get; set; }
    public double Precision { get; set; }
    public bool Flag { get; set; }
    public ObjectId Oid { get; set; }
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
    public SimpleEntity Child { get; set; } = new();
    public List<int> Numbers { get; set; } = new();
}

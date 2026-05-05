using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Bson;
using Xunit;

namespace MongoZen.Tests;

public class EdgeCaseTests : IntegrationTestBase
{
    public class UserWithNullables
    {
        public ObjectId Id { get; set; }
        public int? Age { get; set; }
        public Guid? ExternalId { get; set; }
        public Nested? Profile { get; set; }
        public List<string?>? Tags { get; set; }
    }

    public class Nested
    {
        public string? Bio { get; set; }
        public int Score { get; set; }
    }

    [Fact]
    public async Task Nullable_Primitives_Are_Serialized_Correctly()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Age = 25,
            ExternalId = Guid.NewGuid()
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        Assert.Equal(25, doc["Age"].AsInt32);
        Assert.Equal(user.ExternalId, doc["ExternalId"].AsGuid);
        Assert.False(doc.Contains("Profile"));

        // Load and verify
        using var session2 = store.OpenSession();
        var user2 = await session2.LoadAsync<UserWithNullables>(user.Id);
        Assert.Equal(25, user2!.Age);
        Assert.Equal(user.ExternalId, user2.ExternalId);
        Assert.Null(user2.Profile);
    }

    [Fact]
    public async Task Nullable_Null_Value_Is_Serialized_Correctly()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Age = null,
            ExternalId = null
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        Assert.Equal(BsonNull.Value, doc["Age"]);
        Assert.Equal(BsonNull.Value, doc["ExternalId"]);

        // Load and verify
        using var session2 = store.OpenSession();
        var user2 = await session2.LoadAsync<UserWithNullables>(user.Id);
        Assert.Null(user2!.Age);
        Assert.Null(user2.ExternalId);
    }

    [Fact]
    public async Task Nested_Document_Diff_Handles_New_Object()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Age = 30
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Update with new nested object
        user.Profile = new Nested { Bio = "Hello", Score = 100 };
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        Assert.True(doc.Contains("Profile"));
        Assert.Equal("Hello", doc["Profile"]["Bio"].AsString);
        Assert.Equal(100, doc["Profile"]["Score"].AsInt32);
    }

    [Fact]
    public async Task Nested_Document_Diff_Handles_Null_To_Value_Update()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Profile = new Nested { Bio = "Original", Score = 50 }
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Change nested property
        user.Profile.Bio = "Updated";
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        Assert.Equal("Updated", doc["Profile"]["Bio"].AsString);
    }

    [Fact]
    public async Task Nested_Document_Diff_Handles_Unset()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Profile = new Nested { Bio = "Gone soon", Score = 50 }
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Set to null (should trigger $unset or $set: null depending on implementation, but result should be null)
        user.Profile = null;
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        // My implementation uses $unset for documents
        Assert.False(doc.Contains("Profile"));
    }

    [Fact]
    public async Task Collection_With_Null_Elements_Works()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new UserWithNullables
        {
            Id = ObjectId.GenerateNewId(),
            Tags = new List<string?> { "tag1", null, "tag2" }
        };

        session.Store(user);
        await session.SaveChangesAsync();

        // Verify in DB
        var collectionName = DocumentTypeTracker.GetDefaultCollectionName(typeof(UserWithNullables));
        var collection = Database.GetCollection<BsonDocument>(collectionName);
        var doc = await collection.Find(new BsonDocument("_id", user.Id)).FirstOrDefaultAsync();

        Assert.NotNull(doc);
        var tags = doc["Tags"].AsBsonArray;
        Assert.Equal(3, tags.Count);
        Assert.Equal("tag1", tags[0].AsString);
        Assert.Equal(BsonNull.Value, tags[1]);
        Assert.Equal("tag2", tags[2].AsString);

        // Load and verify
        using var session2 = store.OpenSession();
        var user2 = await session2.LoadAsync<UserWithNullables>(user.Id);
        Assert.NotNull(user2!.Tags);
        Assert.Equal(3, user2.Tags.Count);
        Assert.Equal("tag1", user2.Tags[0]);
        Assert.Null(user2.Tags[1]);
        Assert.Equal("tag2", user2.Tags[2]);
    }

    [Fact]
    public void ChangeTracker_Skips_Delete_For_Unsaved_New_Entities()
    {
        var arena = new SharpArena.Allocators.ArenaAllocator(1024);
        var tracker = new MongoZen.ChangeTracking.ChangeTracker(arena);

        var user = new UserWithNullables { Id = ObjectId.GenerateNewId() };
        
        tracker.Track(user); // Track as new
        tracker.TrackDelete(user); // Then delete

        var updates = tracker.GetGroupedUpdates();
        Assert.Empty(updates); // Should be empty because it was never persisted
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

[Document]
public partial class AdvancedUser
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = null!;
    public int Age { get; set; }
    
    [ConcurrencyCheck]
    public Guid ETag { get; set; }
}

public class AdvancedSessionTests : IntegrationTestBase
{
    [Fact]
    public async Task Can_Evict_Entity()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new AdvancedUser { Name = "John", Age = 30 };
        session.Store(user);
        
        var loadedUser = await session.LoadAsync<AdvancedUser>(user.Id);
        Assert.Same(user, loadedUser);

        session.Advanced.Evict(user);

        var loadedUserAfterEvict = await session.LoadAsync<AdvancedUser>(user.Id);
        // It should reload from DB if it was persisted, but here it wasn't.
        Assert.Null(loadedUserAfterEvict);
    }

    [Fact]
    public async Task Can_Refresh_Entity()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        store.Features.SupportsTransactions = false; // Disable transactions for this test
        
        var userId = ObjectId.GenerateNewId();
        var initialEtag = Guid.NewGuid();
        await Database.GetCollection<BsonDocument>("AdvancedUsers").InsertOneAsync(new BsonDocument
        {
            { "_id", userId },
            { "Name", "John" },
            { "Age", 30 },
            { "_etag", new BsonBinaryData(initialEtag, GuidRepresentation.Standard) }
        });

        using var session = store.OpenSession();
        var user = await session.LoadAsync<AdvancedUser>(userId);
        Assert.NotNull(user);
        Assert.Equal("John", user.Name);
        Assert.Equal(initialEtag, user.ETag);

        // Modify in DB
        var newEtag = Guid.NewGuid();
        await Database.GetCollection<BsonDocument>("AdvancedUsers").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Combine(
                Builders<BsonDocument>.Update.Set("Name", "Jane"),
                Builders<BsonDocument>.Update.Set("_etag", new BsonBinaryData(newEtag, GuidRepresentation.Standard))
            )
        );

        await session.Advanced.RefreshAsync(user);
        Assert.Equal("Jane", user.Name);
        Assert.Equal(newEtag, user.ETag);
        
        // Ensure change tracking is reset (no changes detected)
        var snapshot = session.GetSnapshot(user);
        Assert.NotNull(snapshot);
        Assert.Equal("Jane", snapshot.Value.GetString("Name"));
        Assert.Equal(newEtag, snapshot.Value.GetGuid("_etag"));
        
        Assert.Equal(newEtag, session.Advanced.GetETagFor(user));
    }

    [Fact]
    public async Task Can_Get_And_Set_ETag_Manually()
    {
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        var user = new AdvancedUser { Id = ObjectId.GenerateNewId(), Name = "John", Age = 30 };
        var etag = Guid.NewGuid();
        session.Advanced.Store(user, etag);

        Assert.Equal(etag, session.Advanced.GetETagFor(user));
        
        // Ensure it's in identity map
        var loaded = await session.LoadAsync<AdvancedUser>(user.Id);
        Assert.Same(user, loaded);
    }
}

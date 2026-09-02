using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Tests;
using Xunit;

namespace MongoZen.Tests;

public class ConcurrencyExecutionTests : IntegrationTestBase
{
    [Fact]
    public async Task SaveChangesAsync_Should_Throw_ConcurrencyException_On_Update_Conflict()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        
        // 1. Initial setup
        using (var session1 = store.OpenSession())
        {
            var entity = new ConcurrencyEntity { Id = 1, Name = "Original" };
            session1.Store(entity);
            await session1.SaveChangesAsync();
        }

        // 2. Load and modify in two different sessions
        using var sessionA = store.OpenSession();
        using var sessionB = store.OpenSession();

        var entityA = await sessionA.LoadAsync<ConcurrencyEntity>(1);
        var entityB = await sessionB.LoadAsync<ConcurrencyEntity>(1);

        entityA!.Name = "Modified by A";
        entityB!.Name = "Modified by B";

        // 3. Save session A - should succeed
        await sessionA.SaveChangesAsync();

        // 4. Save session B - should fail with ConcurrencyException
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => sessionB.SaveChangesAsync());
        Assert.Same(entityB, ex.Entity);
        Assert.Contains("was modified by another user", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Throw_ConcurrencyException_On_Delete_Conflict()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        
        // 1. Initial setup
        using (var session1 = store.OpenSession())
        {
            var entity = new ConcurrencyEntity { Id = 2, Name = "Original" };
            session1.Store(entity);
            await session1.SaveChangesAsync();
        }

        // 2. Load in session A and delete in session B
        using var sessionA = store.OpenSession();
        using var sessionB = store.OpenSession();

        var entityA = await sessionA.LoadAsync<ConcurrencyEntity>(2);
        var entityB = await sessionB.LoadAsync<ConcurrencyEntity>(2);

        // Session B deletes it
        sessionB.Delete(entityB!);
        await sessionB.SaveChangesAsync();

        // Session A tries to update it
        entityA!.Name = "Modified by A";
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => sessionA.SaveChangesAsync());
        Assert.Same(entityA, ex.Entity);
        Assert.Contains("was deleted by another user", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Throw_ConcurrencyException_When_Deleting_Already_Modified_Document()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        
        // 1. Initial setup
        using (var session1 = store.OpenSession())
        {
            var entity = new ConcurrencyEntity { Id = 3, Name = "Original" };
            session1.Store(entity);
            await session1.SaveChangesAsync();
        }

        // 2. Load in session A and modify in session B
        using var sessionA = store.OpenSession();
        using var sessionB = store.OpenSession();

        var entityA = await sessionA.LoadAsync<ConcurrencyEntity>(3);
        var entityB = await sessionB.LoadAsync<ConcurrencyEntity>(3);

        // Session B modifies it (changes ETag)
        entityB!.Name = "Modified by B";
        await sessionB.SaveChangesAsync();

        // Session A tries to delete it with old ETag
        sessionA.Delete(entityA!);
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => sessionA.SaveChangesAsync());
        Assert.Same(entityA, ex.Entity);
        Assert.Contains("was modified by another user", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Allow_Retry_After_ConcurrencyException()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        using (var session = store.OpenSession())
        {
            session.Store(new ConcurrencyEntity { Id = 4, Name = "Original" });
            await session.SaveChangesAsync();
        }

        using var sessionA = store.OpenSession();
        using var sessionB = store.OpenSession();

        var entityA = await sessionA.LoadAsync<ConcurrencyEntity>(4);
        var entityB = await sessionB.LoadAsync<ConcurrencyEntity>(4);

        entityA!.Name = "Modified by A";
        entityB!.Name = "Modified by B";

        await sessionA.SaveChangesAsync();

        await Assert.ThrowsAsync<ConcurrencyException>(() => sessionB.SaveChangesAsync());

        await sessionB.Advanced.RefreshAsync(entityB);
        entityB.Name = "Modified by B";
        await sessionB.SaveChangesAsync();

        using var verifySession = store.OpenSession();
        var final = await verifySession.LoadAsync<ConcurrencyEntity>(4);
        Assert.NotNull(final);
        Assert.Equal("Modified by B", final.Name);
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Never_Stamp_ETag_On_Entity_Without_ConcurrencyCheck()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        var collection = Database.GetCollection<BsonDocument>(store.Conventions.GetCollectionName(typeof(HiddenConcurrencyEntity)));

        using (var session = store.OpenSession())
        {
            session.Store(new HiddenConcurrencyEntity { Id = 100, Name = "Original" });
            await session.SaveChangesAsync();
        }

        var afterInsert = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 100)).FirstAsync();
        Assert.False(afterInsert.Contains("_etag"));

        using (var session = store.OpenSession())
        {
            var entity = await session.LoadAsync<HiddenConcurrencyEntity>(100);
            Assert.NotNull(entity);
            entity!.Name = "Updated once";
            await session.SaveChangesAsync();
        }

        var afterFirstUpdate = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 100)).FirstAsync();
        Assert.False(afterFirstUpdate.Contains("_etag"));

        using (var session = store.OpenSession())
        {
            var entity = await session.LoadAsync<HiddenConcurrencyEntity>(100);
            Assert.NotNull(entity);
            entity!.Name = "Updated twice";
            await session.SaveChangesAsync();
        }

        var afterSecondUpdate = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 100)).FirstAsync();
        Assert.False(afterSecondUpdate.Contains("_etag"));
    }
}

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
}

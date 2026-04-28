using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.Collections;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class DocIdDeduplicationTests : IntegrationTestBase
{
    private class SimpleEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Can_Deduplicate_String_Ids()
    {
        await TestDeduplication<SimpleEntity>("users/1");
    }

    [Fact]
    public async Task Can_Deduplicate_ObjectId_Ids()
    {
        await TestDeduplication<SimpleEntity>(ObjectId.GenerateNewId().ToString());
    }

    #region Generic Test Helper
    private async Task TestDeduplication<TEntity>(object id) where TEntity : class, new()
    {
        var collection = Database!.GetCollection<TEntity>("TestCollection_" + Guid.NewGuid().ToString("N"));
        var conventions = new Conventions();
        var ds = new DbSet<TEntity>(collection, conventions);
        var internalSet = (IInternalDbSet<TEntity>)ds;

        using var upsertBuf = new PooledDictionary<DocId, (TEntity Entity, bool IsDirty)>(16);
        using var rawIdBuf = new PooledHashSet<object>(16);
        using var modelBuf = new PooledList<WriteModel<TEntity>>(16);
        using var arena = new ArenaAllocator();

        var e1 = new TEntity();
        typeof(TEntity).GetProperty("Id")!.SetValue(e1, id);
        typeof(TEntity).GetProperty("Name")!.SetValue(e1, "First");

        var e2 = new TEntity();
        typeof(TEntity).GetProperty("Id")!.SetValue(e2, id);
        typeof(TEntity).GetProperty("Name")!.SetValue(e2, "Second");

        // Act: Commit both. Deduplication should take the last one ("Second").
        // Dirty list is empty for this test.
        var work = new CommitWork<TEntity>(
            added: new[] { e1, e2 }, 
            removed: [], 
            removedIds: [], 
            updated: [], 
            dirty: []);

        var buffers = new CommitBuffers<TEntity>(upsertBuf, rawIdBuf, modelBuf);
        var session = new SessionState(null!, TransactionContext.InMemory(), arena);

        var context = new CommitContext<TEntity>(work, buffers, session, null);

        await internalSet.CommitAsync(context);

        // Assert
        var results = await collection.Find(FilterDefinition<TEntity>.Empty).ToListAsync();
        Assert.Single(results);
        Assert.Equal("Second", typeof(TEntity).GetProperty("Name")!.GetValue(results[0]));
    }
    #endregion
}

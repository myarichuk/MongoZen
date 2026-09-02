using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

/// <summary>
/// Covers the paths that <see cref="IntegrationTestBase"/>'s Mongo.Fakes simulator cannot
/// exercise correctly: real transaction commit/abort (Mongo.Fakes is a standalone server, not
/// a replica set, so transactions never actually engage), concurrency-conflict detection against
/// real write-conflict semantics, and index conflict/recreate (the simulator doesn't return
/// server-accurate error codes/index metadata for these). Skipped, not failed, when Docker isn't
/// reachable — see <see cref="TestcontainersIntegrationTestBase"/>.
/// </summary>
public class TransactionCommitAndAbortTests : TestcontainersIntegrationTestBase
{
    public class TxDoc
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    [SkippableFact]
    public async Task Multi_Group_SaveChanges_With_RequireTransactions_Commits_Atomically()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var conventions = new DocumentConventions { RequireTransactions = true };
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
        using var session = store.OpenSession();

        session.Store(new SimpleEntity { Id = 1, Name = "A", Age = 1 });
        var user = new AdvancedUser { Name = "B", Age = 2 };
        session.Store(user);

        // RequireTransactions + a two-group save means SaveChangesAsync throws
        // TransactionRequirementException unless a real transaction was used (see
        // DocumentSession.SaveChangesAsync), so simply not throwing here is already load-bearing;
        // this assertion makes the discriminator explicit rather than implicit in "didn't throw".
        await session.SaveChangesAsync();
        Assert.True(store.Features.SupportsTransactions, "Topology discovery should have found a real replica set and enabled transactions.");

        using var verifySession = store.OpenSession();
        Assert.NotNull(await verifySession.LoadAsync<SimpleEntity>(1));
        Assert.NotNull(await verifySession.LoadAsync<AdvancedUser>(user.Id));
    }

    [SkippableFact]
    public async Task SaveChanges_Failure_Rolls_Back_The_Whole_Transaction()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        await Database.GetCollection<TxDoc>("TxDocs").Indexes.CreateOneAsync(
            new CreateIndexModel<TxDoc>(Builders<TxDoc>.IndexKeys.Ascending("Value"), new CreateIndexOptions { Unique = true }));

        using (var setup = store.OpenSession())
        {
            setup.Store(new TxDoc { Id = 11, Value = "DuplicateValue" });
            await setup.SaveChangesAsync();
        }

        var conventions = new DocumentConventions { RequireTransactions = true };
        var txStore = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
        using (var session = txStore.OpenSession())
        {
            // Two collections in one SaveChangesAsync forces a transaction (RequireTransactions);
            // the second write's unique-index violation must abort the whole thing, including
            // the first write that would otherwise have succeeded on its own.
            session.Store(new SimpleEntity { Id = 12, Name = "ShouldNotPersist", Age = 1 });
            session.Store(new TxDoc { Id = 13, Value = "DuplicateValue" }); // collides with Id=11

            await Assert.ThrowsAnyAsync<MongoException>(() => session.SaveChangesAsync());
        }

        using var verifySession = store.OpenSession();
        Assert.Null(await verifySession.LoadAsync<SimpleEntity>(12));
        Assert.Null(await verifySession.LoadAsync<TxDoc>(13));
    }
}

public class ConcurrencyConflictRealServerTests : TestcontainersIntegrationTestBase
{
    [SkippableFact]
    public async Task SaveChangesAsync_Should_Throw_ConcurrencyException_On_Update_Conflict()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        using (var session1 = store.OpenSession())
        {
            session1.Store(new ConcurrencyEntity { Id = 1, Name = "Original" });
            await session1.SaveChangesAsync();
        }

        using var sessionA = store.OpenSession();
        using var sessionB = store.OpenSession();

        var entityA = await sessionA.LoadAsync<ConcurrencyEntity>(1);
        var entityB = await sessionB.LoadAsync<ConcurrencyEntity>(1);

        entityA!.Name = "Modified by A";
        entityB!.Name = "Modified by B";

        await sessionA.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => sessionB.SaveChangesAsync());
        Assert.Same(entityB, ex.Entity);
    }
}

public class IndexConflictAndRecreateTests : TestcontainersIntegrationTestBase
{
    public class IndexedEntity
    {
        public string Id { get; set; } = null!;
        public string Email { get; set; } = null!;
    }

    public class IndexedEntity_ByEmail_Unique : AbstractIndexCreationTask<IndexedEntity>
    {
        public override string IndexName => "IndexedEntity_ByEmail";
        public override CreateIndexModel<IndexedEntity>? CreateIndexModel() =>
            new(Builders<IndexedEntity>.IndexKeys.Ascending(x => x.Email), new CreateIndexOptions { Unique = true });
    }

    public class IndexedEntity_ByEmail_NonUnique_ForceRecreate : AbstractIndexCreationTask<IndexedEntity>
    {
        public override string IndexName => "IndexedEntity_ByEmail";
        public override bool ForceRecreate => true;
        public override CreateIndexModel<IndexedEntity>? CreateIndexModel() =>
            new(Builders<IndexedEntity>.IndexKeys.Ascending(x => x.Email), new CreateIndexOptions { Unique = false });
    }

    [SkippableFact]
    public async Task Recreating_Index_With_Different_Options_Without_ForceRecreate_Throws()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(IndexedEntity));
        var collection = Database.GetCollection<IndexedEntity>(collectionName);

        await collection.Indexes.CreateOneAsync(new CreateIndexModel<IndexedEntity>(
            Builders<IndexedEntity>.IndexKeys.Ascending(x => x.Email),
            new CreateIndexOptions { Name = "IndexedEntity_ByEmail", Unique = false }));

        // Same name, different options (unique vs. non-unique): the server rejects this as a conflict.
        IAbstractIndexCreationTask conflicting = new IndexedEntity_ByEmail_Unique();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => conflicting.ExecuteAsync(store, CancellationToken.None).AsTask());
    }

    [SkippableFact]
    public async Task ForceRecreate_Drops_And_Recreates_The_Conflicting_Index()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(IndexedEntity));
        var collection = Database.GetCollection<IndexedEntity>(collectionName);

        await collection.Indexes.CreateOneAsync(new CreateIndexModel<IndexedEntity>(
            Builders<IndexedEntity>.IndexKeys.Ascending(x => x.Email),
            new CreateIndexOptions { Name = "IndexedEntity_ByEmail", Unique = true }));

        IAbstractIndexCreationTask task = new IndexedEntity_ByEmail_NonUnique_ForceRecreate();
        await task.ExecuteAsync(store, CancellationToken.None);

        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        var recreated = Assert.Single(indexes, i => i.GetValue("name", "").AsString == "IndexedEntity_ByEmail");
        Assert.False(recreated.GetValue("unique", false).ToBoolean());
    }
}

using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

/// <summary>
/// Pins down the unit-of-work boundaries against a real replica set: every
/// <c>SaveChangesAsync</c> owns one transaction (not just the first one in a session), attachment
/// writes ride along in that transaction and are committed by it, and reads never open one.
/// Skipped, not failed, when no replica set is reachable — see
/// <see cref="TestcontainersIntegrationTestBase"/>.
/// </summary>
public class TransactionLifecycleTests : TestcontainersIntegrationTestBase
{
    public class LifecycleDoc
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    [SkippableFact]
    public async Task Second_Multi_Group_Save_In_Same_Session_Is_Transactional_Again()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var conventions = new DocumentConventions { RequireTransactions = true };
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
        using var session = store.OpenSession();

        session.Store(new SimpleEntity { Id = 1, Name = "first", Age = 1 });
        var userA = new AdvancedUser { Id = ObjectId.GenerateNewId(), Name = "first", Age = 1 };
        session.Store(userA);
        await session.SaveChangesAsync();
        Assert.False(session.Advanced.IsTransactional);

        // Before the fix this threw TransactionRequirementException: the client session survived the
        // first commit, EnsureTransactionStartedAsync saw it was non-null and returned without
        // starting a new transaction, and the RequireTransactions check then failed.
        session.Store(new SimpleEntity { Id = 2, Name = "second", Age = 2 });
        var userB = new AdvancedUser { Id = ObjectId.GenerateNewId(), Name = "second", Age = 2 };
        session.Store(userB);
        await session.SaveChangesAsync();
        Assert.False(session.Advanced.IsTransactional);

        using var verify = store.OpenSession();
        Assert.NotNull(await verify.LoadAsync<SimpleEntity>(1));
        Assert.NotNull(await verify.LoadAsync<SimpleEntity>(2));
        Assert.NotNull(await verify.LoadAsync<AdvancedUser>(userA.Id));
        Assert.NotNull(await verify.LoadAsync<AdvancedUser>(userB.Id));
    }

    [SkippableFact]
    public async Task Second_Save_Failure_Rolls_Back_Only_The_Second_Save()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        await Database.GetCollection<LifecycleDoc>("LifecycleDocs").Indexes.CreateOneAsync(
            new CreateIndexModel<LifecycleDoc>(Builders<LifecycleDoc>.IndexKeys.Ascending("Value"), new CreateIndexOptions { Unique = true }));

        using (var setup = store.OpenSession())
        {
            setup.Store(new LifecycleDoc { Id = 100, Value = "taken" });
            await setup.SaveChangesAsync();
        }

        using var session = store.OpenSession();

        // Save #1: a committed, single-collection write.
        session.Store(new SimpleEntity { Id = 10, Name = "committed", Age = 1 });
        await session.SaveChangesAsync();

        // Save #2: two collections, second one violates the unique index. If this save were not
        // running in its own transaction, the SimpleEntity insert would survive the failure.
        session.Store(new SimpleEntity { Id = 11, Name = "must-roll-back", Age = 1 });
        session.Store(new LifecycleDoc { Id = 101, Value = "taken" });
        await Assert.ThrowsAnyAsync<MongoException>(() => session.SaveChangesAsync());
        Assert.False(session.Advanced.IsTransactional);

        using var verify = store.OpenSession();
        Assert.NotNull(await verify.LoadAsync<SimpleEntity>(10));
        Assert.Null(await verify.LoadAsync<SimpleEntity>(11));
        Assert.Null(await verify.LoadAsync<LifecycleDoc>(101));
    }

    [SkippableFact]
    public async Task Attachment_Store_Then_SaveChanges_With_No_Tracked_Entities_Is_Committed()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        const string documentId = "lifecycle/attach-1";

        using (var session = store.OpenSession())
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("durable?"));
            await session.Attachments.StoreAsync(documentId, "note.txt", stream, "text/plain");
            Assert.True(session.Advanced.IsTransactional, "an attachment write should have joined the unit of work");

            // Nothing tracked: before the fix this returned early without committing, and Dispose
            // then aborted the transaction — the attachment was silently lost.
            await session.SaveChangesAsync();
            Assert.False(session.Advanced.IsTransactional);
        }

        using var fresh = store.OpenSession();
        Assert.Contains("note.txt", await fresh.Attachments.GetNamesAsync(documentId));
        using var result = await fresh.Attachments.GetAsync(documentId, "note.txt");
        using var reader = new StreamReader(result.Stream);
        Assert.Equal("durable?", await reader.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task Attachment_Delete_Then_SaveChanges_With_No_Tracked_Entities_Is_Committed()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        const string documentId = "lifecycle/attach-2";

        using (var session = store.OpenSession())
        {
            using var stream = new MemoryStream([1, 2, 3]);
            await session.Attachments.StoreAsync(documentId, "bye.bin", stream);
            await session.SaveChangesAsync();
        }

        using (var session = store.OpenSession())
        {
            await session.Attachments.DeleteAsync(documentId, "bye.bin");
            Assert.True(session.Advanced.IsTransactional);
            await session.SaveChangesAsync();
        }

        using var fresh = store.OpenSession();
        Assert.Empty(await fresh.Attachments.GetNamesAsync(documentId));
    }

    [SkippableFact]
    public async Task Attachment_Store_Without_SaveChanges_Is_Rolled_Back_On_Dispose()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        const string documentId = "lifecycle/attach-3";

        using (var session = store.OpenSession())
        {
            using var stream = new MemoryStream([9, 9, 9]);
            await session.Attachments.StoreAsync(documentId, "never.bin", stream);
            // The write is visible to this session (read-your-writes inside the transaction)...
            Assert.Contains("never.bin", await session.Attachments.GetNamesAsync(documentId));
        }

        // ...but was never committed, so a fresh session must not see it.
        using var fresh = store.OpenSession();
        Assert.Empty(await fresh.Attachments.GetNamesAsync(documentId));
    }

    [SkippableFact]
    public async Task Reads_Do_Not_Open_A_Transaction()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using (var setup = store.OpenSession())
        {
            setup.Store(new SimpleEntity { Id = 20, Name = "readable", Age = 1 });
            await setup.SaveChangesAsync();
        }

        using var session = store.OpenSession();
        Assert.NotNull(await session.LoadAsync<SimpleEntity>(20));
        Assert.False(session.Advanced.IsTransactional, "LoadAsync must not open a transaction");

        await session.Attachments.GetNamesAsync(20);
        Assert.False(session.Advanced.IsTransactional, "Attachments.GetNamesAsync must not open a transaction");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => session.Attachments.GetAsync(20, "missing").AsTask());
        Assert.False(session.Advanced.IsTransactional, "Attachments.GetAsync must not open a transaction");

        // A no-op save on a session that never opened a transaction is itself a no-op.
        await session.SaveChangesAsync();
        Assert.False(session.Advanced.IsTransactional);
    }

    [SkippableFact]
    public async Task Entity_Write_And_Attachment_Write_Commit_Together_And_Abort_Together()
    {
        Skip.IfNot(SkipReason is null, SkipReason);

        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        await Database.GetCollection<LifecycleDoc>("LifecycleDocs").Indexes.CreateOneAsync(
            new CreateIndexModel<LifecycleDoc>(Builders<LifecycleDoc>.IndexKeys.Ascending("Value"), new CreateIndexOptions { Unique = true }));

        using (var setup = store.OpenSession())
        {
            setup.Store(new LifecycleDoc { Id = 200, Value = "unique" });
            await setup.SaveChangesAsync();
        }

        // Abort path: the entity insert fails, so the attachment written in the same unit of work
        // must not survive either.
        using (var session = store.OpenSession())
        {
            using var stream = new MemoryStream([1]);
            await session.Attachments.StoreAsync(201, "with-doc.bin", stream);
            session.Store(new LifecycleDoc { Id = 201, Value = "unique" });
            await Assert.ThrowsAnyAsync<MongoException>(() => session.SaveChangesAsync());
        }

        using (var verify = store.OpenSession())
        {
            Assert.Null(await verify.LoadAsync<LifecycleDoc>(201));
            Assert.Empty(await verify.Attachments.GetNamesAsync(201));
        }

        // Commit path: both land.
        using (var session = store.OpenSession())
        {
            using var stream = new MemoryStream([2]);
            await session.Attachments.StoreAsync(202, "with-doc.bin", stream);
            session.Store(new LifecycleDoc { Id = 202, Value = "also-unique" });
            await session.SaveChangesAsync();
        }

        using (var verify = store.OpenSession())
        {
            Assert.NotNull(await verify.LoadAsync<LifecycleDoc>(202));
            Assert.Contains("with-doc.bin", await verify.Attachments.GetNamesAsync(202));
        }
    }
}

/// <summary>
/// Needs no replica set (the guard fires before any I/O), so it runs on the Mongo.Fakes base.
/// </summary>
public class AttachmentDisposalGuardTests : IntegrationTestBase
{
    [Fact]
    public async Task Disposed_Session_Rejects_Attachment_Operations()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        var session = store.OpenSession();
        var attachments = session.Attachments;
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => attachments.GetNamesAsync("x").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attachments.GetAsync("x", "y").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attachments.DeleteAsync("x", "y").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attachments.DeleteAllAsync("x").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attachments.StoreAsync("x", "y", Stream.Null).AsTask());
    }

    [Fact]
    public async Task SaveChanges_With_Nothing_Tracked_And_No_Open_Transaction_Is_A_NoOp()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();
        await session.SaveChangesAsync();
        await session.SaveChangesAsync();
        Assert.False(session.Advanced.IsTransactional);
    }
}

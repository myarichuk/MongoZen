using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Sdk;

namespace MongoZen.Tests;

public class ImplicitTransactionTests : TestcontainersIntegrationTestBase
{
    public class SimpleDoc
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    [SkippableFact]
    public async Task SaveChanges_Should_Commit_Implicit_Transaction()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        
        // 1. Store something
        using (var session = store.OpenSession())
        {
            var doc = new SimpleDoc { Id = 1, Value = "Initial" };
            session.Store(doc);
            await session.SaveChangesAsync();
        }

        // 2. Verify it's there
        using (var session = store.OpenSession())
        {
            var doc = await session.LoadAsync<SimpleDoc>(1);
            Assert.NotNull(doc);
            Assert.Equal("Initial", doc.Value);
        }
    }

    [SkippableFact]
    public async Task Load_Does_Not_Open_A_Transaction_And_SaveChanges_Closes_The_One_It_Opens()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        using (var session = store.OpenSession())
        {
            session.Store(new SimpleDoc { Id = 2, Value = "v1" });
            await session.SaveChangesAsync();
        }

        using (var session = store.OpenSession())
        {
            // The unit of work is SaveChangesAsync, not the session: reads run outside any
            // transaction (otherwise the server's transaction lifetime clock would start ticking on
            // the first Load and a long-lived session would fail its save with NoSuchTransaction).
            var doc = await session.LoadAsync<SimpleDoc>(2);
            Assert.NotNull(doc);
            Assert.False(session.Advanced.IsTransactional);

            doc!.Value = "v2";
            await session.SaveChangesAsync();
            Assert.False(session.Advanced.IsTransactional);
        }

        using (var verify = store.OpenSession())
        {
            Assert.Equal("v2", (await verify.LoadAsync<SimpleDoc>(2))!.Value);
        }
    }

    [SkippableFact]
    public async Task SaveChanges_Failure_Should_Rollback()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        using (var session = store.OpenSession())
        {
            session.Store(new SimpleDoc { Id = 10, Value = "Before Failure" });
            
            // We want to force a failure. Let's try to insert another doc with same ID in the same session.
            // Wait, ChangeTracker will probably handle it or throw before BulkWrite.
            // Let's use a unique index violation.
            await Database.GetCollection<SimpleDoc>("SimpleDocs").Indexes.CreateOneAsync(
                new CreateIndexModel<SimpleDoc>(Builders<SimpleDoc>.IndexKeys.Ascending("Value"), new CreateIndexOptions { Unique = true }));

            session.Store(new SimpleDoc { Id = 11, Value = "DuplicateValue" });
            await session.SaveChangesAsync();

            // Setup finished. Now trigger collision.
            using (var session2 = store.OpenSession())
            {
                session2.Store(new SimpleDoc { Id = 12, Value = "NewValue" });
                session2.Store(new SimpleDoc { Id = 13, Value = "DuplicateValue" }); // Collision with Id=11
                
                await Assert.ThrowsAnyAsync<MongoException>(() => session2.SaveChangesAsync());
            }

            // Verify Id=12 was NOT persisted due to rollback
            using (var session3 = store.OpenSession())
            {
                var doc12 = await session3.LoadAsync<SimpleDoc>(12);
                Assert.Null(doc12);
            }
        }
    }
}

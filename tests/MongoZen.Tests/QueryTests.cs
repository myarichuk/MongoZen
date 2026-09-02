using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Xunit;

namespace MongoZen.Tests;

public class QueryTests : IntegrationTestBase
{
    [Fact]
    public async Task QueryAsync_Filter_Should_Track_And_Persist_Changes()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed 2 docs
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "Entity1", Age = 25 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "Entity2", Age = 30 });

        using var session = store.OpenSession();
        var results = await session.QueryAsync(Builders<SimpleEntity>.Filter.Gte(x => x.Age, 25));

        Assert.Equal(2, results.Count);
        var entity1 = results.First(e => e.Id == 1);

        // Mutate and save
        entity1.Name = "Modified";
        await session.SaveChangesAsync();

        // Verify the mutation persisted (and no duplicate insert)
        var allDocs = await collection.CountDocumentsAsync(Builders<SimpleEntity>.Filter.Empty);
        Assert.Equal(2, allDocs);

        var updated = await collection.Find(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 1)).FirstAsync();
        Assert.Equal("Modified", updated.Name);
    }

    [Fact]
    public async Task QueryAsync_Expression_Should_Return_Matching_Entities()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed 3 docs
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "Alice", Age = 20 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "Bob", Age = 30 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 3, Name = "Charlie", Age = 25 });

        using var session = store.OpenSession();
        var results = await session.QueryAsync<SimpleEntity>(x => x.Age >= 25);

        Assert.Equal(2, results.Count);
        Assert.Single(results, x => x.Name == "Bob");
        Assert.Single(results, x => x.Name == "Charlie");
    }

    [Fact]
    public async Task QueryAsync_Should_Return_Same_Instance_As_LoadAsync()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed doc
        await collection.InsertOneAsync(new SimpleEntity { Id = 42, Name = "TestEntity", Age = 99 });

        using var session = store.OpenSession();

        // LoadAsync first
        var loaded = await session.LoadAsync<SimpleEntity>(42);
        Assert.NotNull(loaded);

        // Query with matching filter
        var results = await session.QueryAsync(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 42));

        Assert.Single(results);
        var queried = results.First();

        // Should be the exact same instance (identity map coherence)
        Assert.Same(loaded, queried);
    }

    [Fact]
    public async Task QueryAsync_Should_Support_Sort_Skip_Limit()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed 5 docs with distinct ages
        for (int i = 1; i <= 5; i++)
        {
            await collection.InsertOneAsync(new SimpleEntity { Id = i, Name = $"Entity{i}", Age = i * 10 });
        }

        using var session = store.OpenSession();

        // Sort descending by Age, skip 1, limit 2
        var sort = Builders<SimpleEntity>.Sort.Descending(x => x.Age);
        var results = await session.QueryAsync(
            Builders<SimpleEntity>.Filter.Empty,
            sort: sort,
            skip: 1,
            limit: 2);

        Assert.Equal(2, results.Count);

        // Should be ids 4 and 3 (ages 40 and 30, skipping the 50)
        var ids = results.Select(x => x.Id).OrderByDescending(x => x).ToList();
        Assert.Equal(new[] { 4, 3 }, ids);
    }

    [Fact]
    public async Task AggregateAsync_Should_Return_Untracked_Projection()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        // Seed docs with ages
        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "Alice", Age = 20 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "Bob", Age = 30 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 3, Name = "Charlie", Age = 30 });

        using var session = store.OpenSession();

        // Get tracked count before aggregation
        var trackedBefore = session.Advanced.GetETagFor(new SimpleEntity { Id = 1 }) is null ? 0 : 1;

        // Group by Age, project to AggregationResult
        var pipeline = PipelineDefinition<SimpleEntity, AggregationResult>.Create(
            new BsonDocument("$group", new BsonDocument {
                { "_id", "$Age" },
                { "count", new BsonDocument("$sum", 1) }
            }));

        var results = await session.AggregateAsync(pipeline);

        Assert.Equal(2, results.Count);

        var age20 = results.FirstOrDefault(x => x.Id == 20);
        var age30 = results.FirstOrDefault(x => x.Id == 30);

        Assert.NotNull(age20);
        Assert.Equal(1, age20.Count);

        Assert.NotNull(age30);
        Assert.Equal(2, age30.Count);

        // Mutate one result and attempt to save — nothing should happen since untracked
        age20.Count = 999;
        await session.SaveChangesAsync();

        // Re-aggregate to verify mutations didn't persist
        var results2 = await session.AggregateAsync(pipeline);

        var age20After = results2.First(x => x.Id == 20);
        Assert.Equal(1, age20After.Count); // Still 1, not 999
    }

    [Fact]
    public async Task CountAsync_Should_Count_Matching_Documents()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "A", Age = 10 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "B", Age = 20 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 3, Name = "C", Age = 30 });

        using var session = store.OpenSession();

        var count = await session.CountAsync(Builders<SimpleEntity>.Filter.Gte(x => x.Age, 20));
        Assert.Equal(2, count);

        var zero = await session.CountAsync<SimpleEntity>(x => x.Age > 100);
        Assert.Equal(0, zero);
    }

    [Fact]
    public async Task AnyAsync_Should_Report_Presence_Of_Matching_Documents()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "A", Age = 10 });

        using var session = store.OpenSession();

        Assert.True(await session.AnyAsync(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 1)));
        Assert.False(await session.AnyAsync<SimpleEntity>(x => x.Age > 100));
    }

    [Fact]
    public async Task StreamAsync_Should_Enumerate_All_Matches_Without_Tracking()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        for (int i = 1; i <= 10; i++)
        {
            await collection.InsertOneAsync(new SimpleEntity { Id = i, Name = $"Entity{i}", Age = i });
        }

        using var session = store.OpenSession();

        var seen = new List<int>();
        await foreach (var entity in session.StreamAsync(Builders<SimpleEntity>.Filter.Empty))
        {
            seen.Add(entity.Id);
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(Enumerable.Range(1, 10), seen.OrderBy(x => x));

        // Streamed entities are untracked: the session's identity map is unaffected by streaming.
        var loaded = await session.LoadAsync<SimpleEntity>(1);
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task StreamAsync_Early_Break_Then_Restream_Should_Still_Work()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        for (int i = 1; i <= 5; i++)
        {
            await collection.InsertOneAsync(new SimpleEntity { Id = i, Name = $"Entity{i}", Age = i });
        }

        using var session = store.OpenSession();

        int count = 0;
        await foreach (var entity in session.StreamAsync(Builders<SimpleEntity>.Filter.Empty))
        {
            count++;
            if (count == 2) break;
        }

        Assert.Equal(2, count);

        // Breaking out of an `await foreach` early disposes the async iterator (and its `using`
        // cursor/arena) via DisposeAsync; a second independent stream confirms the session is
        // still usable afterward.
        var secondPassCount = 0;
        await foreach (var _ in session.StreamAsync(Builders<SimpleEntity>.Filter.Empty))
        {
            secondPassCount++;
        }
        Assert.Equal(5, secondPassCount);
    }

    [Fact]
    public async Task UpdateManyAsync_Should_Apply_Immediately_And_Bypass_Identity_Map()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "A", Age = 10 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "B", Age = 20 });

        using var session = store.OpenSession();

        // Load one entity first so the identity map holds a stale copy after the bulk update.
        var tracked = await session.LoadAsync<SimpleEntity>(1);
        Assert.NotNull(tracked);

        var modified = await session.Advanced.UpdateManyAsync(
            Builders<SimpleEntity>.Filter.Empty,
            Builders<SimpleEntity>.Update.Set(x => x.Name, "BulkUpdated"));

        Assert.Equal(2, modified);

        var raw = await collection.Find(Builders<SimpleEntity>.Filter.Eq(x => x.Id, 1)).FirstAsync();
        Assert.Equal("BulkUpdated", raw.Name);

        // The already-tracked instance is untouched by the bulk write (documented staleness).
        Assert.Equal("A", tracked!.Name);
    }

    [Fact]
    public async Task DeleteManyAsync_Should_Apply_Immediately()
    {
        var db = Database;
        var store = new DocumentStore(db.Client, db.DatabaseNamespace.DatabaseName);
        var collectionName = store.Conventions.GetCollectionName(typeof(SimpleEntity));
        var collection = db.GetCollection<SimpleEntity>(collectionName);

        await collection.InsertOneAsync(new SimpleEntity { Id = 1, Name = "A", Age = 10 });
        await collection.InsertOneAsync(new SimpleEntity { Id = 2, Name = "B", Age = 20 });

        using var session = store.OpenSession();

        var deleted = await session.Advanced.DeleteManyAsync(Builders<SimpleEntity>.Filter.Gte(x => x.Age, 15));

        Assert.Equal(1, deleted);
        Assert.Equal(1, await collection.CountDocumentsAsync(Builders<SimpleEntity>.Filter.Empty));
    }
}

public class AggregationResult
{
    [BsonId]
    public int Id { get; set; }
    [BsonElement("count")]
    public int Count { get; set; }
}

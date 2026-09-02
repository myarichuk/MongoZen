using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.HiLo;
using Xunit;
using Xunit.Sdk;

namespace MongoZen.Tests;

public class HiLoIdGeneratorTests : TestcontainersIntegrationTestBase
{
    private DocumentConventions NewConventions(int capacity) => new()
    {
        HiLoCapacity = capacity,
        HiLoCollectionName = "HiLo_" + Guid.NewGuid().ToString("N")
    };

    [SkippableFact]
    public void GenerateNextId_Sync_Should_Produce_Sequential_Ids()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = NewConventions(4);
        var generator = new HiLoIdGenerator(Database, conventions);

        var ids = Enumerable.Range(0, 4).Select(_ => generator.GenerateNextId("sync-tag")).ToArray();

        Assert.Equal(new[] { "sync-tag/1", "sync-tag/2", "sync-tag/3", "sync-tag/4" }, ids);
    }

    [SkippableFact]
    public async Task GenerateNextIdAsync_Full_Capacity_Range_Should_Yield_Exactly_Capacity_Ids_Without_Extra_Roundtrip()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = NewConventions(5);
        var generator = new HiLoIdGenerator(Database, conventions);
        const string tag = "widgets";

        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            ids.Add(await generator.GenerateNextIdAsync(tag));
        }

        Assert.Equal(new[] { "widgets/1", "widgets/2", "widgets/3", "widgets/4", "widgets/5" }, ids);

        var hiLoCollection = Database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
        var doc = await hiLoCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", tag)).FirstAsync();
        Assert.Equal(5L, doc["Max"].AsInt64);
    }

    [SkippableFact]
    public async Task GenerateNextIdAsync_Exhausting_Range_Triggers_Exactly_One_Refill()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = NewConventions(3);
        var generator = new HiLoIdGenerator(Database, conventions);
        const string tag = "gadgets";

        for (int i = 0; i < 4; i++)
        {
            await generator.GenerateNextIdAsync(tag);
        }

        var hiLoCollection = Database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
        var doc = await hiLoCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", tag)).FirstAsync();

        // Capacity 3 covers the first 3 ids exactly; the 4th id forces exactly one refill (Max: 3 -> 6).
        Assert.Equal(6L, doc["Max"].AsInt64);
    }

    [SkippableFact]
    public async Task GenerateNextIdAsync_Concurrent_Should_Never_Produce_Duplicates()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = NewConventions(4);
        var generator = new HiLoIdGenerator(Database, conventions);
        const string tag = "concurrent";
        const int callCount = 200;

        var tasks = Enumerable.Range(0, callCount).Select(_ => generator.GenerateNextIdAsync(tag).AsTask());
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(callCount, ids.Distinct().Count());
    }

    [SkippableFact]
    public async Task Two_Generators_Against_Same_Database_Claim_Disjoint_Ranges()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = NewConventions(4);
        var generatorA = new HiLoIdGenerator(Database, conventions);
        var generatorB = new HiLoIdGenerator(Database, conventions);
        const string tag = "shared";

        var idsA = new List<string>();
        var idsB = new List<string>();
        for (int i = 0; i < 4; i++) idsA.Add(await generatorA.GenerateNextIdAsync(tag));
        for (int i = 0; i < 4; i++) idsB.Add(await generatorB.GenerateNextIdAsync(tag));

        Assert.Empty(idsA.Intersect(idsB));
    }
}

using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

public class IndexCreationTests : IntegrationTestBase
{
    public class IndexedEntity
    {
        public string Id { get; set; } = null!;
        public string Email { get; set; } = null!;
        public int Age { get; set; }
    }

    public class IndexedEntity_ByEmail : AbstractIndexCreationTask<IndexedEntity>
    {
        public override CreateIndexModel<IndexedEntity> CreateIndexModel()
        {
            return new CreateIndexModel<IndexedEntity>(
                Keys.Ascending(x => x.Email),
                new CreateIndexOptions { Unique = true }
            );
        }
    }

    [Fact]
    public async Task CanCreateIndexesFromAssembly()
    {
        // Arrange
        using var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        // Act
        await store.ExecuteIndexesAsync(typeof(IndexCreationTests).Assembly);

        // Assert
        var collectionName = store.Conventions.GetCollectionName(typeof(IndexedEntity));
        var collection = Database!.GetCollection<IndexedEntity>(collectionName);
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();

        Assert.Contains(indexes, i => i.GetValue("name", "").AsString == nameof(IndexedEntity_ByEmail));
        Assert.Contains(indexes, i => i.GetValue("unique", false).ToBoolean() == true);
    }
}

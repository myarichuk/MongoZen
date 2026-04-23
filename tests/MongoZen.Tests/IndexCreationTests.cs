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
        public override string CollectionName => "IndexedEntities";

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
        // Act
        await IndexCreation.CreateIndexesAsync(typeof(IndexCreationTests).Assembly, Database!);

        // Assert
        var collectionName = "IndexedEntities";
        var collection = Database!.GetCollection<IndexedEntity>(collectionName);
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();

        Assert.Contains(indexes, i => i.GetValue("name", "").AsString == nameof(IndexedEntity_ByEmail));
        Assert.Contains(indexes, i => i.GetValue("unique", false).ToBoolean() == true);
    }

    public class TestDbContext : DbContext
    {
        public IDbSet<IndexedEntity> IndexedEntities { get; set; } = null!;

        public TestDbContext(DbContextOptions options) : base(options)
        {
        }
    }

    [Fact]
    public async Task DbContextCanCreateIndexes()
    {
        // Arrange
        var options = new DbContextOptions(Database!);
        var context = new TestDbContext(options);

        // Act
        await context.CreateIndexesAsync();

        // Assert
        var collectionName = context.GetCollectionName(typeof(IndexedEntity));
        var collection = Database!.GetCollection<IndexedEntity>(collectionName);
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();

        Assert.Contains(indexes, i => i.GetValue("name", "").AsString == nameof(IndexedEntity_ByEmail));
    }
}

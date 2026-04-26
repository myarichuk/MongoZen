using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class DocIdDeduplicationTests : IntegrationTestBase
{
    #region Entity Types
    public class EntityWithObjectId { public ObjectId Id { get; set; } public string Name { get; set; } = ""; }
    public class EntityWithGuid { [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid Id { get; set; } public string Name { get; set; } = ""; }
    public class EntityWithInt { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class EntityWithLong { public long Id { get; set; } public string Name { get; set; } = ""; }
    public class EntityWithString { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    
    public class CompositeKey : IDocIdHashable
    {
        public string Part1 { get; set; } = "";
        public int Part2 { get; set; }
        public int WriteIdBytes(Span<byte> destination)
        {
            var p1 = System.Text.Encoding.UTF8.GetBytes(Part1);
            p1.CopyTo(destination);
            BitConverter.TryWriteBytes(destination[p1.Length..], Part2);
            return p1.Length + 4;
        }
    }
    public class EntityWithComposite { public CompositeKey Id { get; set; } = new(); public string Name { get; set; } = ""; }

    public class ComplexKey { public string Key { get; set; } = ""; }
    public class EntityWithComplex { public ComplexKey Id { get; set; } = new(); public string Name { get; set; } = ""; }
    #endregion

    #region DocId Unit Tests
    [Fact]
    public void DocId_ObjectId_Equality()
    {
        var oid = ObjectId.GenerateNewId();
        var id1 = DocId.From(oid);
        var id2 = DocId.From(oid);
        var id3 = DocId.From(ObjectId.GenerateNewId());

        Assert.Equal(id1, id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public void DocId_Guid_Equality()
    {
        var guid = Guid.NewGuid();
        var id1 = DocId.From(guid);
        var id2 = DocId.From(guid);
        var id3 = DocId.From(Guid.NewGuid());

        Assert.Equal(id1, id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public void DocId_String_Equality()
    {
        var s = "test-id";
        var id1 = DocId.From(s);
        var id2 = DocId.From(s);
        var id3 = DocId.From("other-id");

        Assert.Equal(id1, id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public void DocId_Int_Equality()
    {
        var id1 = DocId.From(123);
        var id2 = DocId.From(123);
        var id3 = DocId.From(456);

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public void DocId_Composite_Equality()
    {
        var k1 = new CompositeKey { Part1 = "A", Part2 = 1 };
        var k2 = new CompositeKey { Part1 = "A", Part2 = 1 };
        var k3 = new CompositeKey { Part1 = "B", Part2 = 1 };

        Assert.Equal(DocId.From(k1), DocId.From(k2));
        Assert.NotEqual(DocId.From(k1), DocId.From(k3));
    }

    [Fact]
    public void DocId_BsonFallback_Equality()
    {
        var k1 = new ComplexKey { Key = "X" };
        var k2 = new ComplexKey { Key = "X" };
        var k3 = new ComplexKey { Key = "Y" };

        Assert.Equal(DocId.From(k1), DocId.From(k2));
        Assert.NotEqual(DocId.From(k1), DocId.From(k3));
    }
    #endregion

    #region Integration Tests
    [Fact]
    public async Task DbSet_Deduplicates_ObjectId()
    {
        var oid = ObjectId.GenerateNewId();
        await TestDeduplication<EntityWithObjectId, ObjectId>(oid);
    }

    [Fact]
    public async Task DbSet_Deduplicates_Guid()
    {
        var guid = Guid.NewGuid();
        await TestDeduplication<EntityWithGuid, Guid>(guid);
    }

    [Fact]
    public async Task DbSet_Deduplicates_String()
    {
        await TestDeduplication<EntityWithString, string>("string-id");
    }

    [Fact]
    public async Task DbSet_Deduplicates_Int()
    {
        await TestDeduplication<EntityWithInt, int>(99);
    }

    private async Task TestDeduplication<TEntity, TId>(TId id) where TEntity : class, new()
    {
        var options = new DbContextOptions(Database!);
        var collection = Database!.GetCollection<TEntity>(typeof(TEntity).Name);
        var dbSet = new DbSet<TEntity>(collection, options.Conventions);
        
        var internalSet = (IInternalDbSet<TEntity>)dbSet;
        using var arena = new SharpArena.Allocators.ArenaAllocator();
        var upsertBuf = new Dictionary<DocId, TEntity>();
        var removedBuf = new HashSet<DocId>();
        var modelBuf = new List<WriteModel<TEntity>>();

        var e1 = new TEntity();
        typeof(TEntity).GetProperty("Id")!.SetValue(e1, id);
        typeof(TEntity).GetProperty("Name")!.SetValue(e1, "First");

        var e2 = new TEntity();
        typeof(TEntity).GetProperty("Id")!.SetValue(e2, id);
        typeof(TEntity).GetProperty("Name")!.SetValue(e2, "Second");

        // Act: Commit both. Deduplication should take the last one ("Second").
        await internalSet.CommitAsync(new[] { e1, e2 }, [], [], [], [], arena, upsertBuf, removedBuf, modelBuf, null);

        // Assert
        var results = await collection.Find(FilterDefinition<TEntity>.Empty).ToListAsync();
        Assert.Single(results);
        Assert.Equal("Second", typeof(TEntity).GetProperty("Name")!.GetValue(results[0]));
    }
    #endregion
}

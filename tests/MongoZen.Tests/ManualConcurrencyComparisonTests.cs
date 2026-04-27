using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Xunit;
using MongoZen;
using SharpArena.Allocators;

namespace MongoZen.Tests;

public class ManualConcurrencyComparisonTests : IntegrationTestBase
{
    public class VersionedEntity
    {
        [BsonId]
        public string Id { get; set; } = null!;
        public string Data { get; set; } = null!;
        
        [BsonElement("v")] // Remapped!
        public int Version { get; set; }
    }

    private struct VersionedEntity_Shadow
    {
        public bool _hasValue;
        public SharpArena.Collections.ArenaString Id;
        public SharpArena.Collections.ArenaString Data;
        public long Version;

        public void From(VersionedEntity source, ArenaAllocator arena)
        {
            _hasValue = true;
            Id = SharpArena.Collections.ArenaString.Clone(source.Id, arena);
            Data = SharpArena.Collections.ArenaString.Clone(source.Data, arena);
            Version = source.Version;
        }

        public bool IsDirty(VersionedEntity current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            return !Id.Equals(current.Id) || !Data.Equals(current.Data) || Version != current.Version;
        }
    }

    private class TestDbContext : DbContext
    {
        public IDbSet<VersionedEntity> Entities { get; set; } = null!;
        public TestDbContext(DbContextOptions options) : base(options) { }
    }

    private class TestSession : DbContextSession<TestDbContext>
    {
        public TestSession(TestDbContext dbContext) : base(dbContext)
        {
            Entities = new MutableDbSet<VersionedEntity>(
                _dbContext.Entities,
                () => Transaction,
                this,
                (entity, arena) => { unsafe {
                    var ptr = arena.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<VersionedEntity_Shadow>());
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<VersionedEntity_Shadow>(ptr);
                    s.From(entity, arena);
                    return (System.IntPtr)ptr;
                }},
                (entity, ptr) => { unsafe {
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<VersionedEntity_Shadow>((void*)ptr);
                    return s.IsDirty(entity);
                }},
                null,
                _dbContext.Options.Conventions);
            
            RegisterDbSet((MutableDbSet<VersionedEntity>)Entities);
        }

        public IMutableDbSet<VersionedEntity> Entities { get; }
    }

    [Fact]
    public async Task MongoZen_Concurrency_Matches_Manual_Logic_With_Remapped_BsonName()
    {
        var db = new TestDbContext(new DbContextOptions(Database!));
        var collection = Database!.GetCollection<VersionedEntity>("Entities");

        // 1. Initial setup
        var entity = new VersionedEntity { Id = "e1", Data = "Initial", Version = 1 };
        await collection.InsertOneAsync(entity);

        // 2. Simulate manual update with conflict logic
        // User A loads version 1
        var userALoaded = await collection.Find(e => e.Id == "e1").FirstAsync();
        // User B loads version 1
        var userBLoaded = await collection.Find(e => e.Id == "e1").FirstAsync();

        // User A saves (manual)
        var filterA = Builders<VersionedEntity>.Filter.And(
            Builders<VersionedEntity>.Filter.Eq(e => e.Id, "e1"),
            Builders<VersionedEntity>.Filter.Eq("v", 1) // Manual use of remapped name
        );
        userALoaded.Data = "UserA_Update";
        userALoaded.Version = 2;
        var resultA = await collection.ReplaceOneAsync(filterA, userALoaded);
        Assert.Equal(1, resultA.MatchedCount);

        // User B tries to save with MongoZen
        await using var sessionB = new TestSession(db);
        var bEntity = await sessionB.Entities.LoadAsync("e1");
        Assert.NotNull(bEntity);
        // We need to simulate that bEntity was loaded BEFORE User A's save
        // In this test, LoadAsync actually loaded the version 2! 
        // So we manually set it back to 1 and re-track to simulate stale load.
        bEntity.Version = 1;
        bEntity.Data = "Initial";
        sessionB.Advanced.ClearTracking();
        sessionB.Entities.Attach(bEntity);

        // Now modify and save
        bEntity.Data = "UserB_Conflict";
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => sessionB.SaveChangesAsync());
        Assert.Single(ex.FailedIds);
        Assert.Equal("e1", ex.FailedIds[0]);

        // Verify database state is still UserA's
        var final = await collection.Find(e => e.Id == "e1").FirstAsync();
        Assert.Equal("UserA_Update", final.Data);
        Assert.Equal(2, final.Version);
    }
}

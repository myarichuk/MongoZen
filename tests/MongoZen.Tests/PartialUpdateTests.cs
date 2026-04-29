using MongoDB.Driver;
using SharpArena.Collections;
using MongoZen.Collections;
using SharpArena.Allocators;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MongoZen.Tests;

public class PartialUpdateIntegrationTests : IntegrationTestBase
{
    public class ComplexEntity
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<int> Scores { get; set; } = new();
        public Dictionary<string, string> Tags { get; set; } = new();
        public int Version { get; set; }
    }

    private struct ComplexEntity_Shadow
    {
        public bool _hasValue;
        public ArenaString Name;
        public SharpArena.Collections.ArenaList<int> Scores;
        public ArenaDictionary<ArenaString, ArenaString> Tags;
        public long Version;

        public void From(ComplexEntity source, ArenaAllocator arena)
        {
            _hasValue = true;
            Name = ArenaString.Clone(source.Name, arena);
            
            Scores = new SharpArena.Collections.ArenaList<int>(arena, source.Scores.Count);
            foreach (var s in source.Scores) Scores.Add(s);

            Tags = new ArenaDictionary<ArenaString, ArenaString>(arena, source.Tags.Count);
            foreach (var kvp in source.Tags)
            {
                Tags.AddOrUpdate(ArenaString.Clone(kvp.Key, arena), ArenaString.Clone(kvp.Value, arena));
            }
            Version = source.Version;
        }

        public bool IsDirty(ComplexEntity current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            if (!Name.Equals(current.Name)) return true;
            if (Scores.Length != current.Scores.Count) return true;
            for (int i = 0; i < Scores.Length; i++) if (Scores[i] != current.Scores[i]) return true;
            if (Tags.Count != current.Tags.Count) return true;
            foreach (var kvp in current.Tags)
            {
                if (!Tags.TryGetValue(kvp.Key, out var shadowVal) || !shadowVal.Equals(kvp.Value)) return true;
            }
            return false;
        }

        public UpdateDefinition<ComplexEntity>? ExtractChanges(ComplexEntity current)
        {
            UpdateDefinition<ComplexEntity>? update = null;
            var builder = Builders<ComplexEntity>.Update;

            if (!Name.Equals(current.Name))
                update = builder.Set(x => x.Name, current.Name);

            // Collection check
            bool scoresDirty = false;
            if (Scores.Length != current.Scores.Count) scoresDirty = true;
            else
            {
                for (int i = 0; i < (int)Scores.Length; i++)
                {
                    if (Scores[i] != current.Scores[i]) { scoresDirty = true; break; }
                }
            }
            if (scoresDirty)
            {
                var set = builder.Set(x => x.Scores, current.Scores);
                update = update == null ? set : builder.Combine(update, set);
            }

            // Dictionary check (O(1))
            bool tagsDirty = false;
            if (Tags.Count != current.Tags.Count) tagsDirty = true;
            else
            {
                foreach (var kvp in current.Tags)
                {
                    if (!Tags.TryGetValue(kvp.Key, out var shadowVal) || !shadowVal.Equals(kvp.Value))
                    {
                        tagsDirty = true;
                        break;
                    }
                }
            }
            if (tagsDirty)
            {
                var set = builder.Set(x => x.Tags, current.Tags);
                update = update == null ? set : builder.Combine(update, set);
            }

            return update;
        }
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<ComplexEntity> Entities { get; set; } = null!;

        protected override void InitializeDbSets()
        {
            if (Options.UseInMemory)
                Entities = new InMemoryDbSet<ComplexEntity>("Entities", Options.Conventions);
            else
                Entities = new DbSet<ComplexEntity>(Options.Mongo!.GetCollection<ComplexEntity>("Entities"), Options.Conventions);
        }

        public override string GetCollectionName(Type entityType)
        {
            if (entityType == typeof(ComplexEntity)) return "Entities";
            throw new ArgumentException();
        }
    }

    private class TestSession : DbContextSession<TestDbContext>
    {
        public static async Task<TestSession> OpenSessionAsync(TestDbContext dbContext)
        {
            var session = new TestSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestSession(TestDbContext dbContext) : base(dbContext)
        {
            Entities = new MutableDbSet<ComplexEntity>(
                _dbContext.Entities,
                () => Transaction,
                this,
                (e, a) => { 
                    unsafe {
                        var ptr = a.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<ComplexEntity_Shadow>());
                        ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<ComplexEntity_Shadow>(ptr);
                        s.From(e, a);
                        return (IntPtr)ptr;
                    }
                },
                (e, ptr) => { unsafe { ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<ComplexEntity_Shadow>((void*)ptr); return s.IsDirty(e); } },
                (e, ptr) => { unsafe { ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<ComplexEntity_Shadow>((void*)ptr); return s.ExtractChanges(e); } },
                _dbContext.Options.Conventions);
            RegisterDbSet((MutableDbSet<ComplexEntity>)Entities);
        }

        public IMutableDbSet<ComplexEntity> Entities { get; }
    }

    [Fact]
    public async Task PartialUpdate_SendsOnlyChangedFields()
    {
        var db = new TestDbContext(new DbContextOptions(Database!));
        var collection = Database!.GetCollection<ComplexEntity>("Entities");

        // 1. Initial setup
        var entity = new ComplexEntity 
        { 
            Id = "c1", 
            Name = "Original", 
            Scores = new List<int> { 1, 2 },
            Tags = new Dictionary<string, string> { ["type"] = "test" },
            Version = 1 
        };
        await collection.InsertOneAsync(entity);

        // 2. Modify one field
        await using (var session = await TestSession.OpenSessionAsync(db))
        {
            var loaded = await session.Entities.LoadAsync("c1");
            Assert.NotNull(loaded);
            
            loaded!.Name = "Changed";
            // We DON'T change Scores or Tags
            
            await session.SaveChangesAsync();
        }

        // 3. Verify in DB
        var fresh = await collection.Find(e => e.Id == "c1").FirstAsync();
        Assert.Equal("Changed", fresh.Name);
        Assert.Equal(1, fresh.Scores[0]);
        Assert.Equal("test", fresh.Tags["type"]);
        Assert.Equal(2, fresh.Version);
    }

    [Fact]
    public async Task PartialUpdate_CollectionChange_SendsWholeCollection()
    {
        var db = new TestDbContext(new DbContextOptions(Database!));
        var collection = Database!.GetCollection<ComplexEntity>("Entities");

        var entity = new ComplexEntity { Id = "c2", Name = "A", Scores = new List<int> { 1 }, Version = 1 };
        await collection.InsertOneAsync(entity);

        await using (var session = await TestSession.OpenSessionAsync(db))
        {
            var loaded = await session.Entities.LoadAsync("c2");
            loaded!.Scores.Add(2);
            await session.SaveChangesAsync();
        }

        var fresh = await collection.Find(e => e.Id == "c2").FirstAsync();
        Assert.Equal(new[] { 1, 2 }, fresh.Scores);
    }
}


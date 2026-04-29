using MongoZen;
using Xunit;
using SharpArena.Allocators;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen.Tests;

public class IdentityMapTests : IntegrationTestBase
{
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public string? Ignored { get; set; }
    }

    private struct User_Shadow
    {
        public bool _hasValue;
        public SharpArena.Collections.ArenaString Name;
        // Ignored property is NOT here

        public void From(User source, ArenaAllocator arena)
        {
            _hasValue = true;
            Name = SharpArena.Collections.ArenaString.Clone(source.Name, arena);
        }

        public bool IsDirty(User current)
        {
            if (current == null) return _hasValue;
            if (!_hasValue) return true;
            return !Name.Equals(current.Name);
        }
    }

    private partial class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;

        protected override void InitializeDbSets()
        {
            if (Options.UseInMemory)
            {
                Users = new InMemoryDbSet<User>("Users", Options.Conventions);
            }
            else
            {
                Users = new DbSet<User>(Options.Mongo!.GetCollection<User>("Users"), Options.Conventions);
            }
        }

        public override string GetCollectionName(Type entityType)
        {
            if (entityType == typeof(User)) return "Users";
            throw new ArgumentException();
        }
    }

    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public static async Task<TestDbContextSession> OpenSessionAsync(TestDbContext dbContext)
        {
            var session = new TestDbContextSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestDbContextSession(TestDbContext dbContext) : base(dbContext)
        {
            Users = new MutableDbSet<User>(
                _dbContext.Users,
                () => Transaction,
                this,
                (entity, arena) => { unsafe {
                    var ptr = arena.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<User_Shadow>());
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<User_Shadow>(ptr);
                    s.From(entity, arena);
                    return (System.IntPtr)ptr;
                }},
                (entity, ptr) => { unsafe {
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<User_Shadow>((void*)ptr);
                    return s.IsDirty(entity);
                }},
                null,
                _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            await EnsureTransactionActiveAsync();
            await ((IInternalMutableDbSet)Users).CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task IdentityMap_EnsuresSameInstance()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var userId = Guid.NewGuid().ToString();
        
        // Initial setup
        await using (var setupSession = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            setupSession.Users.Add(new User { Id = userId, Name = "Original" });
            await setupSession.SaveChangesAsync();
        }

        // Test Identity Map
        await using (var session = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            var u1 = (await session.Users.QueryAsync(u => u.Id == userId)).First();
            var u2 = (await session.Users.QueryAsync(u => u.Id == userId)).First();

            Assert.Same(u1, u2); // Same reference!
        }
    }

    [Fact]
    public async Task ChangeTracking_DetectsImplicitChanges()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var userId = Guid.NewGuid().ToString();
        
        // Initial setup
        await using (var setupSession = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            setupSession.Users.Add(new User { Id = userId, Name = "Original" });
            await setupSession.SaveChangesAsync();
        }

        // Test Change Tracking
        await using (var session = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            var user = (await session.Users.QueryAsync(u => u.Id == userId)).First();
            
            user.Name = "Changed";
            
            // Note: We are NOT calling session.Users.Update(user)
            await session.SaveChangesAsync();
        }

        // Verify persistence
        await using (var verifySession = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            var user = (await verifySession.Users.QueryAsync(u => u.Id == userId)).First();
            Assert.Equal("Changed", user.Name);
        }
    }

    [Fact]
    public async Task ChangeTracking_IgnoresBsonIgnoreProperties()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var userId = Guid.NewGuid().ToString();
        
        // Initial setup
        await using (var setupSession = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            setupSession.Users.Add(new User { Id = userId, Name = "Original", Ignored = "Initial" });
            await setupSession.SaveChangesAsync();
        }

        // Test Change Tracking
        await using (var session = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            var user = (await session.Users.QueryAsync(u => u.Id == userId)).First();
            
            user.Ignored = "Changed"; // This should NOT trigger a save
            
            await session.SaveChangesAsync();
        }

        // Verify that no update happened by checking another field or just asserting it runs without error
        // In a real scenario, we could check MongoDB last modified time or something similar, 
        // but here the fact that it doesn't throw and potentially would have No-Op'ed the update is the goal.
        // Actually, if only ignored properties changed, GetDirtyEntities should be empty.
        
        await using (var session = await TestDbContextSession.OpenSessionAsync(ctx))
        {
            var user = (await session.Users.QueryAsync(u => u.Id == userId)).First();
            user.Ignored = "Changed Again";
            
            var dirty = session.GetDirtyEntities<User>().ToList();
            Assert.Empty(dirty);
        }
    }
}


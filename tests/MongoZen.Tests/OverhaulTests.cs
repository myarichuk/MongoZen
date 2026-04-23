using MongoDB.Driver;
using MongoZen;
using Xunit;
using SharpArena.Allocators;

namespace MongoZen.Tests;

public class OverhaulTests : IntegrationTestBase
{
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }

    private struct User_Shadow
    {
        public bool _hasValue;
        public SharpArena.Collections.ArenaString Name;

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

    private partial class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions options) : base(options) { }
        public IDbSet<User> Users { get; set; } = null!;
    }

    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public TestDbContextSession(TestDbContext dbContext) : base(dbContext)
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
                } },
                (entity, ptr) => { unsafe {
                    ref var s = ref System.Runtime.CompilerServices.Unsafe.AsRef<User_Shadow>((void*)ptr); 
                    return s.IsDirty(entity); 
                } },
                _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async Task SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Users.CommitAsync(Transaction);
            await CommitTransactionAsync();
            Users.ClearTracking();
        }
    }

    [Fact]
    public async Task ImplicitSession_IsUsedForQueries()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var session = new TestDbContextSession(ctx);

        // Add a user within the transaction
        session.Users.Add(new User { Name = "Implicit" });
        await session.SaveChangesAsync();

        // Start a new session
        var session2 = new TestDbContextSession(ctx);
        
        var results = await session2.Users.QueryAsync(u => u.Name == "Implicit");
        Assert.Single(results);
    }

    [Fact]
    public async Task BulkCommit_HandlesMultipleOperations()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var session = new TestDbContextSession(ctx);

        var u1 = new User { Name = "User1" };
        var u2 = new User { Name = "User2" };
        
        session.Users.Add(u1);
        session.Users.Add(u2);
        await session.SaveChangesAsync();

        var session2 = new TestDbContextSession(ctx);
        var u1Loaded = (await session2.Users.QueryAsync(u => u.Name == "User1")).First();
        u1Loaded.Name = "User1 Updated";
        
        var u2Loaded = (await session2.Users.QueryAsync(u => u.Name == "User2")).First();
        session2.Users.Remove(u2Loaded);
        
        session2.Users.Add(new User { Name = "User3" });
        
        await session2.SaveChangesAsync();

        var all = (await ctx.Users.QueryAsync(u => true)).ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, u => u.Name == "User1 Updated");
        Assert.Contains(all, u => u.Name == "User3");
        Assert.DoesNotContain(all, u => u.Name == "User2");
    }
}

using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class IdentityMapTests : IntegrationTestBase
{
    private class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
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
            Users = new MutableDbSet<User>(_dbContext.Users, () => Transaction, this, _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Users.CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task IdentityMap_EnsuresSameInstance()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var userId = Guid.NewGuid().ToString();
        
        // Initial setup
        await using (var setupSession = new TestDbContextSession(ctx))
        {
            setupSession.Users.Add(new User { Id = userId, Name = "Original" });
            await setupSession.SaveChangesAsync();
        }

        // Test Identity Map
        await using (var session = new TestDbContextSession(ctx))
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
        await using (var setupSession = new TestDbContextSession(ctx))
        {
            setupSession.Users.Add(new User { Id = userId, Name = "Original" });
            await setupSession.SaveChangesAsync();
        }

        // Test Change Tracking
        await using (var session = new TestDbContextSession(ctx))
        {
            var user = (await session.Users.QueryAsync(u => u.Id == userId)).First();
            
            user.Name = "Changed";
            
            // Note: We are NOT calling session.Users.Update(user)
            await session.SaveChangesAsync();
        }

        // Verify persistence
        await using (var verifySession = new TestDbContextSession(ctx))
        {
            var user = (await verifySession.Users.QueryAsync(u => u.Id == userId)).First();
            Assert.Equal("Changed", user.Name);
        }
    }
}

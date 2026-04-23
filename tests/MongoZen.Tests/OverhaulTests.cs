using MongoDB.Driver;
using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class OverhaulTests : IntegrationTestBase
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

    // This part would normally be generated
    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public TestDbContextSession(TestDbContext dbContext) : base(dbContext)
        {
            Users = new MutableDbSet<User>(_dbContext.Users, () => Transaction, this, (e, a) => IntPtr.Zero, (e, p) => true, _dbContext.Options.Conventions);
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
        
        // Query within the transaction (should be empty if we haven't saved session2 yet, 
        // but here we just want to see it doesn't throw and uses the session)
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
        u1.Name = "User1 Updated";
        session2.Users.Update(u1);
        session2.Users.Remove(u2);
        session2.Users.Add(new User { Name = "User3" });
        
        await session2.SaveChangesAsync();

        var all = (await ctx.Users.QueryAsync(u => true)).ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, u => u.Name == "User1 Updated");
        Assert.Contains(all, u => u.Name == "User3");
        Assert.DoesNotContain(all, u => u.Name == "User2");
    }
}

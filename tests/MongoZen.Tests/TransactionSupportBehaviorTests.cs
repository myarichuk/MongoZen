using MongoZen;

namespace MongoZen.Tests;

public class TransactionSupportBehaviorTests : IntegrationTestBase
{
    private class User
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private class TestDbContext : DbContext
    {
        public IDbSet<User> Users { get; set; } = null!;

        public TestDbContext(DbContextOptions options)
            : base(options)
        {
        }
    }

    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public TestDbContextSession(TestDbContext dbContext)
            : base(dbContext)
        {
            Users = new MutableDbSet<User>(_dbContext.Users, _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            try
            {
                await Users.CommitAsync(Transaction);

                await CommitTransactionAsync();
                await DisposeAsync();
            }
            catch
            {
                if (Transaction.IsActive)
                {
                    await AbortTransactionAsync();
                }

                await DisposeAsync();
                throw;
            }
        }
    }

    public TransactionSupportBehaviorTests()
        : base(useSingleReplicaSet: false)
    {
    }

    [Fact]
    public void StartSession_Throws_When_Transactions_Unsupported()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!, new Conventions()));

        var ex = Assert.Throws<InvalidOperationException>(() => new TestDbContextSession(ctx));
        Assert.Contains("replica set", ex.Message);
    }

    [Fact]
    public async Task SaveChanges_Simulates_When_Transactions_Unsupported()
    {
        var conventions = new Conventions { TransactionSupportBehavior = TransactionSupportBehavior.Simulate };
        var ctx = new TestDbContext(new DbContextOptions(Database!, conventions));
        using var session = new TestDbContextSession(ctx);

        session.Users.Add(new User { Id = "2", Name = "Bob" });

        await session.SaveChangesAsync();

        var saved = await ctx.Users.QueryAsync(u => u.Id == "2");
        Assert.Single(saved);
    }
}

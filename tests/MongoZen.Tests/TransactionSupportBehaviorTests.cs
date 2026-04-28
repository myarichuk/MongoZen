using MongoZen;

namespace MongoZen.Tests;

public class TransactionSupportBehaviorTests : IntegrationTestBase
{
    private class User
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;
    }

    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public static async Task<TestDbContextSession> OpenSessionAsync(TestDbContext dbContext)
        {
            var session = new TestDbContextSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestDbContextSession(TestDbContext dbContext)
            : base(dbContext)
        {
            Users = new MutableDbSet<User>(_dbContext.Users, () => Transaction, this, null, null, null, _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            await EnsureTransactionActiveAsync();
            try
            {
                await ((IInternalMutableDbSet)Users).CommitAsync(Transaction);

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

    [Fact]
    public async Task StartSession_Throws_When_Transactions_Unsupported()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!, new Conventions { DisableTransactions = true }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestDbContextSession.OpenSessionAsync(ctx));
        Assert.Contains("Transactions not supported.", ex.Message);
    }

    [Fact]
    public async Task SaveChanges_Simulates_When_Transactions_Unsupported()
    {
        var conventions = new Conventions { TransactionSupportBehavior = TransactionSupportBehavior.Simulate, DisableTransactions = true };
        var ctx = new TestDbContext(new DbContextOptions(Database!, conventions));
        await using var session = await TestDbContextSession.OpenSessionAsync(ctx);

        session.Users.Add(new User { Id = "2", Name = "Bob" });

        await session.SaveChangesAsync();

        var saved = await ctx.Users.QueryAsync(u => u.Id == "2");
        Assert.Single(saved);
    }
}

using MongoDB.Driver;
using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class TransactionTests : IntegrationTestBase
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
        public TestDbContextSession(TestDbContext dbContext)
            : this(dbContext, startTransaction: true)
        {
        }

        public TestDbContextSession(TestDbContext dbContext, bool startTransaction)
            : base(dbContext, startTransaction)
        {
            Users = new MutableDbSet<User>(_dbContext.Users, () => Transaction, this, null, null, _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
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
    public async Task SaveChanges_ImplicitTransaction_InMemory()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        session.Users.Add(new User { Id = "1", Name = "Alice" });

        await session.SaveChangesAsync();

        var saved = await ctx.Users.QueryAsync(u => u.Id == "1");
        Assert.Single(saved);
    }

    [Fact]
    public async Task Transaction_Commits_Changes_On_SaveChanges()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        await using var session = new TestDbContextSession(ctx);

        session.Users.Add(new User { Id = "1", Name = "Alice" });

        await session.SaveChangesAsync();

        var saved = await ctx.Users.QueryAsync(u => u.Id == "1");
        Assert.Single(saved);
    }

    [Fact]
    public async Task Transaction_Abort_Rolls_Back_Writes()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        using var clientSession = Client.StartSession();
        clientSession.StartTransaction();

        var mutableSet = new MutableDbSet<User>(ctx.Users);
        mutableSet.Add(new User { Id = "1", Name = "Alice" });
        // Removing a non-existent entity with an invalid ID structure shouldn't cause a failure, but we want to simulate some "dirty" work
        mutableSet.Remove("non-existent-id");

        await ((IInternalMutableDbSet)mutableSet).CommitAsync(TransactionContext.FromSession(clientSession));
        await clientSession.AbortTransactionAsync();

        var saved = await ctx.Users.QueryAsync(u => u.Id == "1");
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Transaction_Query_Sees_Uncommitted_Writes()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        
        await using var session = new TestDbContextSession(ctx);
        session.Users.Add(new User { Id = "2", Name = "Bob" });

        // Force commit to the underlying collection within the transaction
        await ((IInternalMutableDbSet)session.Users).CommitAsync(session.Transaction);

        // Query inside the session (should see the change)
        var inside = await session.Users.QueryAsync(u => u.Id == "2");
        // Query outside (should not see it yet)
        var outside = await ctx.Users.QueryAsync(u => u.Id == "2");

        Assert.Single(inside);
        Assert.Empty(outside);

        await session.CommitTransactionAsync();

        var afterCommit = await ctx.Users.QueryAsync(u => u.Id == "2");
        Assert.Single(afterCommit);
    }

    [Fact]
    public async Task Dispose_Without_SaveChanges_Rolls_Back_Transaction()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));

        await using (var session = new TestDbContextSession(ctx))
        {
            session.Users.Add(new User { Id = "1", Name = "Alice" });
            await ((IInternalMutableDbSet)session.Users).CommitAsync(session.Transaction);
        }

        var saved = await ctx.Users.QueryAsync(u => u.Id == "1");
        Assert.Empty(saved);
    }
}

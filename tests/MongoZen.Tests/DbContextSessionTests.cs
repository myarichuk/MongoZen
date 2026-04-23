using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Tests;

public class DbContextSessionTests : IntegrationTestBase
{
    private class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = string.Empty;
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;
    }

    private sealed class TestDbContextSession(TestDbContext dbContext, bool startTransaction = true)
        : DbContextSession<TestDbContext>(dbContext, startTransaction)
    {
        public void ExposeEnsureTransactionActive() => EnsureTransactionActive();
    }

    [Fact]
    public async Task Constructor_StartsTransaction()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        Assert.True(session.Transaction.IsActive);
        Assert.True(session.Transaction.IsInMemoryTransaction);
    }

    [Fact]
    public async Task DisposeAsync_ClearsSession()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);

        await session.DisposeAsync();

        Assert.Null(session.ClientSession);
        Assert.False(session.Transaction.IsActive);
    }

    [Fact]
    public async Task AbortTransactionAsync_SetsIsActiveToFalse_ForInMemory()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        Assert.True(session.Transaction.IsActive);
        await session.AbortTransactionAsync();

        Assert.False(session.Transaction.IsActive);
    }

    [Fact]
    public async Task CommitTransactionAsync_SetsIsActiveToFalse_ForInMemory()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        Assert.True(session.Transaction.IsActive);
        await session.CommitTransactionAsync();

        Assert.False(session.Transaction.IsActive);
    }

    [Fact]
    public async Task EnsureTransactionActive_ThrowsAfterCommit()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        await session.CommitTransactionAsync();

        Assert.Throws<InvalidOperationException>(() => session.ExposeEnsureTransactionActive());
    }
}

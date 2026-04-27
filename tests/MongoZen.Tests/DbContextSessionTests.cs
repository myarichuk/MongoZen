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
    public async Task CommitTransactionAsync_RestartsTransaction_ForInMemory()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        Assert.True(session.Transaction.IsActive);
        await session.CommitTransactionAsync();

        // For in-memory, we currently just set _inMemoryTransaction = false.
        // It restarts on next EnsureTransactionActive() call.
        Assert.False(session.Transaction.IsActive);
        session.ExposeEnsureTransactionActive();
        Assert.True(session.Transaction.IsActive);
    }

    [Fact]
    public async Task EnsureTransactionActive_WorksAfterCommit()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        await using var session = new TestDbContextSession(ctx);

        await session.CommitTransactionAsync();

        // Should NOT throw anymore as we support session reuse
        session.ExposeEnsureTransactionActive();
        Assert.True(session.Transaction.IsActive);
    }

    [Fact]
    public void DefaultMethods_ThrowNotSupported()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };

        Assert.Throws<NotSupportedException>(() => session.Store(user));
        Assert.Throws<NotSupportedException>(() => session.Delete(user));
        Assert.Throws<NotSupportedException>(() => session.Delete<User>("123"));
    }

    [Fact]
    public void IdentityMap_TracksAndReturnsSameInstance()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };
        var id = "user/1";

        // Manual track (infrastructure use)
        var tracked = session.Track(user, id, (u, a) => IntPtr.Zero, (u, p) => false);
        
        Assert.Same(user, tracked);
        Assert.True(session.TryGetEntity<User>(id, out var found));
        Assert.Same(user, found);
    }

    [Fact]
    public void TrackDynamic_SupportsIncludes()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };
        var id = "user/1";

        // TrackDynamic is used when we don't have the shadow logic yet (e.g. Include)
        session.TrackDynamic(user, typeof(User), id);

        Assert.True(session.TryGetEntity<User>(id, out var found));
        Assert.Same(user, found);
    }

    [Fact]
    public void Untrack_RemovesFromMap()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };
        var id = "user/1";

        session.TrackDynamic(user, typeof(User), id);
        session.Untrack<User>(id);

        Assert.False(session.TryGetEntity<User>(id, out _));
    }

    [Fact]
    public void ClearTracking_ResetsState()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        session.TrackDynamic(new User(), typeof(User), "1");

        session.Advanced.ClearTracking();

        Assert.False(session.TryGetEntity<User>("1", out _));
    }

    [Fact]
    public void GetDirtyEntities_ReturnsEntitiesWithChanges()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };
        
        // Track with shadow pointer (simulated) and a differ that always returns true
        session.Track(user, "1", (u, a) => (IntPtr)1, (u, p) => true, forceShadow: true);

        var dirty = session.GetDirtyEntities<User>().ToList();
        Assert.Single(dirty);
        Assert.Same(user, dirty[0]);
    }

    [Fact]
    public void TryGetShadowPtr_ReturnsCorrectPtr()
    {
        var ctx = new TestDbContext(new DbContextOptions());
        var session = new TestDbContextSession(ctx);
        var user = new User { Name = "Oren" };
        IntPtr expectedPtr = unchecked((IntPtr)0xDEADBEEF);
        
        session.Track(user, "1", (u, a) => expectedPtr, (u, p) => false, forceShadow: true);

        Assert.True(session.TryGetShadowPtr<User>("1", out var shadowPtr));
        Assert.Equal(expectedPtr, (IntPtr)shadowPtr);
    }
}

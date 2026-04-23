using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class InMemoryTransactionTests
{
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }

    public partial class TestContext : DbContext
    {
        public TestContext(DbContextOptions options) : base(options) { }
        public IDbSet<User> Users { get; set; } = null!;
    }

    private sealed class TestContextSession : DbContextSession<TestContext>
    {
        public TestContextSession(TestContext dbContext) : base(dbContext)
        {
            Users = new MutableDbSet<User>(
                _dbContext.Users, 
                () => Transaction, 
                this, 
                conventions: _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public void Remove<TEntity>(object id) where TEntity : class
        {
            if (typeof(TEntity) == typeof(User)) Users.Remove(id);
        }

        public async ValueTask<TEntity?> LoadAsync<TEntity>(object id, System.Threading.CancellationToken cancellationToken = default) where TEntity : class
        {
            if (typeof(TEntity) == typeof(User)) return (TEntity?)(object?)await Users.LoadAsync(id, cancellationToken);
            return null;
        }

        public IMutableDbSet<TEntity> Include<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, object?>> path) where TEntity : class
        {
            if (typeof(TEntity) == typeof(User)) return (IMutableDbSet<TEntity>)(object)Users.Include((System.Linq.Expressions.Expression<Func<User, object?>>)(object)path);
            throw new ArgumentException();
        }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Users.Advanced.CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task InMemory_ChangesAreNotPersisted_UntilSaveChangesAsync()
    {
        var options = new DbContextOptions();
        options.UseInMemory = true;
        var ctx = new TestContext(options);

        var user = new User { Name = "Initial" };

        await using (var session = new TestContextSession(ctx))
        {
            session.Users.Add(user);
            
            // Should be findable in session via ID (if we implemented LoadAsync properly for session)
            // But let's check the underlying collection directly
            var inMemorySet = (InMemoryDbSet<User>)ctx.Users;
            Assert.Empty(inMemorySet.Collection);

            await session.SaveChangesAsync();
            Assert.Single(inMemorySet.Collection);
        }
    }

    [Fact]
    public async Task InMemory_AbortTransaction_DoesNotPersistChanges()
    {
        var options = new DbContextOptions();
        options.UseInMemory = true;
        var ctx = new TestContext(options);

        var user = new User { Name = "Aborted" };

        await using (var session = new TestContextSession(ctx))
        {
            session.Users.Add(user);
            await session.AbortTransactionAsync();
        }

        var inMemorySet = (InMemoryDbSet<User>)ctx.Users;
        Assert.Empty(inMemorySet.Collection);
    }
}

using MongoZen;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class InMemoryTransactionTests
{
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }

    public partial class TestContext(DbContextOptions options) : DbContext(options)
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

    private sealed class TestContextSession : DbContextSession<TestContext>
    {
        public static async Task<TestContextSession> OpenSessionAsync(TestContext dbContext)
        {
            var session = new TestContextSession(dbContext);
            await session.Advanced.InitializeAsync();
            return session;
        }

        private TestContextSession(TestContext dbContext) : base(dbContext)
        {
            var userSet = new MutableDbSet<User>(
                _dbContext.Users,
                () => Transaction,
                this,
                extractor: null,
                conventions: _dbContext.Options.Conventions);            Users = userSet;
            RegisterDbSet(userSet);
        }

        public IMutableDbSet<User> Users { get; }

        public override void Store<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is User u) Users.Add(u);
        }

        public override void Delete<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is User u) Users.Remove(u);
        }

        public override void Delete<TEntity>(object id) where TEntity : class
        {
            if (typeof(TEntity) == typeof(User)) Users.Remove(id);
        }

        public override void Delete<TEntity>(in DocId id) where TEntity : class
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

        public override async Task SaveChangesAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            await EnsureTransactionActiveAsync(cancellationToken);
            await ((IInternalMutableDbSet)Users).CommitAsync(Transaction, cancellationToken);
            await CommitTransactionAsync();
            ClearTracking();
        }
    }

    [Fact]
    public async Task InMemory_ChangesAreNotPersisted_UntilSaveChangesAsync()
    {
        var options = new DbContextOptions { UseInMemory = true };
        var ctx = new TestContext(options);

        var user = new User { Name = "Initial" };

        await using (var session = await TestContextSession.OpenSessionAsync(ctx))
        {
            session.Users.Add(user);
            
            var inMemorySet = (InMemoryDbSet<User>)ctx.Users;
            var results = await inMemorySet.QueryAsync(u => true);
            Assert.Empty(results);

            await session.SaveChangesAsync();
            results = await inMemorySet.QueryAsync(u => true);
            Assert.Single(results);
        }
    }

    [Fact]
    public async Task InMemory_AbortTransaction_DoesNotPersistChanges()
    {
        var options = new DbContextOptions { UseInMemory = true };
        var ctx = new TestContext(options);

        var user = new User { Name = "Aborted" };

        await using (var session = await TestContextSession.OpenSessionAsync(ctx))
        {
            session.Users.Add(user);
            await session.AbortTransactionAsync();
        }

        var inMemorySet = (InMemoryDbSet<User>)ctx.Users;
        var results = await inMemorySet.QueryAsync(u => true);
        Assert.Empty(results);
    }
}

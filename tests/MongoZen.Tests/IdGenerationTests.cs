using MongoZen;
using Xunit;

namespace MongoZen.Tests;

public class IdGenerationTests : IntegrationTestBase
{
    public class User
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private partial class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;
    }

    private sealed class TestDbContextSession : DbContextSession<TestDbContext>
    {
        public TestDbContextSession(TestDbContext dbContext) : base(dbContext)
        {
            Users = new MutableDbSet<User>(_dbContext.Users, () => Transaction, this, (e, a) => IntPtr.Zero, (e, p) => true, _dbContext.Options.Conventions);
        }

        public IMutableDbSet<User> Users { get; }

        public async ValueTask SaveChangesAsync()
        {
            EnsureTransactionActive();
            await Users.Advanced.CommitAsync(Transaction);
            await CommitTransactionAsync();
        }
    }

    [Fact]
    public void DefaultIdConvention_CanResolveIdOnUser()
    {
        var convention = new DefaultIdConvention();
        var prop = convention.ResolveIdProperty<User>();
        Assert.NotNull(prop);
        Assert.Equal("Id", prop.Name);
    }

    [Fact]
    public void PrefixedStringIdGenerator_CanAssignId()
    {
        var generator = new PrefixedStringIdGenerator();
        var user = new User { Name = "Test" };
        generator.AssignId(user, "Users", new DefaultIdConvention());
        Assert.NotNull(user.Id);
        Assert.StartsWith("Users/", user.Id);
    }

    [Fact]
    public async Task Add_AutomaticallyAssignsPrefixedId()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        
        await using (var session = new TestDbContextSession(ctx))
        {
            var user = new User { Name = "Raven" };
            session.Users.Add(user);

            Assert.NotNull(user.Id);
            Assert.StartsWith("Users/", user.Id);
            
            await session.SaveChangesAsync();
        }

        // Verify persistence
        var saved = await ctx.Users.QueryAsync(u => true);
        Assert.Single(saved);
        Assert.StartsWith("Users/", saved.First().Id);
    }
}

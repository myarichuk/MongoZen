namespace Library.Tests;

public class MutableDbSetTests: IntegrationTestBase
{
    private class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public int Age { get; set; }
    }

    private class TestDbContext : DbContext
    {
        public IDbSet<User> Users { get; set; }

        public TestDbContext(DbContextOptions options) : base(options)
        {
        }
    }

    [Fact]
    public async Task Can_Add_And_Save_Changes_InMemory()
    {
        var inner = new InMemoryDbSet<User>();
        var mutableSet = new MutableDbSet<User>(inner);

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        await mutableSet.CommitAsync();

        var result = await mutableSet.QueryAsync(u => u.Name == "Charlie");

        Assert.Single(result);
        Assert.Equal("Charlie", result.First().Name);
    }

    [Fact]
    public async Task Can_Add_Update_Remove_InMemory()
    {
        var inner = new InMemoryDbSet<User>();
        inner.Add(new User { Id = "1", Name = "Alice", Age = 30 });
        inner.Add(new User { Id = "2", Name = "Bob", Age = 40 });

        var mutableSet = new MutableDbSet<User>(inner);

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        mutableSet.Update(new User { Id = "2", Name = "Bob", Age = 99 });
        mutableSet.Remove(new User { Id = "1" });

        await mutableSet.CommitAsync();

        var all = await mutableSet.QueryAsync(u => true);

        Assert.Equal(2, all.Count());
        Assert.Contains(all, u => u is { Id: "2", Age: 99 });
        Assert.Contains(all, u => u.Id == "3");
        Assert.DoesNotContain(all, u => u.Id == "1");
    }

    [Fact]
    public async Task Can_Add_And_Save_Changes_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database));
        var mutableSet = new MutableDbSet<User>(ctx.Users);

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        await mutableSet.CommitAsync();

        var result = await ctx.Users.QueryAsync(u => u.Name == "Charlie");

        Assert.Single(result);
        Assert.Equal("Charlie", result.First().Name);
    }

    [Fact]
    public async Task Can_Add_Update_Remove_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database));
        var baseSet = (DbSet<User>)ctx.Users;

        await baseSet.Collection.InsertManyAsync(new[]
        {
            new User { Id = "1", Name = "Alice", Age = 30 },
            new User { Id = "2", Name = "Bob", Age = 40 }
        });

        var mutableSet = new MutableDbSet<User>(ctx.Users);

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        mutableSet.Update(new User { Id = "2", Name = "Bob", Age = 99 });
        mutableSet.Remove(new User { Id = "1" });

        await mutableSet.CommitAsync();

        var all = await ctx.Users.QueryAsync(u => true);

        Assert.Equal(2, all.Count());
        Assert.Contains(all, u => u is { Id: "2", Age: 99 });
        Assert.Contains(all, u => u.Id == "3");
        Assert.DoesNotContain(all, u => u.Id == "1");
    }
}
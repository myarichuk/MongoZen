using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Library.Tests;

public class DbContextQueryTests : IntegrationTestBase
{
    private class User
    {
        [BsonId]
        public string Id { get; set; }

        public string? Name { get; set; }

        public int Age { get; set; }
    }

    private class TestDbContext : DbContext
    {
        public IDbSet<User> Users { get; set; }

        public TestDbContext(DbContextOptions options) : base(options)
        {
        }
    }

    private async Task InsertTestUsersAsync()
    {
        var collection = Database.GetCollection<User>("Users");
        await collection.DeleteManyAsync(FilterDefinition<User>.Empty); // clean slate
        await collection.InsertManyAsync([
            new User { Id = "1", Name = "Alice", Age = 30 },
            new User { Id = "2", Name = "Bob", Age = 40 }
        ]);
    }

    private static void InsertInMemoryTestUsers(TestDbContext ctx)
    {
        ((InMemoryDbSet<User>)ctx.Users).Collection.Add(new User { Id = "1", Name = "Alice", Age = 30 });
        ((InMemoryDbSet<User>)ctx.Users).Collection.Add(new User { Id = "2", Name = "Bob", Age = 40 });
    }

    [Fact]
    public async Task Can_Query_DB_WithLinq()
    {
        await InsertTestUsersAsync();

        using var ctx = new TestDbContext(new DbContextOptions(Database));

        var result = await ctx.Users.QueryAsync(u => u.Age > 35);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }

    [Fact]
    public async Task Can_Query_InMemory_WithLinq()
    {
        using var ctx = new TestDbContext(new DbContextOptions());

        InsertInMemoryTestUsers(ctx);

        var result = await ctx.Users.QueryAsync(u => u.Age > 35);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }

    [Fact]
    public async Task Can_Query_DB_WithFilter()
    {
        await InsertTestUsersAsync();

        using var ctx = new TestDbContext(new DbContextOptions(Database));

        var filter = Builders<User>.Filter.Gt(u => u.Age, 35);
        var result = await ctx.Users.QueryAsync(filter);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }

    [Fact]
    public async Task Can_Query_InMemory_WithFilter()
    {
        using var ctx = new TestDbContext(new DbContextOptions());

        InsertInMemoryTestUsers(ctx);

        var filter = Builders<User>.Filter.Gt(u => u.Age, 35);
        var result = await ctx.Users.QueryAsync(filter);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }

    [Fact]
    public async Task Can_Remove_DB_ById()
    {
        await InsertTestUsersAsync();

        using var ctx = new TestDbContext(new DbContextOptions(Database));

        var dbSet = (DbSet<User>)ctx.Users;
        await dbSet.RemoveById("1");

        var result = await ctx.Users.QueryAsync(u => true);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }
}

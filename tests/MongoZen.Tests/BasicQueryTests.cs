using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Tests;

public class BasicQueryTests : IntegrationTestBase
{
    private class User
    {
        [BsonId]
        public string Id { get; set; } = null!;

        public string? Name { get; set; }

        public int Age { get; set; }
    }

    private class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public IDbSet<User> Users { get; set; } = null!;
    }

    private async Task<IEnumerable<User>> InsertTestUsersAsync()
    {
        User[] data =
        [
            new User { Id = "1", Name = "Alice", Age = 30 },
            new User { Id = "2", Name = "Bob", Age = 40 },
        ];

        var collection = Database!.GetCollection<User>("Users");
        await collection.DeleteManyAsync(FilterDefinition<User>.Empty); // clean slate
        await collection.InsertManyAsync(data);
        return data;
    }

    private static void InsertInMemoryTestUsers(TestDbContext ctx)
    {
        ((InMemoryDbSet<User>)ctx.Users).Seed(new User { Id = "1", Name = "Alice", Age = 30 });
        ((InMemoryDbSet<User>)ctx.Users).Seed(new User { Id = "2", Name = "Bob", Age = 40 });
    }

    [Fact]
    public async Task Can_Query_DB_WithLinq()
    {
        await InsertTestUsersAsync();

        using var ctx = new TestDbContext(new DbContextOptions(Database!));

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

        using var ctx = new TestDbContext(new DbContextOptions(Database!));

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
        var data = (await InsertTestUsersAsync()).ToArray();

        using var ctx = new TestDbContext(new DbContextOptions(Database!));

        var dbSet = (DbSet<User>)ctx.Users;
        await dbSet.Remove(entity: data[0]);

        var result = await ctx.Users.QueryAsync(u => true);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }
}

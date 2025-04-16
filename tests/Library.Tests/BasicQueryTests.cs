using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Xunit;

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
        public IDbSet<User> Users { get; }

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

    [Fact]
    public async Task CanInsertAndQueryUser()
    {
        await InsertTestUsersAsync();

        using var ctx = new TestDbContext(new DbContextOptions(Database));

        var result = await ctx.Users.QueryAsync(u => u.Age > 35);

        Assert.Single(result);
        Assert.Equal("Bob", result.First().Name);
    }
}
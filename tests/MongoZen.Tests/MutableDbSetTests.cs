using MongoDB.Driver;
using MongoZen;

namespace MongoZen.Tests;

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
        await mutableSet.CommitAsync(TransactionContext.InMemory());

        var result = await mutableSet.QueryAsync(u => u.Name == "Charlie");

        Assert.Single(result);
        Assert.Equal("Charlie", result.First().Name);
    }

    [Fact]
    public async Task Can_Add_Update_Remove_InMemory()
    {
        var inner = new InMemoryDbSet<User>();
        inner.Collection.Add(new User { Id = "1", Name = "Alice", Age = 30 });
        inner.Collection.Add(new User { Id = "2", Name = "Bob", Age = 40 });

        var mutableSet = new MutableDbSet<User>(inner);

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        mutableSet.Update(new User { Id = "2", Name = "Bob", Age = 99 });
        mutableSet.Remove(new User { Id = "1" });

        await mutableSet.CommitAsync(TransactionContext.InMemory());

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
        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

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

        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

        var all = await ctx.Users.QueryAsync(u => true);

        Assert.Equal(2, all.Count());
        Assert.Contains(all, u => u is { Id: "2", Age: 99 });
        Assert.Contains(all, u => u.Id == "3");
        Assert.DoesNotContain(all, u => u.Id == "1");
    }

    [Fact]
    public async Task Can_Handle_Multiple_Adds_With_Same_Id()
    {
        var inner = new InMemoryDbSet<User>();
        var mutableSet = new MutableDbSet<User>(inner);

        var user = new User { Id = "1", Name = "Original", Age = 20 };
        mutableSet.Add(user);
        mutableSet.Add(new User { Id = "1", Name = "Overwritten", Age = 30 }); // same ID

        await mutableSet.CommitAsync(TransactionContext.InMemory());

        var result = await inner.QueryAsync(u => true);
        Assert.Single(result);
        Assert.Equal("Overwritten", result.First().Name);
    }

    [Fact]
    public async Task Can_Handle_Multiple_Adds_With_Same_Id_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database));
        var baseSet = (DbSet<User>)ctx.Users;

        await baseSet.Collection.DeleteManyAsync(FilterDefinition<User>.Empty); // just in case

        var mutableSet = new MutableDbSet<User>(ctx.Users);
        mutableSet.Add(new User { Id = "1", Name = "Original", Age = 20 });
        mutableSet.Add(new User { Id = "1", Name = "Overwritten", Age = 30 });

        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

        var result = await ctx.Users.QueryAsync(u => true);
        Assert.Single(result);
        Assert.Equal("Overwritten", result.First().Name);
    }

    [Fact]
    public async Task Update_Before_Add_Should_Apply_Add()
    {
        var inner = new InMemoryDbSet<User>();
        var mutableSet = new MutableDbSet<User>(inner);

        var user = new User { Id = "1", Name = "Newbie", Age = 25 };
        mutableSet.Update(user); // update first
        mutableSet.Add(user);    // then add

        await mutableSet.CommitAsync(TransactionContext.InMemory());

        var result = await inner.QueryAsync(u => true);
        Assert.Single(result);
        Assert.Equal("Newbie", result.First().Name);
    }

    [Fact]
    public async Task Update_Before_Add_Should_Apply_Add_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database));
        var baseSet = (DbSet<User>)ctx.Users;

        await baseSet.Collection.DeleteManyAsync(FilterDefinition<User>.Empty);

        var mutableSet = new MutableDbSet<User>(ctx.Users);
        var user = new User { Id = "1", Name = "Newbie", Age = 25 };
        mutableSet.Update(user);
        mutableSet.Add(user);

        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

        var result = await ctx.Users.QueryAsync(u => true);
        Assert.Single(result);
        Assert.Equal("Newbie", result.First().Name);
    }

    [Fact]
    public async Task Remove_NonExistent_Should_Not_Throw()
    {
        var inner = new InMemoryDbSet<User>();
        var mutableSet = new MutableDbSet<User>(inner);

        mutableSet.Remove(new User { Id = "non-existent" });

        await mutableSet.CommitAsync(TransactionContext.InMemory());

        var result = await inner.QueryAsync(u => true);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Remove_NonExistent_Should_Not_Throw_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database!));
        var baseSet = (DbSet<User>)ctx.Users;

        await baseSet.Collection.DeleteManyAsync(FilterDefinition<User>.Empty);

        var mutableSet = new MutableDbSet<User>(ctx.Users);
        mutableSet.Remove(new User { Id = "non-existent" });

        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

        var result = await ctx.Users.QueryAsync(u => true);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Can_Handle_Mixed_Mutations()
    {
        var inner = new InMemoryDbSet<User>();
        inner.Collection.Add(new User { Id = "1", Name = "A", Age = 10 });
        inner.Collection.Add(new User { Id = "2", Name = "B", Age = 20 });

        var mutableSet = new MutableDbSet<User>(inner);
        mutableSet.Add(new User { Id = "3", Name = "C", Age = 30 });
        mutableSet.Remove(new User { Id = "1" });
        mutableSet.Update(new User { Id = "2", Name = "B updated", Age = 99 });

        await mutableSet.CommitAsync(TransactionContext.InMemory());

        var all = await inner.QueryAsync(u => true);
        Assert.Equal(2, all.Count());
        Assert.DoesNotContain(all, u => u.Id == "1");
        Assert.Contains(all, u => u.Id == "2" && u.Age == 99);
        Assert.Contains(all, u => u.Id == "3");
    }

    [Fact]
    public async Task Can_Handle_Mixed_Mutations_DB()
    {
        var ctx = new TestDbContext(new DbContextOptions(Database));
        var baseSet = (DbSet<User>)ctx.Users;

        await baseSet.Collection.DeleteManyAsync(FilterDefinition<User>.Empty);

        await baseSet.Collection.InsertManyAsync(new[]
        {
            new User { Id = "1", Name = "A", Age = 10 },
            new User { Id = "2", Name = "B", Age = 20 }
        });

        var mutableSet = new MutableDbSet<User>(ctx.Users);
        mutableSet.Add(new User { Id = "3", Name = "C", Age = 30 });
        mutableSet.Remove(new User { Id = "1" });
        mutableSet.Update(new User { Id = "2", Name = "B updated", Age = 99 });

        using var session = Client.StartSession();
        session.StartTransaction();
        await mutableSet.CommitAsync(TransactionContext.FromSession(session));
        await session.CommitTransactionAsync();

        var all = await ctx.Users.QueryAsync(u => true);
        Assert.Equal(2, all.Count());
        Assert.DoesNotContain(all, u => u.Id == "1");
        Assert.Contains(all, u => u.Id == "2" && u.Age == 99);
        Assert.Contains(all, u => u.Id == "3");
    }
}

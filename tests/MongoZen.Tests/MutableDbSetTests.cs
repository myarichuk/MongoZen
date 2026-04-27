using System.Linq.Expressions;
using MongoDB.Driver;
using MongoZen;
using SharpArena.Allocators;
using Xunit;

namespace MongoZen.Tests;

public class MutableDbSetTests
{
    public class User
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private static IIdConvention Convention => new DefaultIdConvention();

    [Fact]
    public async Task Can_Add_And_Save_Changes_InMemory()
    {
        var inner = new InMemoryDbSet<User>("Users", new Conventions { IdConvention = Convention });
        var mutableSet = new MutableDbSet<User>(inner, null!, null!, null, null, null, new Conventions { IdConvention = Convention });

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        await ((IInternalMutableDbSet)mutableSet).CommitAsync(TransactionContext.InMemory());

        var result = await mutableSet.QueryAsync(u => u.Name == "Charlie");

        Assert.Single(result);
        Assert.Equal("Charlie", result.First().Name);
    }

    [Fact]
    public async Task Can_Add_Update_Remove_InMemory()
    {
        var inner = new InMemoryDbSet<User>("Users", new Conventions { IdConvention = Convention });
        inner.Seed(new User { Id = "1", Name = "Alice", Age = 30 });
        inner.Seed(new User { Id = "2", Name = "Bob", Age = 40 });

        var mutableSet = new MutableDbSet<User>(inner, null!, null!, null, null, null, new Conventions { IdConvention = Convention });

        mutableSet.Add(new User { Id = "3", Name = "Charlie", Age = 28 });
        mutableSet.Add(new User { Id = "2", Name = "Bob", Age = 99 });
        mutableSet.Remove(new User { Id = "1" });

        await ((IInternalMutableDbSet)mutableSet).CommitAsync(TransactionContext.InMemory());

        var all = (await mutableSet.QueryAsync(u => true)).ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, u => u.Id == "3");
        Assert.Contains(all, u => u.Id == "2" && u.Age == 99);
        Assert.DoesNotContain(all, u => u.Id == "1");
    }

    [Fact]
    public async Task GetAdded_Returns_Pending_Additions()
    {
        var inner = new InMemoryDbSet<User>("Users", new Conventions { IdConvention = Convention });
        var mutableSet = new MutableDbSet<User>(inner, null!, null!, null, null, null, new Conventions { IdConvention = Convention });

        var user = new User { Id = "1", Name = "Alice" };
        mutableSet.Add(user);

        var added = mutableSet.Advanced.GetAdded();
        Assert.Single(added);
        Assert.Same(user, added.First());
    }

    [Fact]
    public async Task Commit_Clears_Pending_Changes()
    {
        var inner = new InMemoryDbSet<User>("Users", new Conventions { IdConvention = Convention });
        var mutableSet = new MutableDbSet<User>(inner, null!, null!, null, null, null, new Conventions { IdConvention = Convention });

        mutableSet.Add(new User { Id = "1", Name = "Alice" });
        await ((IInternalMutableDbSet)mutableSet).CommitAsync(TransactionContext.InMemory());
        mutableSet.Advanced.ClearTracking();

        Assert.Empty(mutableSet.Advanced.GetAdded());
    }

    [Fact]
    public async Task Can_Remove_By_Id_InMemory()
    {
        var inner = new InMemoryDbSet<User>("Users", new Conventions { IdConvention = Convention });
        inner.Seed(new User { Id = "1", Name = "Alice" });

        var mutableSet = new MutableDbSet<User>(inner, null!, null!, null, null, null, new Conventions { IdConvention = Convention });

        mutableSet.Remove("1");
        await ((IInternalMutableDbSet)mutableSet).CommitAsync(TransactionContext.InMemory());

        var result = await mutableSet.QueryAsync(u => true);
        Assert.Empty(result);
    }
}

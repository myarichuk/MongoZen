using Xunit;

namespace MongoZen.Tests;

/// <summary>
/// The identity map is keyed by (collection, id), with the id normalized to the entity's id type.
/// The audit found it keyed by the raw boxed id alone, so two types sharing an id value collided and an
/// int lookup for a long id produced a second tracked instance.
/// </summary>
public class IdentityMapTests : IntegrationTestBase
{
    public class OrderDoc { public int Id { get; set; } public string? Note { get; set; } }
    public class CustomerDoc { public int Id { get; set; } public string? Name { get; set; } }
    public class LongIdDoc { public long Id { get; set; } public string? Label { get; set; } }

    [Fact]
    public async Task Same_Id_In_Different_Collections_Does_Not_Collide()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using (var setup = store.OpenSession())
        {
            setup.Store(new OrderDoc { Id = 7, Note = "order" });
            setup.Store(new CustomerDoc { Id = 7, Name = "customer" });
            await setup.SaveChangesAsync();
        }

        using var session = store.OpenSession();
        var order = await session.LoadAsync<OrderDoc>(7);
        var customer = await session.LoadAsync<CustomerDoc>(7); // used to throw InvalidCastException
        Assert.Equal("order", order!.Note);
        Assert.Equal("customer", customer!.Name);
    }

    [Fact]
    public async Task Numeric_Id_Lookup_Is_Normalized_To_The_Entity_Id_Type()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using (var setup = store.OpenSession())
        {
            setup.Store(new LongIdDoc { Id = 4, Label = "a" });
            await setup.SaveChangesAsync();
        }

        using var session = store.OpenSession();
        var viaInt = await session.LoadAsync<LongIdDoc>(4);
        var viaLong = await session.LoadAsync<LongIdDoc>(4L);
        Assert.NotNull(viaInt);
        Assert.Same(viaInt, viaLong); // one tracked instance, not two

        viaInt!.Label = "edited";
        await session.SaveChangesAsync();

        using var verify = store.OpenSession();
        Assert.Equal("edited", (await verify.LoadAsync<LongIdDoc>(4))!.Label);
    }

    [Fact]
    public async Task Storing_A_Different_Instance_Under_A_Tracked_Id_Throws()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();
        var first = new OrderDoc { Id = 10, Note = "first" };
        var second = new OrderDoc { Id = 10, Note = "second" };

        session.Store(first);
        session.Store(first); // same instance: no-op
        var ex = Assert.Throws<InvalidOperationException>(() => session.Store(second)); // used to be silently dropped
        Assert.Contains("different object", ex.Message);
        Assert.Contains("10", ex.Message);

        await session.SaveChangesAsync();
        using var verify = store.OpenSession();
        Assert.Equal("first", (await verify.LoadAsync<OrderDoc>(10))!.Note);
    }

    [Fact]
    public async Task Store_Then_Delete_Before_Save_Purges_The_Identity_Map()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();
        var doc = new OrderDoc { Id = 11, Note = "ephemeral" };
        session.Store(doc);
        session.Delete(doc);
        await session.SaveChangesAsync();

        Assert.Null(await session.LoadAsync<OrderDoc>(11)); // used to return the deleted instance from the map
    }

    [Fact]
    public async Task Evict_Allows_Storing_A_Replacement_Instance()
    {
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();
        var first = new OrderDoc { Id = 12, Note = "first" };
        session.Store(first);
        session.Advanced.Evict(first);
        session.Store(new OrderDoc { Id = 12, Note = "replacement" });
        await session.SaveChangesAsync();

        using var verify = store.OpenSession();
        Assert.Equal("replacement", (await verify.LoadAsync<OrderDoc>(12))!.Note);
    }
}

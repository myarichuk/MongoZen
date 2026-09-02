using MongoZen.ChangeTracking;
using Xunit;
using Xunit.Sdk;

namespace MongoZen.Tests;

/// <summary>
/// Pure unit tests for <see cref="DocumentSession.ComputeGroupCount"/> — deliberately not an
/// <see cref="IntegrationTestBase"/> subclass so they run without Docker/Testcontainers.
/// </summary>
public class ComputeGroupCountTests
{
    [Fact]
    public void Empty_Buffer_Returns_Zero()
    {
        var buffer = Array.Empty<PendingOperation>();
        Assert.Equal(0, DocumentSession.ComputeGroupCount(buffer, 0));
    }

    [Fact]
    public void Single_Collection_Single_Type_Returns_One()
    {
        var buffer = new PendingOperation[]
        {
            new() { Type = OperationType.Insert, CollectionId = 1 },
            new() { Type = OperationType.Insert, CollectionId = 1 },
        };
        Assert.Equal(1, DocumentSession.ComputeGroupCount(buffer, buffer.Length));
    }

    [Fact]
    public void Different_CollectionIds_Counted_As_Separate_Groups()
    {
        var buffer = new PendingOperation[]
        {
            new() { Type = OperationType.Insert, CollectionId = 1 },
            new() { Type = OperationType.Insert, CollectionId = 2 },
        };
        Assert.Equal(2, DocumentSession.ComputeGroupCount(buffer, buffer.Length));
    }

    [Fact]
    public void Mixed_Insert_And_Update_In_Same_Collection_Counted_As_Separate_Groups()
    {
        // Mirrors SaveChangesAsync: buffer is pre-sorted by (CollectionId, Type) before this is called.
        var buffer = new PendingOperation[]
        {
            new() { Type = OperationType.Insert, CollectionId = 1 },
            new() { Type = OperationType.Update, CollectionId = 1 },
        };
        Assert.Equal(2, DocumentSession.ComputeGroupCount(buffer, buffer.Length));
    }
}

public class TransactionRequirementTests : TestcontainersIntegrationTestBase
{
    [SkippableFact]
    public async Task SaveChangesAsync_MultiGroup_Without_RequireTransactions_Succeeds_Without_Transaction()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using var session = store.OpenSession();

        session.Store(new SimpleEntity { Id = 1, Name = "A", Age = 1 });
        session.Store(new AdvancedUser { Name = "B", Age = 2 });

        Assert.False(session.Advanced.IsTransactional);
        await session.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task SaveChangesAsync_MultiGroup_With_RequireTransactions_Persists_Both_Groups()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var conventions = new DocumentConventions { RequireTransactions = true };
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName, conventions);
        using var session = store.OpenSession();

        Assert.False(session.Advanced.IsTransactional);

        session.Store(new SimpleEntity { Id = 2, Name = "A", Age = 1 });
        var user = new AdvancedUser { Name = "B", Age = 2 };
        session.Store(user);

        // Should not throw TransactionRequirementException: the Testcontainers replica set
        // supports transactions, so the multi-group requirement is satisfiable.
        await session.SaveChangesAsync();

        // The transaction is committed by the time SaveChangesAsync returns, so IsTransactional
        // is false again here — persistence of both groups is the observable proof it ran.
        Assert.False(session.Advanced.IsTransactional);

        using var verifySession = store.OpenSession();
        Assert.NotNull(await verifySession.LoadAsync<SimpleEntity>(2));
        Assert.NotNull(await verifySession.LoadAsync<AdvancedUser>(user.Id));
    }
}

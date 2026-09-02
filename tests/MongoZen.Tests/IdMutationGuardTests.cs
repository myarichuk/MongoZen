using Xunit;
using Xunit.Sdk;

namespace MongoZen.Tests;

public class IdMutationGuardTests : TestcontainersIntegrationTestBase
{
    [SkippableFact]
    public async Task SaveChangesAsync_Should_Throw_When_Id_Mutated_After_Store()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);

        using (var session = store.OpenSession())
        {
            session.Store(new SimpleEntity { Id = 500, Name = "Original", Age = 1 });
            await session.SaveChangesAsync();
        }

        using var loadSession = store.OpenSession();
        var entity = await loadSession.LoadAsync<SimpleEntity>(500);
        Assert.NotNull(entity);

        entity!.Id = 999;
        entity.Name = "Changed";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loadSession.SaveChangesAsync());
        Assert.Contains("Id", ex.Message);
        Assert.Contains("Mutating", ex.Message);
    }

    [SkippableFact]
    public async Task SaveChangesAsync_Should_Throw_When_HiLoGenerated_Id_Mutated_In_Fresh_Session()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        var store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        string generatedId;

        using (var session = store.OpenSession())
        {
            var entity = new HiLoGuardedEntity { Name = "Original" };
            session.Store(entity);
            generatedId = entity.Id;
            Assert.False(string.IsNullOrEmpty(generatedId));
            await session.SaveChangesAsync();
        }

        using var freshSession = store.OpenSession();
        var loaded = await freshSession.LoadAsync<HiLoGuardedEntity>(generatedId);
        Assert.NotNull(loaded);

        loaded!.Id = "some-other-id";
        loaded.Name = "Changed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => freshSession.SaveChangesAsync());
    }
}

[Document]
public partial class HiLoGuardedEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

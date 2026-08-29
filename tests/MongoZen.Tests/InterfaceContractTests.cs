using System.Linq.Expressions;
using MongoDB.Driver;
using Xunit;

namespace MongoZen.Tests;

public class InterfaceContractTests : IntegrationTestBase
{
    [Fact]
    public void DocumentSession_Should_Implement_IDocumentSession()
    {
        Assert.Contains(typeof(IDocumentSession), typeof(DocumentSession).GetInterfaces());
    }

    [Fact]
    public void DocumentStore_Should_Implement_IDocumentStore()
    {
        Assert.Contains(typeof(IDocumentStore), typeof(DocumentStore).GetInterfaces());
    }

    [Fact]
    public async Task OpenSession_Through_Interface_Should_Work_End_To_End()
    {
        IDocumentStore store = new DocumentStore(Client, Database.DatabaseNamespace.DatabaseName);
        using IDocumentSession session = store.OpenSession();

        var entity = new SimpleEntity { Id = 777, Name = "ViaInterface", Age = 5 };
        session.Store(entity);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<SimpleEntity>(777);
        Assert.NotNull(loaded);
        Assert.Equal("ViaInterface", loaded!.Name);
    }

    [Fact]
    public void Fake_IDocumentSession_Should_Compile_And_Be_Usable()
    {
        IDocumentSession fake = new FakeDocumentSession();
        fake.Store(new SimpleEntity { Id = 1 });
        Assert.NotNull(fake);
    }

    private sealed class FakeDocumentSession : IDocumentSession
    {
        public IAttachmentsSessionOperations Attachments => throw new NotImplementedException();
        public ISessionAdvancedOperations Advanced => throw new NotImplementedException();

        public ValueTask<T?> LoadAsync<T>(object id, CancellationToken ct = default) => throw new NotImplementedException();
        public void Store<T>(T entity) { }
        public void Delete<T>(T entity) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IReadOnlyList<T>> QueryAsync<T>(FilterDefinition<T> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<T>> QueryAsync<T>(Expression<Func<T, bool>> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<TResult>> AggregateAsync<T, TResult>(PipelineDefinition<T, TResult> pipeline, AggregateOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<long> CountAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<long> CountAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> AnyAsync<T>(FilterDefinition<T> filter, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> AnyAsync<T>(Expression<Func<T, bool>> filter, CancellationToken ct = default) => throw new NotImplementedException();

        public IAsyncEnumerable<T> StreamAsync<T>(FilterDefinition<T> filter, SortDefinition<T>? sort = null, int? skip = null, int? limit = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public void Dispose() { }
    }
}

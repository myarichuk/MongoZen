using System.Collections;
using System.Linq.Expressions;
using MongoDB.Driver;

namespace MongoZen;

/// <summary>
/// A test-friendly implementation of IDbSet that stores entities in a local dictionary.
/// Does NOT perform deep cloning by default — changes to entities are immediate.
/// </summary>
public class InMemoryDbSet<T> : IDbSet<T>, IInternalDbSet<T> where T : class
{
    private readonly Dictionary<object, T> _data = new();
    private readonly Func<T, object?> _idAccessor;
    private readonly string _idFieldName;

    public string CollectionName { get; }

    internal IMongoCollection<T> Collection => null!; // For tests that check the collection namespace/name

    // Helper for seeding tests
    public void Seed(T entity)
    {
        var id = _idAccessor(entity) ?? throw new InvalidOperationException("Entity has no ID.");
        _data[id] = entity;
    }

    public InMemoryDbSet(string collectionName, Conventions conventions)
    {
        CollectionName = collectionName;
        _idAccessor = EntityIdAccessor<T>.GetAccessor(conventions.IdConvention);
        _idFieldName = conventions.IdConvention.ResolveIdProperty<T>()?.Name ?? "_id";
    }

    /// <summary>
    /// Reflection-friendly constructor used by <see cref="DbContext.InitializeDbSets"/>.
    /// </summary>
    public InMemoryDbSet(IEnumerable<T> items, Conventions conventions, string collectionName)
        : this(collectionName, conventions)
    {
        foreach (var item in items)
        {
            Seed(item);
        }
    }

    public InMemoryDbSet() : this("Unknown", new Conventions()) { }

    public ValueTask<T?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        _data.TryGetValue(id, out var entity);
        return new ValueTask<T?>(entity);
    }

    public IDbSet<T> Include(Expression<Func<T, object?>> path) => this;
    public IDbSet<T> Include<TInclude>(Expression<Func<T, object?>> path) where TInclude : class => this;

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default)
    {
        var translator = new FilterUtils.FilterToLinqTranslator<T>();
        var predicate = translator.GetCompiled(filter);
        return new ValueTask<IEnumerable<T>>(_data.Values.Where(predicate).ToList());
    }

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default)
    {
        var predicate = filter.Compile();
        return new ValueTask<IEnumerable<T>>(_data.Values.Where(predicate).ToList());
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    public Task Remove(T entity)
    {
        var id = _idAccessor(entity) ?? throw new InvalidOperationException("Entity has no ID.");
        _data.Remove(id);
        return Task.CompletedTask;
    }

    async ValueTask IInternalDbSet<T>.CommitAsync(
        IEnumerable<T> added, 
        IEnumerable<T> removed, 
        IEnumerable<object> removedIds, 
        IEnumerable<T> updated, 
        IEnumerable<T> dirty, 
        Dictionary<DocId, T> upsertBuffer,
        HashSet<DocId> dedupeBuffer,
        HashSet<object> rawIdBuffer,
        List<WriteModel<T>> modelBuffer,
        TransactionContext transaction, 
        CancellationToken cancellationToken)
    {
        // 1. Removals
        foreach (var entity in removed)
        {
            var id = _idAccessor(entity);
            if (id != null) _data.Remove(id);
        }
        foreach (var id in removedIds)
        {
            if (id != null) _data.Remove(id);
        }

        // 2. Added
        foreach (var entity in added)
        {
            var id = _idAccessor(entity);
            if (id != null) _data[id] = entity;
        }

        // 3. Updated/Dirty
        foreach (var entity in updated)
        {
            var id = _idAccessor(entity);
            if (id != null) _data[id] = entity;
        }
        foreach (var entity in dirty)
        {
            var id = _idAccessor(entity);
            if (id != null) _data[id] = entity;
        }

        await Task.Yield();
    }
}

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
    private readonly Dictionary<object, long> _versions = new();
    private readonly Func<T, object?> _idAccessor;
    private readonly string _idFieldName;
    private readonly Conventions _conventions;

    public string CollectionName { get; }

    internal IMongoCollection<T> Collection => null!; // For tests that check the collection namespace/name

    // Helper for seeding tests
    public void Seed(T entity)
    {
        var id = _idAccessor(entity) ?? throw new InvalidOperationException("Entity has no ID.");
        _data[id] = entity;

        var versionGetter = ConcurrencyVersionAccessor<T>.GetGetter(_conventions.ConcurrencyPropertyName);
        if (versionGetter != null)
        {
            _versions[id] = versionGetter(entity);
        }
    }

    public InMemoryDbSet(string collectionName, Conventions conventions)
    {
        CollectionName = collectionName;
        _conventions = conventions;
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
        _versions.Remove(id);
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
        // Mirror the deduplication semantics of DbSet.CommitAsync so that
        // in-memory tests see the same Remove→Add→Dirty ordering as production.

        var versionGetter = ConcurrencyVersionAccessor<T>.GetGetter(_conventions.ConcurrencyPropertyName);
        var versionSetter = ConcurrencyVersionAccessor<T>.GetSetter(_conventions.ConcurrencyPropertyName);

        // 1. Removals — collect and deduplicate via DocId
        foreach (var entity in removed)
        {
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (dedupeBuffer.Add(docId))
            {
                rawIdBuffer.Add(id);
                _data.Remove(id);
                _versions.Remove(id);
            }
        }
        foreach (var id in removedIds)
        {
            if (id == null) continue;
            var docId = DocId.From(id);
            if (dedupeBuffer.Add(docId))
            {
                rawIdBuffer.Add(id);
                _data.Remove(id);
                _versions.Remove(id);
            }
        }

        // 2. Added — skip if already removed (removed has priority)
        foreach (var entity in added)
        {
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
            {
                upsertBuffer[docId] = entity;
            }
        }
        foreach (var entry in upsertBuffer)
        {
            var entity = entry.Value;
            var id = _idAccessor(entity)!;
            _data[id] = entity;
            if (versionGetter != null)
            {
                _versions[id] = versionGetter(entity);
            }
            dedupeBuffer.Add(entry.Key); // prevent dirty from overwriting a fresh add
        }
        upsertBuffer.Clear();

        // 3. Updated / Dirty — skip if already removed or just added
        foreach (var entity in updated)
        {
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
                upsertBuffer[docId] = entity;
        }
        foreach (var entity in dirty)
        {
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
                upsertBuffer[docId] = entity;
        }

        var conflicts = new List<object>();
        foreach (var entry in upsertBuffer)
        {
            var entity = entry.Value;
            var id = _idAccessor(entity)!;

            if (versionGetter != null && versionSetter != null)
            {
                var expectedVersion = versionGetter(entity);
                if (!_versions.TryGetValue(id, out var actualVersion) || actualVersion != expectedVersion)
                {
                    conflicts.Add(id);
                    continue;
                }

                // Increment version for DB state
                var newVersion = actualVersion + 1;
                _versions[id] = newVersion;
                
                // DbSet increments the entity version in CommitAsync, so we match it here.
                versionSetter(entity, newVersion);
            }

            _data[id] = entity;
        }

        if (conflicts.Count > 0)
        {
            throw new ConcurrencyException($"Optimistic concurrency check failed for {conflicts.Count} entities in-memory.", conflicts);
        }

        await Task.Yield();
    }
}

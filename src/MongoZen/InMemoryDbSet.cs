using System.Collections;
using System.Linq.Expressions;
using MongoDB.Driver;
using MongoZen.Collections;
using SharpArena.Allocators;

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
    private readonly FilterUtils.FilterToLinqTranslator<T> _translator;

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

    /// <summary>
    /// For testing/seeding purposes. In production, use session.Store().
    /// </summary>
    public void Store(T entity) => Seed(entity);

    public InMemoryDbSet(string collectionName, Conventions conventions)
    {
        CollectionName = collectionName;
        _conventions = conventions;
        _idAccessor = EntityIdAccessor<T>.GetAccessor(conventions.IdConvention);
        _idFieldName = conventions.IdConvention.ResolveIdProperty<T>()?.Name ?? "_id";
        _translator = new FilterUtils.FilterToLinqTranslator<T>(conventions);
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
        var predicate = _translator.GetCompiled(filter);
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

    public void Dispose()
    {
        // No-op for InMemoryDbSet
    }

    async ValueTask IInternalDbSet<T>.CommitAsync(CommitContext<T> context, CancellationToken cancellationToken)
    {
        context.Buffers.UpsertBuffer.Clear();
        context.Buffers.RawIdBuffer.Clear();

        var dedupeBuffer = new ArenaHashSet<DocId>(context.Session.Arena, 128);
        var versionGetter = ConcurrencyVersionAccessor<T>.GetGetter(_conventions.ConcurrencyPropertyName);
        var versionSetter = ConcurrencyVersionAccessor<T>.GetSetter(_conventions.ConcurrencyPropertyName);

        // 1. Removals — collect and deduplicate via DocId
        foreach (var entity in context.Work.Removed)
        {
            if (entity == null) continue;
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (dedupeBuffer.Add(docId))
            {
                context.Buffers.RawIdBuffer.Add(id);
                _data.Remove(id);
                _versions.Remove(id);
            }
        }
        foreach (var id in context.Work.RemovedIds)
        {
            if (id == null) continue;
            var docId = DocId.From(id);
            if (dedupeBuffer.Add(docId))
            {
                context.Buffers.RawIdBuffer.Add(id);
                _data.Remove(id);
                _versions.Remove(id);
            }
        }

        // 2. Added — process directly with last-one-wins
        using var addedMap = new PooledDictionary<DocId, T>(16);
        foreach (var entity in context.Work.Added)
        {
            if (entity == null) continue;
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
            {
                addedMap[docId] = entity;
            }
        }

        foreach (var kvp in addedMap)
        {
            var id = _idAccessor(kvp.Value)!;
            _data[id] = kvp.Value;
            if (versionGetter != null)
            {
                _versions[id] = versionGetter(kvp.Value);
            }
            dedupeBuffer.Add(kvp.Key);
        }

        // 3. Updated / Dirty — collect to buffer
        foreach (var entity in context.Work.Updated)
        {
            if (entity == null) continue;
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
                context.Buffers.UpsertBuffer.AddOrUpdate(docId, (entity, false));
        }
        foreach (var entity in context.Work.Dirty)
        {
            if (entity == null) continue;
            var id = _idAccessor(entity);
            if (id == null) continue;
            var docId = DocId.From(id);
            if (!dedupeBuffer.Contains(docId))
                context.Buffers.UpsertBuffer.AddOrUpdate(docId, (entity, true));
        }

        var conflicts = new List<object>();
        foreach (var entry in context.Buffers.UpsertBuffer)
        {
            var entity = entry.Value.Entity;
            var id = _idAccessor(entity)!;

            if (versionGetter != null && versionSetter != null)
            {
                var expectedVersion = versionGetter(entity);
                if (!_versions.TryGetValue(id, out var actualVersion) || actualVersion != expectedVersion)
                {
                    conflicts.Add(id);
                    continue;
                }

                var newVersion = actualVersion + 1;
                _versions[id] = newVersion;
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

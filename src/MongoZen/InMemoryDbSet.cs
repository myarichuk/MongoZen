using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.FilterUtils;

namespace MongoZen;

public class InMemoryDbSet<T> : IDbSet<T>, IInternalDbSet<T> where T : class
{
    private readonly List<T> _items;
    private readonly Func<T, object?> _idAccessor;
    private readonly Conventions _conventions;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string CollectionName { get; }

    private readonly FilterToLinqTranslator<T> _translator =
        FilterToLinqTranslatorFactory.Create<T>();

    internal IList<T> Collection => _items;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    /// <param name="items">Initial items for the in-memory collection</param>
    /// <param name="conventions">Conventions to use for ID mapping</param>
    /// <param name="collectionName">Optional name for the collection</param>
    public InMemoryDbSet(IEnumerable<T> items, Conventions? conventions = null, string? collectionName = null)
    {
        _items = [..items];
        _conventions = conventions ?? new();
        _idAccessor = EntityIdAccessor<T>.GetAccessor(_conventions.IdConvention);
        CollectionName = collectionName ?? typeof(T).Name;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    public InMemoryDbSet(Conventions? conventions = null)
        : this([], conventions, null)
    {
    }

    public async ValueTask<T?> LoadAsync(object id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var item = _items.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
            return item != null ? Clone(item) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public IDbSet<T> Include(Expression<Func<T, object?>> path)
    {
        return this;
    }

    public IDbSet<T> Include<TInclude>(Expression<Func<T, object?>> path) where TInclude : class
    {
        return this;
    }

    public async ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var compiled = _translator.GetCompiled(filter);
            var result = _items.Where(compiled).Select(Clone).ToList();
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var result = _items.AsQueryable()
                .Where(filter)
                .AsEnumerable()
                .Select(Clone)
                .ToList();
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    async ValueTask IInternalDbSet<T>.CommitAsync(IEnumerable<T> added, IEnumerable<T> removed, IEnumerable<object> removedIds, IEnumerable<T> updated, Dictionary<object, T> upsertBuffer, HashSet<object> removedIdBuffer, List<WriteModel<T>> modelBuffer, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        // Simulate atomic update
        await _lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var entity in added)
            {
                var id = _idAccessor(entity);
                if (id != null)
                {
                    var existing = _items.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
                    if (existing != null) _items.Remove(existing);
                }
                _items.Add(Clone(entity));
            }

            foreach (var entity in removed)
            {
                var id = _idAccessor(entity);
                if (id != null)
                {
                    var existing = _items.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
                    if (existing != null) _items.Remove(existing);
                }
            }

            foreach (var id in removedIds)
            {
                var existing = _items.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
                if (existing != null) _items.Remove(existing);
            }

            foreach (var entity in updated)
            {
                var id = _idAccessor(entity);
                if (id != null)
                {
                    var existing = _items.FirstOrDefault(x => _idAccessor(x)?.Equals(id) == true);
                    if (existing != null)
                    {
                        _items.Remove(existing);
                        _items.Add(Clone(entity));
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static T Clone(T source)
    {
        var bson = source.ToBson();
        return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(bson);
    }
}

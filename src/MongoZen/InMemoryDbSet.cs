using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoZen.FilterUtils;

namespace MongoZen;

public class InMemoryDbSet<T> : IDbSet<T> where T : class
{
    private readonly List<T> _items;

    public string CollectionName { get; }

    private readonly FilterToLinqTranslator<T> _translator =
        FilterToLinqTranslatorFactory.Create<T>();

    internal IList<T> Collection => _items;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    /// <param name="items">Initial items for the in-memory collection</param>
    /// <param name="collectionName">Optional name for the collection</param>
    public InMemoryDbSet(IEnumerable<T> items, string? collectionName = null)
    {
        _items = [..items];
        CollectionName = collectionName ?? typeof(T).Name;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    public InMemoryDbSet()
        : this([], null)
    {
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default)
    {
        var expr = _translator.Translate(filter);
        var result = _items.AsQueryable().Where(expr).Select(Clone).ToList();
        return ValueTask.FromResult((IEnumerable<T>)result);
    }

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default)
    {
        var result = _items.AsQueryable()
            .Where(filter)
            .AsEnumerable()
            .Select(Clone)
            .ToList();

        return ValueTask.FromResult<IEnumerable<T>>(result);
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session, CancellationToken cancellationToken = default)
        => QueryAsync(filter, cancellationToken);

    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Use QueryAsync for asynchronous execution instead of synchronous LINQ evaluation.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _items.AsQueryable().ElementType;

    public Expression Expression => _items.AsQueryable().Expression;

    public IQueryProvider Provider => _items.AsQueryable().Provider;

    private static T Clone(T source)
    {
        var bson = source.ToBson();
        return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(bson);
    }
}

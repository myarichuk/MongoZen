using System.Collections;
using System.Reflection;
using System.Text.Json;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoZen.FilterUtils;

namespace MongoZen;

public class InMemoryDbSet<T> : IDbSet<T>
{
    private readonly List<T> _items;

    private readonly FilterToLinqTranslator<T> _translator =
        FilterToLinqTranslatorFactory.Create<T>();

    internal IList<T> Collection => _items;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    /// <param name="items">Initial items for the in-memory collection</param>
    public InMemoryDbSet(IEnumerable<T> items) => _items = [..items];

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    public InMemoryDbSet()
        : this([])
    {
    }

    private static T Clone(T source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter)
    {
        var expr = _translator.Translate(filter);
        var result = _items.AsQueryable().Where(expr).Select(Clone).ToList();
        return ValueTask.FromResult((IEnumerable<T>)result);
    }

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter)
    {
        var result = _items.AsQueryable()
            .Where(filter)
            .AsEnumerable()
            .Select(Clone)
            .ToList();

        return ValueTask.FromResult<IEnumerable<T>>(result);
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter, IClientSessionHandle session)
        => QueryAsync(filter);

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter, IClientSessionHandle session)
        => QueryAsync(filter);

    public IEnumerator<T> GetEnumerator() => _items.AsQueryable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _items.AsQueryable().ElementType;

    public Expression Expression => _items.AsQueryable().Expression;

    public IQueryProvider Provider => _items.AsQueryable().Provider;
}

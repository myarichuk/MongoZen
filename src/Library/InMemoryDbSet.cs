using System.Collections;
using Library.FilterUtils;
using MongoDB.Driver;

namespace Library;

public class InMemoryDbSet<T> : IDbSet<T>
{
    private readonly List<T> _items;

    private readonly FilterToLinqTranslator<T> _translator =
        FilterToLinqTranslatorFactory.Create<T>();

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    /// <param name="items">Initial items for the in-memory collection</param>
    public InMemoryDbSet(IEnumerable<T> items) => _items = [.. items];

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    public InMemoryDbSet()
        : this([])
    {
    }

    public ValueTask<IEnumerable<T>> QueryAsync(FilterDefinition<T> filter)
    {
        var expr = _translator.Translate(filter);
        return ValueTask.FromResult(_items.AsQueryable().Where(expr).AsEnumerable());
    }

    public ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> filter) =>
        ValueTask.FromResult(_items.AsQueryable().Where(filter).AsEnumerable());

    public void Add(T entity) => _items.Add(entity);

    public void Remove(T entity) => _items.Remove(entity);

    public IEnumerator<T> GetEnumerator() => _items.AsQueryable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _items.AsQueryable().ElementType;

    public Expression Expression => _items.AsQueryable().Expression;

    public IQueryProvider Provider => _items.AsQueryable().Provider;
}
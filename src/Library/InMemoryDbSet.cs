using System.Collections;
using System.Reflection;
using System.Text.Json;
using Library.FilterUtils;
using MongoDB.Bson.Serialization.Attributes;
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
    public InMemoryDbSet(IEnumerable<T> items) => _items = new List<T>(items);

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDbSet{T}"/> class.
    /// </summary>
    public InMemoryDbSet()
        : this(Enumerable.Empty<T>())
    {
    }

    private static PropertyInfo? GetIdProperty()
    {
        return typeof(T).GetProperties().FirstOrDefault(p =>
            p.Name == "Id" || p.GetCustomAttributes(typeof(BsonIdAttribute), true).Any());
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
        var result = _items.AsQueryable().Where(filter).Select(Clone).ToList();
        return ValueTask.FromResult((IEnumerable<T>)result);
    }

    public void Add(T entity) => _items.Add(entity);

    public void Remove(T entity)
    {
        var idProp = GetIdProperty();
        if (idProp != null)
        {
            var id = idProp.GetValue(entity);
            _items.RemoveAll(e => Equals(idProp.GetValue(e), id));
        }
        else
        {
            _items.Remove(entity);
        }
    }

    public void RemoveById(object id)
    {
        var idProp = GetIdProperty() ?? throw new InvalidOperationException("No Id or [BsonId] property found");
        _items.RemoveAll(e => Equals(idProp.GetValue(e), id));
    }

    public IEnumerator<T> GetEnumerator() => _items.AsQueryable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Type ElementType => _items.AsQueryable().ElementType;

    public Expression Expression => _items.AsQueryable().Expression;

    public IQueryProvider Provider => _items.AsQueryable().Provider;
}
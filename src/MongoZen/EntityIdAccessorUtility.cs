using System.Collections.Concurrent;
using System.Reflection;

namespace MongoZen;

/// <summary>
/// Entry point for accessing entity IDs with high-performance caching.
/// </summary>
public static class EntityIdAccessor
{
    private static readonly ConcurrentDictionary<Type, IIdGetter> _getterCache = new();

    public static object? GetId(object entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var type = entity.GetType();
        var getter = _getterCache.GetOrAdd(type, t =>
        {
            var dispatcherType = typeof(IdGetter<>).MakeGenericType(t);
            return (IIdGetter)Activator.CreateInstance(dispatcherType)!;
        });

        return getter.GetId(entity);
    }

    public static DocId GetDocId(object entity)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var type = entity.GetType();
        var getter = _getterCache.GetOrAdd(type, t =>
        {
            var dispatcherType = typeof(IdGetter<>).MakeGenericType(t);
            return (IIdGetter)Activator.CreateInstance(dispatcherType)!;
        });

        return getter.GetDocId(entity);
    }

    private interface IIdGetter
    {
        object? GetId(object entity);
        DocId GetDocId(object entity);
    }

    private class IdGetter<T> : IIdGetter
    {
        private static readonly Func<T, object?> _getter;
        private static readonly Func<T, DocId> _docIdGetter;

        static IdGetter()
        {
            var convention = DefaultIdConvention.Instance;
            _getter = EntityIdAccessor<T>.GetAccessor(convention);
            _docIdGetter = EntityIdAccessor<T>.GetDocIdAccessor(convention);
        }

        public object? GetId(object entity) => _getter((T)entity);
        public DocId GetDocId(object entity) => _docIdGetter((T)entity);
    }
}

public static class EntityIdAccessorUtility<T>
{
    public static readonly bool HasETag;
    public static readonly bool HasSettableStringId;
    private static readonly Action<T, Guid> _etagSetter;
    private static readonly Action<T, string> _idSetter;

    static EntityIdAccessorUtility()
    {
        var props = typeof(T).GetProperties();
        var etagProp = props.FirstOrDefault(p => p.GetCustomAttribute<ConcurrencyCheckAttribute>() != null)
                   ?? typeof(T).GetProperty("ETag")
                   ?? typeof(T).GetProperty("_etag")
                   ?? typeof(T).GetProperty("Version");

        HasETag = etagProp != null && etagProp.PropertyType == typeof(Guid);
        if (HasETag)
        {
            var entityParam = System.Linq.Expressions.Expression.Parameter(typeof(T), "entity");
            var etagParam = System.Linq.Expressions.Expression.Parameter(typeof(Guid), "etag");
            var assign = System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Property(entityParam, etagProp!), etagParam);
            _etagSetter = System.Linq.Expressions.Expression.Lambda<Action<T, Guid>>(assign, entityParam, etagParam).Compile();
        }
        else
        {
            _etagSetter = (_, _) => { };
        }

        var convention = DefaultIdConvention.Instance;
        var idProp = convention.ResolveIdProperty<T>();
        HasSettableStringId = idProp != null && idProp.PropertyType == typeof(string) && idProp.CanWrite;
        if (HasSettableStringId)
        {
            var entityParam = System.Linq.Expressions.Expression.Parameter(typeof(T), "entity");
            var idParam = System.Linq.Expressions.Expression.Parameter(typeof(string), "id");
            var assign = System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Property(entityParam, idProp!), idParam);
            _idSetter = System.Linq.Expressions.Expression.Lambda<Action<T, string>>(assign, entityParam, idParam).Compile();
        }
        else
        {
            _idSetter = (_, _) => { };
        }
    }

    public static void SetETag(T entity, Guid etag) => _etagSetter(entity, etag);
    public static void SetId(T entity, string id) => _idSetter(entity, id);
}

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
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
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
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
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

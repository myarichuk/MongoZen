using System.Collections.Concurrent;
using System.Reflection;

namespace MongoZen;

/// <summary>
/// Entry point for accessing entity IDs with high-performance caching.
/// </summary>
public static class EntityIdAccessor
{
    private static readonly ConcurrentDictionary<Type, Delegate> _getterCache = new();
    private static readonly ConcurrentDictionary<Type, Delegate> _docIdGetterCache = new();

    public static object? GetId(object entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
        var type = entity.GetType();
        var getter = _getterCache.GetOrAdd(type, t =>
        {
            var accessorType = typeof(EntityIdAccessor<>).MakeGenericType(t);
            return (Delegate)accessorType.GetMethod("GetAccessor", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [DefaultIdConvention.Instance])!;
        });

        return getter.DynamicInvoke(entity);
    }

    public static DocId GetDocId(object entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
        var type = entity.GetType();
        var getter = _docIdGetterCache.GetOrAdd(type, t =>
        {
            var accessorType = typeof(EntityIdAccessor<>).MakeGenericType(t);
            return (Delegate)accessorType.GetMethod("GetDocIdAccessor", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [DefaultIdConvention.Instance])!;
        });

        return (DocId)getter.DynamicInvoke(entity)!;
    }
}

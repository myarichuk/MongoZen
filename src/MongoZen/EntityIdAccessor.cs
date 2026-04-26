using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
#nullable enable

namespace MongoZen;

/// <summary>
/// Caches compiled Id‑accessor delegates per (TEntity, IIdConvention‑type) pair.
/// </summary>
internal static class EntityIdAccessor<TEntity>
{
    private static readonly ConcurrentDictionary<IIdConvention, Lazy<Func<TEntity, object?>>> GetterCache = new();
    private static readonly ConcurrentDictionary<IIdConvention, Lazy<Action<TEntity, object?>>> SetterCache = new();

    /// <summary>
    /// Returns the compiled Id‑accessor for the given convention.
    /// </summary>
    internal static Func<TEntity, object?> GetAccessor(IIdConvention convention) =>
        GetterCache.GetOrAdd(
            convention,
            c => new Lazy<Func<TEntity, object?>>(() => BuildGetter(c))).Value;

    /// <summary>
    /// Returns the compiled Id‑setter for the given convention.
    /// </summary>
    internal static Action<TEntity, object?> GetSetter(IIdConvention convention) =>
        SetterCache.GetOrAdd(
            convention,
            c => new Lazy<Action<TEntity, object?>>(() => BuildSetter(c))).Value;

    private static Func<TEntity, object?> BuildGetter(IIdConvention convention)
    {
        var prop = convention.ResolveIdProperty<TEntity>();

        if (prop is null)
        {
            return _ => null;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, prop);
        var convertToObject = Expression.Convert(propertyAccess, typeof(object));

        return Expression.Lambda<Func<TEntity, object?>>(convertToObject, parameter).Compile();
    }

    private static Action<TEntity, object?> BuildSetter(IIdConvention convention)
    {
        var prop = convention.ResolveIdProperty<TEntity>();

        if (prop is null || !prop.CanWrite)
        {
            return (e, v) => { };
        }

        var entityParameter = Expression.Parameter(typeof(TEntity), "entity");
        var valueParameter = Expression.Parameter(typeof(object), "value");
        
        var propertyAccess = Expression.Property(entityParameter, prop);
        var convertToPropertyType = Expression.Convert(valueParameter, prop.PropertyType);
        
        var assign = Expression.Assign(propertyAccess, convertToPropertyType);

        return Expression.Lambda<Action<TEntity, object?>>(assign, entityParameter, valueParameter).Compile();
    }
}

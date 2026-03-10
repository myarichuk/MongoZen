using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
#nullable enable

namespace MongoZen;

/// <summary>
/// Caches compiled Id‑accessor delegates per (TEntity, IIdConvention‑type) pair.
/// Thread‑safe: <see cref="ConcurrentDictionary{TKey,TValue}"/> handles concurrent reads/writes,
/// and <see cref="Lazy{T}"/> ensures each delegate is compiled exactly once.
/// </summary>
internal static class EntityIdAccessor<TEntity>
{
    private static readonly ConcurrentDictionary<Type, Lazy<Func<TEntity, object?>>> Cache = new();

    /// <summary>
    /// Returns the compiled Id‑accessor for the given convention, building it once and caching it.
    /// </summary>
    /// <returns>The accessor, or null if no Id‑property was found.</returns>
    internal static Func<TEntity, object?> GetAccessor(IIdConvention convention) =>
        Cache.GetOrAdd(
            convention.GetType(),
            _ => new Lazy<Func<TEntity, object?>>(() => Build(convention))).Value;

    private static Func<TEntity, object?> Build(IIdConvention convention)
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
}

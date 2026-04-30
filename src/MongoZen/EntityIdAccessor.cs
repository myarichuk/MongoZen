using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
#nullable enable

namespace MongoZen;



public interface IIdConvention
{
    PropertyInfo? ResolveIdProperty<TEntity>();
}

/// <summary>
/// Caches compiled Id‑accessor delegates per (TEntity, IIdConvention‑type) pair.
/// </summary>
internal static class EntityIdAccessor<TEntity>
{
    private static readonly ConcurrentDictionary<IIdConvention, Func<TEntity, object?>> GetterCache = new();
    private static readonly ConcurrentDictionary<IIdConvention, Action<TEntity, object?>> SetterCache = new();

    /// <summary>
    /// Returns the compiled Id‑accessor for the given convention.
    /// </summary>
    internal static Func<TEntity, object?> GetAccessor(IIdConvention convention) =>
        GetterCache.GetOrAdd(convention, BuildGetter);

    /// <summary>
    /// Returns the compiled Id‑setter for the given convention.
    /// </summary>
    internal static Action<TEntity, object?> GetSetter(IIdConvention convention) =>
        SetterCache.GetOrAdd(convention, BuildSetter);

    private static readonly ConcurrentDictionary<IIdConvention, Func<TEntity, DocId>> DocIdCache = new();

    /// <summary>
    /// Returns a non-boxing compiled Id‑accessor that returns a DocId.
    /// </summary>
    internal static Func<TEntity, DocId> GetDocIdAccessor(IIdConvention convention) =>
        DocIdCache.GetOrAdd(convention, BuildDocIdGetter);

    private static Func<TEntity, DocId> BuildDocIdGetter(IIdConvention convention)
    {
        var prop = convention.ResolveIdProperty<TEntity>();
        if (prop is null) return _ => default;

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, prop);
        
        MethodInfo? method;
        Expression? call;

        if (prop.PropertyType == typeof(int))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromInt32), [typeof(int)]);
            call = Expression.Call(method!, propertyAccess);
        }
        else if (prop.PropertyType == typeof(long))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromInt64), [typeof(long)]);
            call = Expression.Call(method!, propertyAccess);
        }
        else if (prop.PropertyType == typeof(Guid))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromGuid), [typeof(Guid)]);
            call = Expression.Call(method!, propertyAccess);
        }
        else if (prop.PropertyType == typeof(ObjectId))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromObjectId), [typeof(ObjectId)]);
            call = Expression.Call(method!, propertyAccess);
        }
        else if (prop.PropertyType == typeof(string))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromString), [typeof(string)]);
            call = Expression.Call(method!, propertyAccess);
        }
        else if (typeof(IDocIdHashable).IsAssignableFrom(prop.PropertyType))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromHashable), [typeof(IDocIdHashable)]);
            call = Expression.Call(method!, Expression.Convert(propertyAccess, typeof(IDocIdHashable)));
        }
        else
        {
            // Fallback to boxing for unknown types
            method = typeof(DocId).GetMethod(nameof(DocId.FromBson), [typeof(object)]);
            call = Expression.Call(method!, Expression.Convert(propertyAccess, typeof(object)));
        }

        return Expression.Lambda<Func<TEntity, DocId>>(call, parameter).Compile();
    }

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
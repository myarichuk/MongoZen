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
        if (prop is null)
        {
            return _ => default;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, prop);
        
        var type = prop.PropertyType;
        bool isNullable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        var underlyingType = isNullable ? Nullable.GetUnderlyingType(type)! : type;

        MethodInfo? method = null;
        if (underlyingType == typeof(int))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromInt32), [typeof(int)]);
        }
        else if (underlyingType == typeof(long))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromInt64), [typeof(long)]);
        }
        else if (underlyingType == typeof(Guid))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromGuid), [typeof(Guid)]);
        }
        else if (underlyingType == typeof(ObjectId))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromObjectId), [typeof(ObjectId)]);
        }
        else if (underlyingType == typeof(string))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromString), [typeof(string)]);
        }
        else if (typeof(IDocIdHashable).IsAssignableFrom(underlyingType))
        {
            method = typeof(DocId).GetMethod(nameof(DocId.FromHashable), [typeof(IDocIdHashable)]);
        }

        if (method != null)
        {
            if (isNullable)
            {
                var hasValueProp = type.GetProperty("HasValue")!;
                var valueProp = type.GetProperty("Value")!;
                var call = Expression.Condition(
                    Expression.Property(propertyAccess, hasValueProp),
                    Expression.Call(method, Expression.Property(propertyAccess, valueProp)),
                    Expression.Constant(default(DocId))
                );
                return Expression.Lambda<Func<TEntity, DocId>>(call, parameter).Compile();
            }
            else
            {
                Expression argExpr = method.GetParameters()[0].ParameterType == typeof(IDocIdHashable) 
                    ? Expression.Convert(propertyAccess, typeof(IDocIdHashable)) 
                    : propertyAccess;
                var call = Expression.Call(method, argExpr);
                return Expression.Lambda<Func<TEntity, DocId>>(call, parameter).Compile();
            }
        }

        // Fallback to boxing for unknown types
        var fallbackMethod = typeof(DocId).GetMethod(nameof(DocId.FromBson), [typeof(object)])!;
        var fallbackCall = Expression.Call(fallbackMethod, Expression.Convert(propertyAccess, typeof(object)));
        return Expression.Lambda<Func<TEntity, DocId>>(fallbackCall, parameter).Compile();
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
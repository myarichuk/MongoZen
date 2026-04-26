using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson.Serialization;
#nullable enable

namespace MongoZen;

internal static class ConcurrencyVersionAccessor<TEntity>
{
    private static readonly ConcurrentDictionary<string, Lazy<Func<TEntity, long>?>> GetterCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<Action<TEntity, long>?>> SetterCache = new();
    private static readonly ConcurrentDictionary<string, string?> ElementNameCache = new();

    internal static Func<TEntity, long>? GetGetter(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;

        return GetterCache.GetOrAdd(propertyName, name => new Lazy<Func<TEntity, long>?>(() => BuildGetter(name))).Value;
    }

    internal static Action<TEntity, long>? GetSetter(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;

        return SetterCache.GetOrAdd(propertyName, name => new Lazy<Action<TEntity, long>?>(() => BuildSetter(name))).Value;
    }

    internal static string? GetElementName(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;

        return ElementNameCache.GetOrAdd(propertyName, name => {
            var prop = typeof(TEntity).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null) return null;

            try 
            {
                var classMap = BsonClassMap.LookupClassMap(typeof(TEntity));
                var memberMap = classMap.GetMemberMap(prop.Name);
                return memberMap?.ElementName ?? prop.Name;
            }
            catch 
            {
                return prop.Name;
            }
        });
    }

    private static Func<TEntity, long>? BuildGetter(string propertyName)
    {
        var prop = typeof(TEntity).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || !prop.CanRead) return null;

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, prop);
        
        // Ensure we can convert to long
        Expression body = propertyAccess;
        if (prop.PropertyType != typeof(long))
        {
            body = Expression.Convert(propertyAccess, typeof(long));
        }

        return Expression.Lambda<Func<TEntity, long>>(body, parameter).Compile();
    }

    private static Action<TEntity, long>? BuildSetter(string propertyName)
    {
        var prop = typeof(TEntity).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || !prop.CanWrite) return null;

        var entityParameter = Expression.Parameter(typeof(TEntity), "entity");
        var valueParameter = Expression.Parameter(typeof(long), "value");
        
        var propertyAccess = Expression.Property(entityParameter, prop);
        var convertToPropertyType = Expression.Convert(valueParameter, prop.PropertyType);
        
        var assign = Expression.Assign(propertyAccess, convertToPropertyType);

        return Expression.Lambda<Action<TEntity, long>>(assign, entityParameter, valueParameter).Compile();
    }
}

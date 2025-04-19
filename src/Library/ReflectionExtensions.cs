// ReSharper disable ComplexConditionExpression
// ReSharper disable TooManyDeclarations
namespace Library;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson.Serialization.Attributes; // make sure to reference MongoDB.Bson

internal static class ReflectionExtensions
{
    // Cache for the accessor functions, one per type.
    private static readonly ConcurrentDictionary<Type, Func<object, object?>> IdAccessorCache = new();

    /// <summary>
    /// Tries to get the "Id" property value from an object. It first checks for [BsonId] attribute;
    /// if not found, it then checks for a property named "Id".
    /// </summary>
    /// <param name="obj">The object to inspect.</param>
    /// <param name="id">The retrieved id value, if found; otherwise, null.</param>
    /// <returns>true if an appropriate property was found and returned, false otherwise.</returns>
    public static bool TryGetId(this object? obj, out object? id)
    {
        id = null;
        if (obj == null)
        {
            return false;
        }

        try
        {
            id = obj.GetId();
            return id != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the "Id" value from an object. Checks for [BsonId] or a property named "Id".
    /// Throws if the ID is not found.
    /// </summary>
    /// <param name="obj">The object to inspect.</param>
    /// <returns>The value of the ID property.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no ID property is found or value is null.</exception>
    public static object? GetId(this object obj)
    {
        if (obj == null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        var accessor = IdAccessorCache.GetOrAdd(obj.GetType(), CreateIdAccessor);
        var id = accessor(obj);
        return id;
    }

    /// <summary>
    /// Creates a fast accessor function for an id property on the given type.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns>A compiled function to fetch the id property or null if not found.</returns>
    private static Func<object, object?> CreateIdAccessor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // try finding a property that has the [BsonId] attribute.
        var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                           .FirstOrDefault(p => p.CanRead &&
                                                  p.GetCustomAttributes(typeof(BsonIdAttribute), true).Any()) ??
                       type.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);

        // if there's no valid property, cache a null to avoid repeated reflection.
        if (property == null)
        {
            return _ => null;
        }

        // Build a fast getter using expression trees.
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var castInstance = Expression.Convert(instanceParam, type);
        var propertyAccess = Expression.Property(castInstance, property);
        var convertResult = Expression.Convert(propertyAccess, typeof(object));

        var lambda = Expression.Lambda<Func<object, object?>>(convertResult, instanceParam);
        return lambda.Compile();
    }
}

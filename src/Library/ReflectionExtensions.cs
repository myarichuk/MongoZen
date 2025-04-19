using System.Reflection;
using System.Runtime.CompilerServices;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable ComplexConditionExpression
namespace Library;

internal static class EntityIdAccessor<TEntity>
{
    internal static readonly Func<TEntity, object?> Get = Build();

    private static Func<TEntity, object?> Build()
    {
        var prop = typeof(TEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                       .FirstOrDefault(p => p.IsDefined(typeof(BsonIdAttribute), true) && p.CanRead)
                   ?? typeof(TEntity).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);

        if (prop is null)
        {
            return _ => null;
        }

        // typed delegate: Func<T, TValue>
        var typed = prop.GetGetMethod()!
            .CreateDelegate(typeof(Func<,>)
                .MakeGenericType(typeof(TEntity), prop.PropertyType));

        // wrapper boxes only if TValue is a struct
        return entity => ((Delegate)typed).DynamicInvoke(entity);
    }
}

internal static class ReflectionExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetId<TEntity>(this TEntity? obj, out object? id)
    {
        if (obj is null)
        {
            id = null;
            return false;
        }

        id = EntityIdAccessor<TEntity>.Get(obj);
        return id is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object GetId<TEntity>(this TEntity obj)
    {
        var id = EntityIdAccessor<TEntity>.Get(obj);
        return id ?? throw new InvalidOperationException(
            $"Object of type {obj.GetType().Name} doesn’t expose an Id.");
    }
}
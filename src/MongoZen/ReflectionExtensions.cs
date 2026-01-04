using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

// ReSharper disable ComplexConditionExpression
namespace MongoZen;

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
    public static object GetId<TEntity>([DisallowNull] this TEntity obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var id = EntityIdAccessor<TEntity>.Get(obj);
        return id ?? throw new InvalidOperationException(
            $"Object of type {obj.GetType().Name} doesn't expose an Id.");
    }
}

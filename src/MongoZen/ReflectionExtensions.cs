using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
#nullable enable

// ReSharper disable ComplexConditionExpression
namespace MongoZen;

internal static class ReflectionExtensions
{
    /// <summary>
    /// Tries to extract the Id from an entity using the given accessor delegate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetId<TEntity>(this TEntity? obj, Func<TEntity, object?> idAccessor, out object? id)
    {
        if (obj is null)
        {
            id = null;
            return false;
        }

        id = idAccessor(obj);
        return id is not null;
    }

    /// <summary>
    /// Extracts the Id as a DocId from an entity using the given accessor delegate, throwing if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DocId GetDocId<TEntity>([DisallowNull] this TEntity obj, Func<TEntity, DocId> idAccessor)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var id = idAccessor(obj);
        if (id == default) throw new InvalidOperationException(
            $"Object of type {obj.GetType().Name} doesn't expose an Id.");
        return id;
    }

    /// <summary>
    /// Extracts the Id from an entity using the given accessor delegate, throwing if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object GetId<TEntity>([DisallowNull] this TEntity obj, Func<TEntity, object?> idAccessor)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var id = idAccessor(obj);
        return id ?? throw new InvalidOperationException(
            $"Object of type {obj.GetType().Name} doesn't expose an Id.");
    }
}

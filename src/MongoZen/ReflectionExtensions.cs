using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable ComplexConditionExpression
namespace MongoZen;

internal sealed class DefaultIdConvention : IIdConvention
{
    public PropertyInfo? ResolveIdProperty<TEntity>()
    {
        var type = typeof(TEntity);
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                   .FirstOrDefault(p => p.IsDefined(typeof(BsonIdAttribute), true) && p.CanRead)
            ?? type.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
    }
}

public interface IIdConvention
{
    /// <summary>
    /// Resolves the property that represents the entity identifier.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The property info for the identifier, if found.</returns>
    PropertyInfo? ResolveIdProperty<TEntity>();
}

internal static class GlobalIdConventionProvider
{
    public static IIdConvention Convention { get; private set; } = new DefaultIdConvention();
}

internal static class EntityIdAccessor<TEntity>
{
    internal static Func<TEntity, object?> Get { get; private set; } = Build();

    internal static void SetConvention(IIdConvention convention) => Get = Build(convention);

    private static Func<TEntity, object?> Build(IIdConvention? customResolver = null)
    {
        var prop = customResolver != null ?
            customResolver.ResolveIdProperty<TEntity>() :
            GlobalIdConventionProvider.Convention.ResolveIdProperty<TEntity>();

        if (prop is null)
        {
            return _ => null;
        }

        var getter = prop.GetGetMethod()!
            .CreateDelegate(typeof(Func<,>)
                .MakeGenericType(typeof(TEntity), prop.PropertyType));

        return entity => getter.DynamicInvoke(entity);
    }
}

internal static class ReflectionExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// Tries to read the entity identifier using the configured convention.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="obj">The entity instance to read.</param>
    /// <param name="id">When this method returns, contains the identifier if available.</param>
    /// <returns><see langword="true"/> when an identifier is found; otherwise <see langword="false"/>.</returns>
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
    /// <summary>
    /// Gets the identifier for the specified entity or throws if none is present.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="obj">The entity instance to read.</param>
    /// <returns>The identifier value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="obj"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no identifier is present.</exception>
    public static object GetId<TEntity>([DisallowNull] this TEntity obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var id = EntityIdAccessor<TEntity>.Get(obj);
        return id ?? throw new InvalidOperationException(
            $"Object of type {obj.GetType().Name} doesn't expose an Id.");
    }
}

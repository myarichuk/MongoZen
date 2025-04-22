using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable ComplexConditionExpression
namespace MongoFlow;

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
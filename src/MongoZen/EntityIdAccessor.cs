using System.Reflection;

namespace MongoZen;

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

using System.Linq.Expressions;
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

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, prop);
        var convertToObject = Expression.Convert(propertyAccess, typeof(object));

        return Expression.Lambda<Func<TEntity, object?>>(convertToObject, parameter).Compile();
    }
}

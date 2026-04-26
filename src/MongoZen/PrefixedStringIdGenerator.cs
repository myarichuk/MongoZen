using System;

namespace MongoZen;

public record PrefixedStringIdGenerator : IIdGenerator
{
    public void AssignId<TEntity>(TEntity entity, string collectionName, IIdConvention convention) where TEntity : class
    {
        var idProp = convention.ResolveIdProperty<TEntity>();
        if (idProp == null)
        {
            throw new InvalidOperationException($"Could not resolve Id property for entity type {typeof(TEntity).Name} using convention {convention.GetType().Name}");
        }

        var setter = EntityIdAccessor<TEntity>.GetSetter(convention);
        var getter = EntityIdAccessor<TEntity>.GetAccessor(convention);

        var currentId = getter(entity);
        if (currentId == null || (currentId is string s && string.IsNullOrEmpty(s)))
        {
            var newId = $"{collectionName}/{Guid.NewGuid()}";
            setter(entity, newId);
        }
    }
}

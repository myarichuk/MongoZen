using System.Reflection;

namespace MongoZen;

/// <summary>
/// Entry point for accessing entity IDs.
/// </summary>
public static class EntityIdAccessor
{
    public static object? GetId(object entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
        var type = entity.GetType();
        var convention = DefaultIdConvention.Instance;
        
        // Use reflection to call the generic EntityIdAccessor<TEntity>
        var accessorType = typeof(EntityIdAccessor<>).MakeGenericType(type);
        var getter = accessorType.GetMethod("GetAccessor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [convention]);

        return getter?.GetType().GetMethod("Invoke")?.Invoke(getter, [entity]);
    }

    public static DocId GetDocId(object entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        
        var type = entity.GetType();
        var convention = DefaultIdConvention.Instance;
        
        var accessorType = typeof(EntityIdAccessor<>).MakeGenericType(type);
        var getter = accessorType.GetMethod("GetDocIdAccessor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [convention]);

        return (DocId)(getter?.GetType().GetMethod("Invoke")?.Invoke(getter, [entity]) ?? default(DocId));
    }
}

using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen;

internal sealed record DefaultIdConvention : IIdConvention
{
    public PropertyInfo? ResolveIdProperty<TEntity>()
    {
        var type = typeof(TEntity);
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        
        return props.FirstOrDefault(p => p.IsDefined(typeof(BsonIdAttribute), true) && p.CanRead)
            ?? props.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase) && p.CanRead);
    }
}

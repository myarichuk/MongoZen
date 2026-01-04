using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;

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

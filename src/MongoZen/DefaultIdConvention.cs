using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoZen;

internal sealed class DefaultIdConvention : IIdConvention
{
    public static readonly DefaultIdConvention Instance = new();

    public PropertyInfo? ResolveIdProperty<TEntity>() =>
        Cache<TEntity>.Property;

    private static class Cache<TEntity>
    {
        // ReSharper disable once StaticMemberInGenericType
        public static readonly PropertyInfo? Property = Resolve();

        private static PropertyInfo? Resolve()
        {
            var type = typeof(TEntity);

            var props = type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            return props.FirstOrDefault(p =>
                       p.CanRead &&
                       p.IsDefined(typeof(BsonIdAttribute), true))
                   ?? props.FirstOrDefault(p =>
                       p.CanRead &&
                       string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase));
        }
    }
}
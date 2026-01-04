using System.Reflection;

namespace MongoZen;

public interface IIdConvention
{
    PropertyInfo? ResolveIdProperty<TEntity>();
}

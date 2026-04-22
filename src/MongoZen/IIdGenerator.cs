namespace MongoZen;

public interface IIdGenerator
{
    void AssignId<TEntity>(TEntity entity, string collectionName, IIdConvention convention) where TEntity : class;
}
